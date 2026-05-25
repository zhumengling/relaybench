using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RelayBench.Services.Infrastructure;

namespace RelayBench.Services;

internal sealed partial class TransparentProxyResponseCacheService
{
    private const int ResponseCacheMaxEntries = 512;
    private const int ModelListCacheMaxEntries = 32;

    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ConcurrentDictionary<string, TransparentProxyCachedResponse> _responses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TransparentProxyCachedModelsList> _modelLists = new(StringComparer.Ordinal);
    private readonly object _databaseSyncRoot = new();
    private readonly string _databasePath;
    private long _hits;
    private long _misses;
    private long _stores;
    private long _evictions;
    private long _modelListHits;
    private long _modelListMisses;
    private long _leaseWaits;

    public TransparentProxyResponseCacheService(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? RelayBenchPaths.TransparentProxyCachePath
            : databasePath;
    }

    public int Count => _responses.Count + _modelLists.Count;

    public TransparentProxyCacheStats Stats => new(
        _responses.Count,
        _modelLists.Count,
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _misses),
        Interlocked.Read(ref _stores),
        Interlocked.Read(ref _evictions),
        Interlocked.Read(ref _modelListHits),
        Interlocked.Read(ref _modelListMisses),
        _inFlight.Count,
        Interlocked.Read(ref _leaseWaits));

    public int Clear()
    {
        var count = Count;
        ClearMemory();
        count += ClearPersistentResponses();
        count += ClearPersistentModelLists();
        return count;
    }

    public void ClearMemory()
    {
        _responses.Clear();
        _modelLists.Clear();
        _inFlight.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _stores, 0);
        Interlocked.Exchange(ref _evictions, 0);
        Interlocked.Exchange(ref _modelListHits, 0);
        Interlocked.Exchange(ref _modelListMisses, 0);
        Interlocked.Exchange(ref _leaseWaits, 0);
    }

    public void ClearModelsList()
    {
        _modelLists.Clear();
        ClearPersistentModelLists();
    }

    public void PruneExpiredResponses(int ttlSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var ttl = Math.Max(1, ttlSeconds);
        foreach (var pair in _responses)
        {
            if ((now - pair.Value.LastAccessedAt).TotalSeconds > ttl)
            {
                RemoveResponse(pair.Key);
            }
        }

        foreach (var pair in _inFlight)
        {
            if (pair.Value.CurrentCount > 0)
            {
                _inFlight.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in _modelLists)
        {
            if (now >= pair.Value.ExpiresAt)
            {
                RemoveModelList(pair.Key);
            }
        }

        EnforceResponseCapacity(ResponseCacheMaxEntries);
        EnforceModelListCapacity(ModelListCacheMaxEntries);
        PrunePersistentResponses(ttl);
        PrunePersistentModelLists();
        EnforcePersistentResponseCapacity(ResponseCacheMaxEntries);
        EnforcePersistentModelListCapacity(ModelListCacheMaxEntries);
    }

    public bool TryGetResponse(string cacheKey, int ttlSeconds, out TransparentProxyCachedResponse cachedResponse)
    {
        cachedResponse = default!;
        if (string.IsNullOrWhiteSpace(cacheKey) || !_responses.TryGetValue(cacheKey, out var entry))
        {
            if (TryGetPersistentResponse(cacheKey, ttlSeconds, out cachedResponse))
            {
                _responses[cacheKey] = cachedResponse;
                EnforceResponseCapacity(ResponseCacheMaxEntries);
                Interlocked.Increment(ref _hits);
                return true;
            }

            Interlocked.Increment(ref _misses);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if ((now - entry.LastAccessedAt).TotalSeconds > Math.Max(1, ttlSeconds))
        {
            RemoveResponse(cacheKey);
            Interlocked.Increment(ref _misses);
            return false;
        }

        cachedResponse = entry with
        {
            LastAccessedAt = now,
            HitCount = entry.HitCount + 1
        };
        _responses[cacheKey] = cachedResponse;
        UpdatePersistentResponseHit(cacheKey, now, cachedResponse.HitCount);
        Interlocked.Increment(ref _hits);
        return true;
    }

    public void StoreResponse(string cacheKey, int statusCode, string contentType, byte[] body, string modelName, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || body.Length > Math.Max(0, maxBytes))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _responses[cacheKey] = new TransparentProxyCachedResponse(
            now,
            now,
            statusCode,
            contentType,
            body,
            string.IsNullOrWhiteSpace(modelName) ? "-" : modelName.Trim());
        StorePersistentResponse(cacheKey, _responses[cacheKey], maxBytes);
        Interlocked.Increment(ref _stores);
        EnforceResponseCapacity(ResponseCacheMaxEntries);
        EnforcePersistentResponseCapacity(ResponseCacheMaxEntries);
    }

    public async Task<TransparentProxyCacheLease> AcquireResponseLeaseAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return TransparentProxyCacheLease.Empty;
        }

        var gate = _inFlight.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        if (gate.CurrentCount == 0)
        {
            Interlocked.Increment(ref _leaseWaits);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new TransparentProxyCacheLease(cacheKey, gate, _inFlight);
    }

    public bool TryGetModelsList(
        string pathAndQuery,
        TransparentProxyServerConfig config,
        out byte[] payload)
    {
        payload = Array.Empty<byte>();
        var key = BuildModelsListCacheKey(pathAndQuery, config);
        if (!_modelLists.TryGetValue(key, out var cache))
        {
            if (!TryGetPersistentModelsList(key, out cache))
            {
                Interlocked.Increment(ref _modelListMisses);
                return false;
            }

            _modelLists[key] = cache;
            EnforceModelListCapacity(ModelListCacheMaxEntries);
            Interlocked.Increment(ref _modelListHits);
        }

        var now = DateTimeOffset.UtcNow;
        if (now >= cache.ExpiresAt)
        {
            RemoveModelList(key);
            Interlocked.Increment(ref _modelListMisses);
            return false;
        }

        _modelLists[key] = cache with { LastAccessedAt = now };
        UpdatePersistentModelListHit(key, now);
        payload = cache.Payload;
        Interlocked.Increment(ref _modelListHits);
        return true;
    }

    public void StoreModelsList(string pathAndQuery, TransparentProxyServerConfig config, object payload)
    {
        var now = DateTimeOffset.UtcNow;
        var key = BuildModelsListCacheKey(pathAndQuery, config);
        _modelLists[key] = new TransparentProxyCachedModelsList(
            key,
            JsonSerializer.SerializeToUtf8Bytes(payload, CompactJsonOptions),
            now,
            now,
            now.AddSeconds(Math.Max(60, Math.Min(600, config.CacheTtlSeconds * 5))));
        StorePersistentModelList(_modelLists[key]);
        EnforceModelListCapacity(ModelListCacheMaxEntries);
        EnforcePersistentModelListCapacity(ModelListCacheMaxEntries);
    }

    public static bool TryBuildResponseCacheKey(
        string method,
        string pathAndQuery,
        byte[] requestBody,
        string routeId,
        string? requestedModel,
        out string cacheKey,
        out string rejectReason)
    {
        cacheKey = string.Empty;
        rejectReason = string.Empty;
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "method";
            return false;
        }

        if (requestBody.Length > 1024 * 1024)
        {
            rejectReason = "large-body";
            return false;
        }

        if (LooksUnsafeToCache(requestBody))
        {
            rejectReason = "tool-file-image";
            return false;
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(BuildCanonicalCacheBody(requestBody));
        var canonicalPathAndQuery = BuildCanonicalCachePath(pathAndQuery);
        cacheKey = $"{method.ToUpperInvariant()}|{canonicalPathAndQuery}|{routeId}|{requestedModel?.Trim() ?? string.Empty}|{Convert.ToHexString(hash)}";
        return true;
    }


}

internal sealed record TransparentProxyCacheStats(
    int ResponseEntries,
    int ModelListEntries,
    long Hits,
    long Misses,
    long Stores,
    long Evictions,
    long ModelListHits,
    long ModelListMisses,
    int InFlightKeys,
    long LeaseWaits);
