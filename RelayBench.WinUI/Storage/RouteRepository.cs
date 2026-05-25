using Microsoft.Data.Sqlite;
using RelayBench.Services.Infrastructure;

namespace RelayBench.WinUI.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IRouteRepository"/>.
/// API keys are encrypted via <see cref="SecretProtector"/> (DPAPI) on write and decrypted on read.
/// All dates are stored as ISO-8601 UTC strings.
/// </summary>
public sealed class RouteRepository : IRouteRepository
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<RouteDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var connection = HistoryDatabase.CreateConnection();
            EnsureRouteColumns(connection);
            using var cmd = connection.CreateCommand();

            cmd.CommandText = """
                SELECT id, name, upstream_url, api_key, priority, model_filter, enabled, updated_at,
                       prefix, outbound_proxy, request_retry, max_retry_interval_seconds,
                       model_cooldown_seconds, excluded_model_patterns, payload_rules_text,
                       preferred_wire_api, headers_text, auth_mode, oauth_provider,
                       oauth_credential_id, codex_backend_base_url, codex_oauth_fast_mode
                FROM routes
                ORDER BY priority DESC
                """;

            using var reader = cmd.ExecuteReader();
            var results = new List<RouteDefinition>();

            while (reader.Read())
            {
                var protectedKey = reader.IsDBNull(3) ? null : reader.GetString(3);
                var decryptedKey = protectedKey is not null
                    ? SecretProtector.Unprotect(protectedKey)
                    : null;

                results.Add(new RouteDefinition(
                    Id: reader.GetString(0),
                    Name: reader.GetString(1),
                    UpstreamUrl: reader.GetString(2),
                    ApiKeyProtected: decryptedKey,
                    Priority: reader.GetInt32(4),
                    ModelFilter: reader.IsDBNull(5) ? null : reader.GetString(5),
                    Enabled: reader.GetInt32(6) != 0,
                    UpdatedAtUtc: DateTime.Parse(reader.GetString(7)).ToUniversalTime(),
                    Prefix: reader.IsDBNull(8) ? null : reader.GetString(8),
                    OutboundProxy: reader.IsDBNull(9) ? null : reader.GetString(9),
                    RequestRetry: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    MaxRetryIntervalSeconds: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    ModelCooldownSeconds: reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    ExcludedModelPatterns: reader.IsDBNull(13) ? null : reader.GetString(13),
                    PayloadRulesText: reader.IsDBNull(14) ? null : reader.GetString(14),
                    PreferredWireApi: reader.IsDBNull(15) ? null : reader.GetString(15),
                    HeadersText: reader.IsDBNull(16) ? null : reader.GetString(16),
                    AuthMode: reader.IsDBNull(17) ? null : reader.GetString(17),
                    OAuthProvider: reader.IsDBNull(18) ? null : reader.GetString(18),
                    OAuthCredentialId: reader.IsDBNull(19) ? null : reader.GetString(19),
                    CodexBackendBaseUrl: reader.IsDBNull(20) ? null : reader.GetString(20),
                    CodexOAuthFastMode: !reader.IsDBNull(21) && reader.GetInt32(21) != 0));
            }

            return (IReadOnlyList<RouteDefinition>)results;
        }, ct);
    }

    /// <inheritdoc/>
    public Task UpsertAsync(RouteDefinition route, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(route.Name);

        return Task.Run(() =>
        {
            var id = string.IsNullOrWhiteSpace(route.Id)
                ? Guid.NewGuid().ToString()
                : route.Id;

            var protectedKey = route.ApiKeyProtected is not null
                ? SecretProtector.Protect(route.ApiKeyProtected)
                : null;

            using var connection = HistoryDatabase.CreateConnection();
            EnsureRouteColumns(connection);
            using var cmd = connection.CreateCommand();

            cmd.CommandText = """
                INSERT INTO routes (
                    id, name, upstream_url, api_key, priority, model_filter, enabled, updated_at,
                    prefix, outbound_proxy, request_retry, max_retry_interval_seconds,
                    model_cooldown_seconds, excluded_model_patterns, payload_rules_text,
                    preferred_wire_api, headers_text, auth_mode, oauth_provider,
                    oauth_credential_id, codex_backend_base_url, codex_oauth_fast_mode)
                VALUES (
                    @id, @name, @upstreamUrl, @apiKey, @priority, @modelFilter, @enabled, @updatedAt,
                    @prefix, @outboundProxy, @requestRetry, @maxRetryIntervalSeconds,
                    @modelCooldownSeconds, @excludedModelPatterns, @payloadRulesText,
                    @preferredWireApi, @headersText, @authMode, @oauthProvider,
                    @oauthCredentialId, @codexBackendBaseUrl, @codexOAuthFastMode)
                ON CONFLICT(id) DO UPDATE SET
                    name                       = excluded.name,
                    upstream_url               = excluded.upstream_url,
                    api_key                    = excluded.api_key,
                    priority                   = excluded.priority,
                    model_filter               = excluded.model_filter,
                    enabled                    = excluded.enabled,
                    updated_at                 = excluded.updated_at,
                    prefix                     = excluded.prefix,
                    outbound_proxy             = excluded.outbound_proxy,
                    request_retry              = excluded.request_retry,
                    max_retry_interval_seconds = excluded.max_retry_interval_seconds,
                    model_cooldown_seconds     = excluded.model_cooldown_seconds,
                    excluded_model_patterns    = excluded.excluded_model_patterns,
                    payload_rules_text         = excluded.payload_rules_text,
                    preferred_wire_api         = excluded.preferred_wire_api,
                    headers_text               = excluded.headers_text,
                    auth_mode                  = excluded.auth_mode,
                    oauth_provider             = excluded.oauth_provider,
                    oauth_credential_id        = excluded.oauth_credential_id,
                    codex_backend_base_url     = excluded.codex_backend_base_url,
                    codex_oauth_fast_mode      = excluded.codex_oauth_fast_mode
                """;

            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", route.Name);
            cmd.Parameters.AddWithValue("@upstreamUrl", route.UpstreamUrl);
            cmd.Parameters.AddWithValue("@apiKey", protectedKey is not null ? protectedKey : DBNull.Value);
            cmd.Parameters.AddWithValue("@priority", route.Priority);
            cmd.Parameters.AddWithValue("@modelFilter", route.ModelFilter is not null ? route.ModelFilter : DBNull.Value);
            cmd.Parameters.AddWithValue("@enabled", route.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@prefix", ToDb(route.Prefix));
            cmd.Parameters.AddWithValue("@outboundProxy", ToDb(route.OutboundProxy));
            cmd.Parameters.AddWithValue("@requestRetry", route.RequestRetry is { } requestRetry ? requestRetry : DBNull.Value);
            cmd.Parameters.AddWithValue("@maxRetryIntervalSeconds", route.MaxRetryIntervalSeconds is { } retryInterval ? retryInterval : DBNull.Value);
            cmd.Parameters.AddWithValue("@modelCooldownSeconds", route.ModelCooldownSeconds is { } cooldownSeconds ? cooldownSeconds : DBNull.Value);
            cmd.Parameters.AddWithValue("@excludedModelPatterns", ToDb(route.ExcludedModelPatterns));
            cmd.Parameters.AddWithValue("@payloadRulesText", ToDb(route.PayloadRulesText));
            cmd.Parameters.AddWithValue("@preferredWireApi", ToDb(route.PreferredWireApi));
            cmd.Parameters.AddWithValue("@headersText", ToDb(route.HeadersText));
            cmd.Parameters.AddWithValue("@authMode", ToDb(route.AuthMode));
            cmd.Parameters.AddWithValue("@oauthProvider", ToDb(route.OAuthProvider));
            cmd.Parameters.AddWithValue("@oauthCredentialId", ToDb(route.OAuthCredentialId));
            cmd.Parameters.AddWithValue("@codexBackendBaseUrl", ToDb(route.CodexBackendBaseUrl));
            cmd.Parameters.AddWithValue("@codexOAuthFastMode", route.CodexOAuthFastMode ? 1 : 0);

            cmd.ExecuteNonQuery();
        }, ct);
    }

    /// <inheritdoc/>
    public Task ReorderAsync(IReadOnlyList<(string id, int priority)> ordering, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ordering);

        return Task.Run(() =>
        {
            if (ordering.Count == 0)
                return;

            using var connection = HistoryDatabase.CreateConnection();
            using var transaction = connection.BeginTransaction();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;

                cmd.CommandText = "UPDATE routes SET priority = @priority, updated_at = @updatedAt WHERE id = @id";

                var idParam = cmd.Parameters.Add("@id", SqliteType.Text);
                var priorityParam = cmd.Parameters.Add("@priority", SqliteType.Integer);
                var updatedAtParam = cmd.Parameters.Add("@updatedAt", SqliteType.Text);

                var now = DateTime.UtcNow.ToString("o");

                foreach (var (id, priority) in ordering)
                {
                    idParam.Value = id;
                    priorityParam.Value = priority;
                    updatedAtParam.Value = now;
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, ct);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return Task.Run(() =>
        {
            using var connection = HistoryDatabase.CreateConnection();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "DELETE FROM routes WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);

            cmd.ExecuteNonQuery();
        }, ct);
    }

    private static object ToDb(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static void EnsureRouteColumns(SqliteConnection connection)
    {
        TryAddColumn(connection, "prefix TEXT");
        TryAddColumn(connection, "outbound_proxy TEXT");
        TryAddColumn(connection, "request_retry INTEGER");
        TryAddColumn(connection, "max_retry_interval_seconds INTEGER");
        TryAddColumn(connection, "model_cooldown_seconds INTEGER");
        TryAddColumn(connection, "excluded_model_patterns TEXT");
        TryAddColumn(connection, "payload_rules_text TEXT");
        TryAddColumn(connection, "preferred_wire_api TEXT");
        TryAddColumn(connection, "headers_text TEXT");
        TryAddColumn(connection, "auth_mode TEXT");
        TryAddColumn(connection, "oauth_provider TEXT");
        TryAddColumn(connection, "oauth_credential_id TEXT");
        TryAddColumn(connection, "codex_backend_base_url TEXT");
        TryAddColumn(connection, "codex_oauth_fast_mode INTEGER NOT NULL DEFAULT 0");
    }

    private static void TryAddColumn(SqliteConnection connection, string columnDefinition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE routes ADD COLUMN {columnDefinition}";
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                                        ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Existing installations keep their data; only missing columns are added.
        }
    }
}
