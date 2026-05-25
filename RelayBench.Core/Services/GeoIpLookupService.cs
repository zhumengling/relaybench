using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class GeoIpLookupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<string, GeoIpResult> _cache = new();
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private readonly string? _cacheFilePath;
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private int _pendingFlushCount;

    /// <summary>
    /// Creates a new GeoIpLookupService.
    /// </summary>
    /// <param name="cacheFilePath">
    /// Path to the JSON file used for disk persistence of cached results.
    /// Pass null to disable disk caching.
    /// </param>
    public GeoIpLookupService(string? cacheFilePath = null)
    {
        _cacheFilePath = cacheFilePath;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
            BaseAddress = new Uri("http://ip-api.com/"),
        };

        LoadCache();
    }

    /// <summary>
    /// Looks up geolocation data for an IP address.
    /// Results are cached in-memory and persisted to disk.
    /// Uses ip-api.com (free, no key required, 45 req/min).
    /// Returns null on any failure (graceful degradation).
    /// </summary>
    public async Task<GeoIpResult?> LookupAsync(string ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return null;

        var normalizedIp = ipAddress.Trim();

        if (_cache.TryGetValue(normalizedIp, out var cached))
            return cached;

        try
        {
            await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Double-check cache after acquiring semaphore (another call may have populated it)
                if (_cache.TryGetValue(normalizedIp, out cached))
                    return cached;

                // Enforce 1.5s spacing between requests to stay within 45 req/min
                var elapsed = DateTime.UtcNow - _lastRequestUtc;
                var requiredSpacing = TimeSpan.FromMilliseconds(1500);
                if (elapsed < requiredSpacing)
                {
                    var delay = requiredSpacing - elapsed;
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }

                var response = await _httpClient.GetAsync(
                    $"json/{Uri.EscapeDataString(normalizedIp)}?fields=status,country,city,as,org,lat,lon",
                    ct).ConfigureAwait(false);

                _lastRequestUtc = DateTime.UtcNow;

                if (!response.IsSuccessStatusCode)
                    return null;

                var apiResult = await response.Content
                    .ReadFromJsonAsync<IpApiResponse>(JsonOptions, ct)
                    .ConfigureAwait(false);

                if (apiResult is null || !string.Equals(apiResult.Status, "success", StringComparison.OrdinalIgnoreCase))
                    return null;

                var result = new GeoIpResult(
                    Country: apiResult.Country ?? string.Empty,
                    City: apiResult.City ?? string.Empty,
                    Asn: apiResult.As ?? string.Empty,
                    Organization: apiResult.Org ?? string.Empty,
                    Latitude: apiResult.Lat,
                    Longitude: apiResult.Lon);

                _cache[normalizedIp] = result;
                ScheduleFlush();

                return result;
            }
            finally
            {
                _rateLimiter.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // Graceful degradation: return null on any failure
            return null;
        }
    }

    /// <summary>
    /// Flushes the in-memory cache to disk immediately.
    /// </summary>
    public void FlushCache()
    {
        if (_cacheFilePath is null)
            return;

        try
        {
            var snapshot = _cache.ToArray();
            var entries = snapshot.Select(kvp => new GeoIpCacheEntry(kvp.Key, kvp.Value)).ToArray();
            var json = JsonSerializer.Serialize(entries, JsonOptions);

            var directory = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_cacheFilePath, json);
            Interlocked.Exchange(ref _pendingFlushCount, 0);
        }
        catch
        {
            // Disk flush is best-effort; failures are silently ignored
        }
    }

    private void LoadCache()
    {
        if (_cacheFilePath is null || !File.Exists(_cacheFilePath))
            return;

        try
        {
            var json = File.ReadAllText(_cacheFilePath);
            var entries = JsonSerializer.Deserialize<GeoIpCacheEntry[]>(json, JsonOptions);

            if (entries is null)
                return;

            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.IpAddress) && entry.Result is not null)
                    _cache[entry.IpAddress] = entry.Result;
            }
        }
        catch
        {
            // If cache file is corrupted, start fresh
        }
    }

    private void ScheduleFlush()
    {
        var count = Interlocked.Increment(ref _pendingFlushCount);

        // Flush every 10 new entries to avoid excessive disk writes
        if (count >= 10)
        {
            _ = Task.Run(FlushCache);
        }
    }

    private sealed record GeoIpCacheEntry(string IpAddress, GeoIpResult Result);

    private sealed class IpApiResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("city")]
        public string? City { get; set; }

        [JsonPropertyName("as")]
        public string? As { get; set; }

        [JsonPropertyName("org")]
        public string? Org { get; set; }

        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lon")]
        public double Lon { get; set; }
    }
}
