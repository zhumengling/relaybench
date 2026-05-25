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
        => (body, false);

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

        if (!OperatingSystem.IsWindows())
        {
            return false;
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
}
