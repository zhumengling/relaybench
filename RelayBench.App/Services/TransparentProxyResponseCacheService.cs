using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyResponseCacheService
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
        Interlocked.Read(ref _evictions));

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
                return false;
            }

            _modelLists[key] = cache;
            EnforceModelListCapacity(ModelListCacheMaxEntries);
        }

        var now = DateTimeOffset.UtcNow;
        if (now >= cache.ExpiresAt)
        {
            RemoveModelList(key);
            return false;
        }

        _modelLists[key] = cache with { LastAccessedAt = now };
        UpdatePersistentModelListHit(key, now);
        payload = cache.Payload;
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

    private static bool LooksUnsafeToCache(byte[] requestBody)
    {
        if (requestBody.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return ContainsUnsafeCacheNode(document.RootElement);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsUnsafeCacheNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsUnsafeCacheProperty(property.Name))
                    {
                        return true;
                    }

                    if (ContainsUnsafeCacheNode(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsUnsafeCacheNode(item))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.String:
                var value = element.GetString();
                return value is not null &&
                       (value.Contains("data:image/", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("\"type\":\"input_image\"", StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    private static bool IsUnsafeCacheProperty(string name)
        => name.Equals("tools", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("tool_choice", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("tool_calls", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("function_call", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("files", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("file_ids", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("attachments", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("image_url", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("input_image", StringComparison.OrdinalIgnoreCase);

    private static byte[] BuildCanonicalCacheBody(byte[] requestBody)
    {
        if (requestBody.Length == 0 || requestBody.Length > 1024 * 1024)
        {
            return requestBody;
        }

        try
        {
            var node = JsonNode.Parse(requestBody);
            if (node is null)
            {
                return requestBody;
            }

            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                WriteCanonicalJsonNode(writer, node, depth: 0);
            }

            return stream.ToArray();
        }
        catch
        {
            return requestBody;
        }
    }

    private static void WriteCanonicalJsonNode(Utf8JsonWriter writer, JsonNode? node, int depth)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(static item => item.Key, StringComparer.Ordinal))
                {
                    if (ShouldSkipCacheProperty(property.Key, depth))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Key);
                    WriteCanonicalJsonNode(writer, property.Value, depth + 1);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteCanonicalJsonNode(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static bool ShouldSkipCacheProperty(string name, int depth)
        => ShouldSkipVolatileCacheProperty(name) ||
           depth == 0 &&
           (name.Equals("stream", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("user", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("store", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldSkipVolatileCacheProperty(string name)
        => name.Equals("idempotency_key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("idempotencyKey", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("request_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("requestId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("client_request_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("clientRequestId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("x_request_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("trace_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("traceId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("span_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("spanId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("session_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("sessionId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("prompt_cache_key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("promptCacheKey", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("prompt_cache_retention", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("promptCacheRetention", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("cache_control", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("cacheControl", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("nonce", StringComparison.OrdinalIgnoreCase);

    private static string BuildCanonicalCachePath(string pathAndQuery)
    {
        var value = string.IsNullOrWhiteSpace(pathAndQuery)
            ? "/"
            : pathAndQuery.Trim();
        var question = value.IndexOf('?');
        if (question < 0)
        {
            return value;
        }

        var path = question == 0 ? "/" : value[..question];
        var query = question + 1 < value.Length ? value[(question + 1)..] : string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return path;
        }

        var parameters = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part =>
            {
                var equals = part.IndexOf('=');
                var rawName = equals >= 0 ? part[..equals] : part;
                var rawValue = equals >= 0 ? part[(equals + 1)..] : string.Empty;
                var name = DecodeQueryPart(rawName);
                var value = DecodeQueryPart(rawValue);
                return (Name: name, Value: value);
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name) && !ShouldSkipVolatileCacheQueryParameter(item.Name))
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Value, StringComparer.Ordinal)
            .Select(static item => $"{Uri.EscapeDataString(item.Name)}={Uri.EscapeDataString(item.Value)}")
            .ToArray();

        return parameters.Length == 0
            ? path
            : path + "?" + string.Join("&", parameters);
    }

    private static bool ShouldSkipVolatileCacheQueryParameter(string name)
        => ShouldSkipVolatileCacheProperty(name) ||
           name.Equals("_", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ts", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("access_token", StringComparison.OrdinalIgnoreCase);

    private static string DecodeQueryPart(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal)).Trim();
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string BuildModelsListCacheKey(string pathAndQuery, TransparentProxyServerConfig config)
    {
        StringBuilder builder = new();
        foreach (var route in config.Routes)
        {
            AppendHashPart(builder, route.Id);
            AppendHashPart(builder, route.BaseUrl);
            AppendHashPart(builder, route.Prefix);
            AppendHashPart(builder, route.ApiKey);
            AppendHashPart(builder, route.Model);
            AppendHashPart(builder, route.OutboundProxy);
            AppendHashPart(builder, route.PayloadRulesText);
            foreach (var mapping in route.ModelMappings.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                AppendHashPart(builder, mapping.Name);
                AppendHashPart(builder, mapping.Alias);
            }

            foreach (var pattern in route.ExcludedModelPatterns.Order(StringComparer.OrdinalIgnoreCase))
            {
                AppendHashPart(builder, pattern);
            }

            foreach (var header in route.Headers.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                AppendHashPart(builder, header.Key);
                AppendHashPart(builder, header.Value);
            }
        }

        AppendHashPart(builder, pathAndQuery);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static void AppendHashPart(StringBuilder builder, string? value)
        => builder.Append(value?.Trim() ?? string.Empty).Append('\u001F');

    private bool TryGetPersistentResponse(
        string cacheKey,
        int ttlSeconds,
        out TransparentProxyCachedResponse cachedResponse)
    {
        cachedResponse = default!;
        if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return false;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT created_at, last_accessed_at, status_code, content_type, body, body_protected, model_name, hit_count
                    FROM transparent_proxy_response_cache
                    WHERE cache_key = $cache_key
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$cache_key", cacheKey);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                var createdAt = ReadDateTimeOffset(reader, 0);
                var lastAccessedAt = ReadDateTimeOffset(reader, 1);
                var now = DateTimeOffset.UtcNow;
                if ((now - lastAccessedAt).TotalSeconds > Math.Max(1, ttlSeconds))
                {
                    DeletePersistentResponse(connection, cacheKey);
                    return false;
                }

                var storedBody = (byte[])reader["body"];
                var bodyProtected = reader.GetInt32(5) != 0;
                if (!TryDecodeBody(storedBody, bodyProtected, out var body))
                {
                    DeletePersistentResponse(connection, cacheKey);
                    return false;
                }

                var hitCount = reader.GetInt32(7) + 1;
                cachedResponse = new TransparentProxyCachedResponse(
                    createdAt,
                    now,
                    reader.GetInt32(2),
                    reader.GetString(3),
                    body,
                    reader.GetString(6),
                    hitCount);
                UpdatePersistentResponseHit(connection, cacheKey, now, hitCount);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void StorePersistentResponse(
        string cacheKey,
        TransparentProxyCachedResponse response,
        int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) ||
            response.Body.Length == 0 ||
            response.Body.Length > Math.Max(0, maxBytes) ||
            string.IsNullOrWhiteSpace(_databasePath))
        {
            return;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                var (body, bodyProtected) = EncodeBody(response.Body);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO transparent_proxy_response_cache (
                        cache_key,
                        created_at,
                        last_accessed_at,
                        status_code,
                        content_type,
                        body,
                        body_protected,
                        model_name,
                        hit_count
                    )
                    VALUES (
                        $cache_key,
                        $created_at,
                        $last_accessed_at,
                        $status_code,
                        $content_type,
                        $body,
                        $body_protected,
                        $model_name,
                        $hit_count
                    )
                    ON CONFLICT(cache_key) DO UPDATE SET
                        created_at = excluded.created_at,
                        last_accessed_at = excluded.last_accessed_at,
                        status_code = excluded.status_code,
                        content_type = excluded.content_type,
                        body = excluded.body,
                        body_protected = excluded.body_protected,
                        model_name = excluded.model_name,
                        hit_count = excluded.hit_count;
                    """;
                command.Parameters.AddWithValue("$cache_key", cacheKey);
                command.Parameters.AddWithValue("$created_at", response.CreatedAt.ToString("O"));
                command.Parameters.AddWithValue("$last_accessed_at", response.LastAccessedAt.ToString("O"));
                command.Parameters.AddWithValue("$status_code", response.StatusCode);
                command.Parameters.AddWithValue("$content_type", response.ContentType);
                command.Parameters.Add("$body", SqliteType.Blob).Value = body;
                command.Parameters.AddWithValue("$body_protected", bodyProtected ? 1 : 0);
                command.Parameters.AddWithValue("$model_name", response.ModelName);
                command.Parameters.AddWithValue("$hit_count", response.HitCount);
                command.ExecuteNonQuery();
            }
            catch
            {
            }
        }
    }

    private int ClearPersistentResponses()
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return 0;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM transparent_proxy_response_cache;";
                return Math.Max(0, command.ExecuteNonQuery());
            }
            catch
            {
                return 0;
            }
        }
    }

    private bool TryGetPersistentModelsList(
        string key,
        out TransparentProxyCachedModelsList cache)
    {
        cache = default!;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return false;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT payload, created_at, last_accessed_at, expires_at
                    FROM transparent_proxy_model_list_cache
                    WHERE cache_key = $cache_key
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$cache_key", key);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                var expiresAt = ReadDateTimeOffset(reader, 3);
                if (DateTimeOffset.UtcNow >= expiresAt)
                {
                    DeletePersistentModelList(connection, key);
                    return false;
                }

                cache = new TransparentProxyCachedModelsList(
                    key,
                    (byte[])reader["payload"],
                    ReadDateTimeOffset(reader, 1),
                    DateTimeOffset.UtcNow,
                    expiresAt);
                UpdatePersistentModelListHit(connection, key, cache.LastAccessedAt);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void StorePersistentModelList(TransparentProxyCachedModelsList cache)
    {
        if (string.IsNullOrWhiteSpace(cache.Key) ||
            cache.Payload.Length == 0 ||
            string.IsNullOrWhiteSpace(_databasePath))
        {
            return;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO transparent_proxy_model_list_cache (
                        cache_key,
                        payload,
                        created_at,
                        last_accessed_at,
                        expires_at
                    )
                    VALUES (
                        $cache_key,
                        $payload,
                        $created_at,
                        $last_accessed_at,
                        $expires_at
                    )
                    ON CONFLICT(cache_key) DO UPDATE SET
                        payload = excluded.payload,
                        created_at = excluded.created_at,
                        last_accessed_at = excluded.last_accessed_at,
                        expires_at = excluded.expires_at;
                    """;
                command.Parameters.AddWithValue("$cache_key", cache.Key);
                command.Parameters.Add("$payload", SqliteType.Blob).Value = cache.Payload;
                command.Parameters.AddWithValue("$created_at", cache.CreatedAt.ToString("O"));
                command.Parameters.AddWithValue("$last_accessed_at", cache.LastAccessedAt.ToString("O"));
                command.Parameters.AddWithValue("$expires_at", cache.ExpiresAt.ToString("O"));
                command.ExecuteNonQuery();
            }
            catch
            {
            }
        }
    }

    private int ClearPersistentModelLists()
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return 0;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM transparent_proxy_model_list_cache;";
                return Math.Max(0, command.ExecuteNonQuery());
            }
            catch
            {
                return 0;
            }
        }
    }

    private void PrunePersistentResponses(int ttlSeconds)
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM transparent_proxy_response_cache
                    WHERE last_accessed_at < $expires_before;
                    """;
                command.Parameters.AddWithValue("$expires_before", DateTimeOffset.UtcNow.AddSeconds(-Math.Max(1, ttlSeconds)).ToString("O"));
                var removed = command.ExecuteNonQuery();
                if (removed > 0)
                {
                    Interlocked.Add(ref _evictions, removed);
                }
            }
            catch
            {
            }
        }
    }

    private void PrunePersistentModelLists()
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM transparent_proxy_model_list_cache
                    WHERE expires_at < $now;
                    """;
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                var removed = command.ExecuteNonQuery();
                if (removed > 0)
                {
                    Interlocked.Add(ref _evictions, removed);
                }
            }
            catch
            {
            }
        }
    }

    private void EnforcePersistentResponseCapacity(int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM transparent_proxy_response_cache
                    WHERE cache_key IN (
                        SELECT cache_key
                        FROM transparent_proxy_response_cache
                        ORDER BY last_accessed_at ASC
                        LIMIT (
                            SELECT CASE
                                WHEN COUNT(*) > $max_entries THEN COUNT(*) - $max_entries
                                ELSE 0
                            END
                            FROM transparent_proxy_response_cache
                        )
                    );
                    """;
                command.Parameters.AddWithValue("$max_entries", Math.Max(1, maxEntries));
                var removed = command.ExecuteNonQuery();
                if (removed > 0)
                {
                    Interlocked.Add(ref _evictions, removed);
                }
            }
            catch
            {
            }
        }
    }

    private void EnforcePersistentModelListCapacity(int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM transparent_proxy_model_list_cache
                    WHERE cache_key IN (
                        SELECT cache_key
                        FROM transparent_proxy_model_list_cache
                        ORDER BY last_accessed_at ASC
                        LIMIT (
                            SELECT CASE
                                WHEN COUNT(*) > $max_entries THEN COUNT(*) - $max_entries
                                ELSE 0
                            END
                            FROM transparent_proxy_model_list_cache
                        )
                    );
                    """;
                command.Parameters.AddWithValue("$max_entries", Math.Max(1, maxEntries));
                var removed = command.ExecuteNonQuery();
                if (removed > 0)
                {
                    Interlocked.Add(ref _evictions, removed);
                }
            }
            catch
            {
            }
        }
    }

    private void UpdatePersistentResponseHit(
        string cacheKey,
        DateTimeOffset lastAccessedAt,
        int hitCount)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                UpdatePersistentResponseHit(connection, cacheKey, lastAccessedAt, hitCount);
            }
            catch
            {
            }
        }
    }

    private static void UpdatePersistentResponseHit(
        SqliteConnection connection,
        string cacheKey,
        DateTimeOffset lastAccessedAt,
        int hitCount)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transparent_proxy_response_cache
            SET last_accessed_at = $last_accessed_at,
                hit_count = $hit_count
            WHERE cache_key = $cache_key;
            """;
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        command.Parameters.AddWithValue("$last_accessed_at", lastAccessedAt.ToString("O"));
        command.Parameters.AddWithValue("$hit_count", hitCount);
        command.ExecuteNonQuery();
    }

    private void UpdatePersistentModelListHit(
        string key,
        DateTimeOffset lastAccessedAt)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return;
        }

        lock (_databaseSyncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsurePersistentSchema(connection);
                UpdatePersistentModelListHit(connection, key, lastAccessedAt);
            }
            catch
            {
            }
        }
    }

    private static void UpdatePersistentModelListHit(
        SqliteConnection connection,
        string key,
        DateTimeOffset lastAccessedAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transparent_proxy_model_list_cache
            SET last_accessed_at = $last_accessed_at
            WHERE cache_key = $cache_key;
            """;
        command.Parameters.AddWithValue("$cache_key", key);
        command.Parameters.AddWithValue("$last_accessed_at", lastAccessedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void DeletePersistentResponse(SqliteConnection connection, string cacheKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM transparent_proxy_response_cache WHERE cache_key = $cache_key;";
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        command.ExecuteNonQuery();
    }

    private static void DeletePersistentModelList(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM transparent_proxy_model_list_cache WHERE cache_key = $cache_key;";
        command.Parameters.AddWithValue("$cache_key", key);
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Cache = SqliteCacheMode.Shared
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsurePersistentSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS transparent_proxy_response_cache (
                cache_key TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                status_code INTEGER NOT NULL,
                content_type TEXT NOT NULL,
                body BLOB NOT NULL,
                body_protected INTEGER NOT NULL DEFAULT 1,
                model_name TEXT NOT NULL,
                hit_count INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_transparent_proxy_response_cache_last_accessed
                ON transparent_proxy_response_cache(last_accessed_at);
            CREATE TABLE IF NOT EXISTS transparent_proxy_model_list_cache (
                cache_key TEXT PRIMARY KEY,
                payload BLOB NOT NULL,
                created_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_transparent_proxy_model_list_cache_expires
                ON transparent_proxy_model_list_cache(expires_at);
            """;
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.TryParse(reader.GetString(ordinal), out var value)
            ? value
            : DateTimeOffset.UtcNow;

    private static (byte[] Body, bool Protected) EncodeBody(byte[] body)
    {
        try
        {
            return (ProtectedData.Protect(body, optionalEntropy: null, DataProtectionScope.CurrentUser), true);
        }
        catch
        {
            return (body, false);
        }
    }

    private static bool TryDecodeBody(byte[] storedBody, bool bodyProtected, out byte[] body)
    {
        body = Array.Empty<byte>();
        if (storedBody.Length == 0)
        {
            return false;
        }

        if (!bodyProtected)
        {
            body = storedBody;
            return true;
        }

        try
        {
            body = ProtectedData.Unprotect(storedBody, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnforceResponseCapacity(int maxEntries)
    {
        if (_responses.Count <= maxEntries)
        {
            return;
        }

        foreach (var key in _responses
                     .OrderBy(static pair => pair.Value.LastAccessedAt)
                     .Take(Math.Max(1, _responses.Count - maxEntries))
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            RemoveResponse(key);
        }
    }

    private void EnforceModelListCapacity(int maxEntries)
    {
        if (_modelLists.Count <= maxEntries)
        {
            return;
        }

        foreach (var key in _modelLists
                     .OrderBy(static pair => pair.Value.LastAccessedAt)
                     .Take(Math.Max(1, _modelLists.Count - maxEntries))
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            RemoveModelList(key);
        }
    }

    private void RemoveResponse(string key)
    {
        if (_responses.TryRemove(key, out _))
        {
            Interlocked.Increment(ref _evictions);
        }

        if (!string.IsNullOrWhiteSpace(_databasePath) && File.Exists(_databasePath))
        {
            lock (_databaseSyncRoot)
            {
                try
                {
                    using var connection = OpenConnection();
                    EnsurePersistentSchema(connection);
                    DeletePersistentResponse(connection, key);
                }
                catch
                {
                }
            }
        }
    }

    private void RemoveModelList(string key)
    {
        if (_modelLists.TryRemove(key, out _))
        {
            Interlocked.Increment(ref _evictions);
        }

        if (!string.IsNullOrWhiteSpace(_databasePath) && File.Exists(_databasePath))
        {
            lock (_databaseSyncRoot)
            {
                try
                {
                    using var connection = OpenConnection();
                    EnsurePersistentSchema(connection);
                    DeletePersistentModelList(connection, key);
                }
                catch
                {
                }
            }
        }
    }
}

internal readonly struct TransparentProxyCacheLease : IDisposable
{
    private readonly string _cacheKey;
    private readonly SemaphoreSlim? _gate;
    private readonly ConcurrentDictionary<string, SemaphoreSlim>? _owner;

    internal static TransparentProxyCacheLease Empty => new();

    internal TransparentProxyCacheLease(
        string cacheKey,
        SemaphoreSlim gate,
        ConcurrentDictionary<string, SemaphoreSlim> owner)
    {
        _cacheKey = cacheKey;
        _gate = gate;
        _owner = owner;
    }

    public void Dispose()
    {
        if (_gate is null)
        {
            return;
        }

        _gate.Release();
        if (_gate.CurrentCount > 0 &&
            _owner is not null &&
            _owner.TryGetValue(_cacheKey, out var current) &&
            ReferenceEquals(current, _gate))
        {
            ((ICollection<KeyValuePair<string, SemaphoreSlim>>)_owner)
                .Remove(new KeyValuePair<string, SemaphoreSlim>(_cacheKey, _gate));
        }
    }
}

internal sealed record TransparentProxyCacheStats(
    int ResponseEntries,
    int ModelListEntries,
    long Hits,
    long Misses,
    long Stores,
    long Evictions);
