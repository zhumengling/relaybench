using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class ProxyEndpointModelCacheService
{
    private const string EndpointModelSentinel = "";
    private readonly string _databasePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ProxyEndpointModelCacheService(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RelayBench",
                "endpoint-model-cache.sqlite")
            : databasePath;
    }

    public async Task SaveCatalogAsync(
        ProxyEndpointSettings settings,
        ProxyModelCatalogResult result,
        CancellationToken cancellationToken = default)
    {
        if (!result.Success)
        {
            return;
        }

        var baseUrl = NormalizeEndpointKey(result.BaseUrl, settings.BaseUrl);
        var apiKeyHash = HashApiKey(settings.ApiKey);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKeyHash))
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            await UpsertAsync(
                connection,
                baseUrl,
                apiKeyHash,
                EndpointModelSentinel,
                null,
                null,
                null,
                null,
                null,
                result.CheckedAt,
                cancellationToken);

            var modelItems = result.ModelItems is { Count: > 0 }
                ? result.ModelItems
                : result.Models.Select(static model => new ProxyModelCatalogItem(model)).ToArray();

            foreach (var item in modelItems)
            {
                var model = item.Id.Trim();
                if (string.IsNullOrWhiteSpace(model))
                {
                    continue;
                }

                await UpsertAsync(
                    connection,
                    baseUrl,
                    apiKeyHash,
                    model,
                    ModelContextWindowCatalog.ResolveContextWindow(model, item.ContextWindow),
                    null,
                    null,
                    null,
                    null,
                    result.CheckedAt,
                    cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveProtocolProbeAsync(
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult result,
        CancellationToken cancellationToken = default)
    {
        if (!result.ChatCompletionsSupported &&
            !result.ResponsesSupported &&
            !result.AnthropicMessagesSupported)
        {
            return;
        }

        var baseUrl = NormalizeEndpointKey(result.BaseUrl, settings.BaseUrl);
        var apiKeyHash = HashApiKey(settings.ApiKey);
        var model = FirstNonEmpty(result.ProbeModel, settings.Model);
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(apiKeyHash) ||
            string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var preferredWireApi = NormalizeWireApi(
                result.PreferredWireApi,
                result.ChatCompletionsSupported,
                result.ResponsesSupported,
                result.AnthropicMessagesSupported) ??
            ResolvePreferredWireApi(
                result.ChatCompletionsSupported,
                result.ResponsesSupported,
                result.AnthropicMessagesSupported);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await UpsertAsync(
                connection,
                baseUrl,
                apiKeyHash,
                EndpointModelSentinel,
                null,
                preferredWireApi,
                result.ChatCompletionsSupported,
                result.ResponsesSupported,
                result.AnthropicMessagesSupported,
                result.CheckedAt,
                cancellationToken);
            await UpsertAsync(
                connection,
                baseUrl,
                apiKeyHash,
                model,
                ModelContextWindowCatalog.ResolveContextWindow(model),
                preferredWireApi,
                result.ChatCompletionsSupported,
                result.ResponsesSupported,
                result.AnthropicMessagesSupported,
                result.CheckedAt,
                cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveDiagnosticsAsync(
        ProxyEndpointSettings settings,
        ProxyDiagnosticsResult result,
        CancellationToken cancellationToken = default)
    {
        var anthropicSupported = FindScenario(result.ScenarioResults, ProxyProbeScenarioKind.AnthropicMessages)?.Success == true;
        var responsesSupported = FindScenario(result.ScenarioResults, ProxyProbeScenarioKind.Responses)?.Success == true;
        var chatSupported = IsOpenAiChatCompletionsSupported(result);
        if (!result.ModelsRequestSucceeded &&
            !chatSupported &&
            !responsesSupported &&
            !anthropicSupported)
        {
            return;
        }

        var baseUrl = NormalizeEndpointKey(result.BaseUrl, settings.BaseUrl);
        var apiKeyHash = HashApiKey(settings.ApiKey);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKeyHash))
        {
            return;
        }

        var effectiveModel = FirstNonEmpty(result.EffectiveModel, result.RequestedModel, settings.Model);
        var preferredWireApi = ResolvePreferredWireApi(
            chatSupported,
            responsesSupported,
            anthropicSupported);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await UpsertAsync(
                connection,
                baseUrl,
                apiKeyHash,
                EndpointModelSentinel,
                null,
                preferredWireApi,
                chatSupported,
                responsesSupported,
                anthropicSupported,
                result.CheckedAt,
                cancellationToken);

            var models = result.SampleModels
                .Append(effectiveModel)
                .Where(static model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var model in models)
            {
                await UpsertAsync(
                    connection,
                    baseUrl,
                    apiKeyHash,
                    model,
                    ModelContextWindowCatalog.ResolveContextWindow(model),
                    preferredWireApi,
                    chatSupported,
                    responsesSupported,
                    anthropicSupported,
                    result.CheckedAt,
                    cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CachedProxyEndpointModelInfo?> TryResolveAsync(
        string baseUrl,
        string apiKey,
        string model,
        CancellationToken cancellationToken = default)
    {
        var normalizedBaseUrl = NormalizeEndpointKey(baseUrl);
        var apiKeyHash = HashApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl) ||
            string.IsNullOrWhiteSpace(apiKeyHash) ||
            string.IsNullOrWhiteSpace(model) ||
            !File.Exists(_databasePath))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var modelInfo = await QueryAsync(
            connection,
            normalizedBaseUrl,
            apiKeyHash,
            model.Trim(),
            cancellationToken);
        var endpointInfo = await QueryAsync(
            connection,
            normalizedBaseUrl,
            apiKeyHash,
            EndpointModelSentinel,
            cancellationToken);

        if (modelInfo is null)
        {
            return endpointInfo is null
                ? null
                : endpointInfo with { Model = model.Trim() };
        }

        if (endpointInfo is null)
        {
            return modelInfo;
        }

        return modelInfo with
        {
            PreferredWireApi = modelInfo.PreferredWireApi ?? endpointInfo.PreferredWireApi,
            ChatCompletionsSupported = modelInfo.ChatCompletionsSupported ?? endpointInfo.ChatCompletionsSupported,
            ResponsesSupported = modelInfo.ResponsesSupported ?? endpointInfo.ResponsesSupported,
            AnthropicMessagesSupported = modelInfo.AnthropicMessagesSupported ?? endpointInfo.AnthropicMessagesSupported,
            CheckedAt = modelInfo.CheckedAt >= endpointInfo.CheckedAt ? modelInfo.CheckedAt : endpointInfo.CheckedAt
        };
    }

    public async Task<string?> TryResolvePreferredWireApiAsync(
        string baseUrl,
        string apiKey,
        string model,
        CancellationToken cancellationToken = default)
    {
        var cached = await TryResolveAsync(baseUrl, apiKey, model, cancellationToken);
        return NormalizeWireApi(
            cached?.PreferredWireApi,
            cached?.ChatCompletionsSupported == true,
            cached?.ResponsesSupported == true,
            cached?.AnthropicMessagesSupported == true) ??
            ResolvePreferredWireApi(
                cached?.ChatCompletionsSupported == true,
                cached?.ResponsesSupported == true,
                cached?.AnthropicMessagesSupported == true);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS proxy_endpoint_model_cache (
                base_url TEXT NOT NULL,
                api_key_hash TEXT NOT NULL,
                model TEXT NOT NULL,
                context_window INTEGER NULL,
                preferred_wire_api TEXT NULL,
                chat_supported INTEGER NULL,
                responses_supported INTEGER NULL,
                anthropic_supported INTEGER NULL,
                checked_at_utc TEXT NOT NULL,
                PRIMARY KEY (base_url, api_key_hash, model)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.CommandText = "ALTER TABLE proxy_endpoint_model_cache ADD COLUMN anthropic_supported INTEGER NULL;";
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                                        ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        string baseUrl,
        string apiKeyHash,
        string model,
        int? contextWindow,
        string? preferredWireApi,
        bool? chatSupported,
        bool? responsesSupported,
        bool? anthropicSupported,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO proxy_endpoint_model_cache (
                base_url,
                api_key_hash,
                model,
                context_window,
                preferred_wire_api,
                chat_supported,
                responses_supported,
                anthropic_supported,
                checked_at_utc
            )
            VALUES (
                $base_url,
                $api_key_hash,
                $model,
                $context_window,
                $preferred_wire_api,
                $chat_supported,
                $responses_supported,
                $anthropic_supported,
                $checked_at_utc
            )
            ON CONFLICT(base_url, api_key_hash, model) DO UPDATE SET
                context_window = COALESCE(excluded.context_window, proxy_endpoint_model_cache.context_window),
                preferred_wire_api = COALESCE(excluded.preferred_wire_api, proxy_endpoint_model_cache.preferred_wire_api),
                chat_supported = COALESCE(excluded.chat_supported, proxy_endpoint_model_cache.chat_supported),
                responses_supported = COALESCE(excluded.responses_supported, proxy_endpoint_model_cache.responses_supported),
                anthropic_supported = COALESCE(excluded.anthropic_supported, proxy_endpoint_model_cache.anthropic_supported),
                checked_at_utc = excluded.checked_at_utc;
            """;
        command.Parameters.AddWithValue("$base_url", baseUrl);
        command.Parameters.AddWithValue("$api_key_hash", apiKeyHash);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$context_window", contextWindow is null ? DBNull.Value : contextWindow.Value);
        command.Parameters.AddWithValue("$preferred_wire_api", preferredWireApi is null ? DBNull.Value : preferredWireApi);
        command.Parameters.AddWithValue("$chat_supported", chatSupported is null ? DBNull.Value : chatSupported.Value ? 1 : 0);
        command.Parameters.AddWithValue("$responses_supported", responsesSupported is null ? DBNull.Value : responsesSupported.Value ? 1 : 0);
        command.Parameters.AddWithValue("$anthropic_supported", anthropicSupported is null ? DBNull.Value : anthropicSupported.Value ? 1 : 0);
        command.Parameters.AddWithValue("$checked_at_utc", checkedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CachedProxyEndpointModelInfo?> QueryAsync(
        SqliteConnection connection,
        string baseUrl,
        string apiKeyHash,
        string model,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                model,
                context_window,
                preferred_wire_api,
                chat_supported,
                responses_supported,
                anthropic_supported,
                checked_at_utc
            FROM proxy_endpoint_model_cache
            WHERE base_url = $base_url
              AND api_key_hash = $api_key_hash
              AND model = $model
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$base_url", baseUrl);
        command.Parameters.AddWithValue("$api_key_hash", apiKeyHash);
        command.Parameters.AddWithValue("$model", model);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var cachedModel = reader.GetString(0);
        var contextWindow = reader.IsDBNull(1) ? null : (int?)reader.GetInt32(1);
        var chatSupported = reader.IsDBNull(3) ? (bool?)null : reader.GetInt32(3) == 1;
        var responsesSupported = reader.IsDBNull(4) ? (bool?)null : reader.GetInt32(4) == 1;
        var anthropicSupported = reader.IsDBNull(5) ? (bool?)null : reader.GetInt32(5) == 1;
        var preferredWireApi = NormalizeWireApi(
            reader.IsDBNull(2) ? null : reader.GetString(2),
            chatSupported == true,
            responsesSupported == true,
            anthropicSupported == true);
        var checkedAtText = reader.GetString(6);
        var checkedAt = DateTimeOffset.TryParse(checkedAtText, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        return new CachedProxyEndpointModelInfo(
            baseUrl,
            cachedModel,
            contextWindow,
            preferredWireApi,
            chatSupported,
            responsesSupported,
            anthropicSupported,
            checkedAt);
    }

    private static ProxyProbeScenarioResult? FindScenario(
        IReadOnlyList<ProxyProbeScenarioResult>? scenarios,
        ProxyProbeScenarioKind kind)
        => scenarios?.FirstOrDefault(item => item.Scenario == kind);

    private static bool IsOpenAiChatCompletionsSupported(ProxyDiagnosticsResult result)
    {
        var chatScenario = FindScenario(result.ScenarioResults, ProxyProbeScenarioKind.ChatCompletions);
        if (chatScenario is null)
        {
            return result.ChatRequestSucceeded;
        }

        if (!chatScenario.Success)
        {
            return false;
        }

        if (chatScenario.Trace is null)
        {
            return result.ChatRequestSucceeded &&
                   FindScenario(result.ScenarioResults, ProxyProbeScenarioKind.AnthropicMessages)?.Success != true &&
                   FindScenario(result.ScenarioResults, ProxyProbeScenarioKind.Responses)?.Success != true;
        }

        return string.Equals(
            ProxyWireApiProbeService.NormalizeWireApi(chatScenario.Trace.WireApi),
            ProxyWireApiProbeService.ChatCompletionsWireApi,
            StringComparison.Ordinal);
    }

    private static string? ResolvePreferredWireApi(
        bool chatSupported,
        bool responsesSupported,
        bool anthropicSupported)
        => ProxyWireApiProbeService.ResolvePreferredWireApi(
            chatSupported,
            responsesSupported,
            anthropicSupported);

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string? NormalizeWireApi(
        string? value,
        bool chatSupported,
        bool responsesSupported,
        bool anthropicSupported)
    {
        var normalized = ProxyWireApiProbeService.NormalizeWireApi(value);
        if (responsesSupported &&
            string.Equals(normalized, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal))
        {
            return ProxyWireApiProbeService.ResponsesWireApi;
        }

        if (anthropicSupported &&
            string.Equals(normalized, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal))
        {
            return ProxyWireApiProbeService.AnthropicMessagesWireApi;
        }

        if (chatSupported &&
            string.Equals(normalized, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal))
        {
            return ProxyWireApiProbeService.ChatCompletionsWireApi;
        }

        return null;
    }

    private static string HashApiKey(string? apiKey)
    {
        var normalized = apiKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    private static string NormalizeEndpointKey(params string?[] candidates)
    {
        var value = candidates.FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate));
        var normalized = ClientApiConfigPatterns.NormalizeEndpoint(value);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return normalized.ToLowerInvariant();
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3].TrimEnd('/');
        }

        builder.Path = string.IsNullOrWhiteSpace(path) ? "/" : path;
        return builder.Uri.ToString().TrimEnd('/').ToLowerInvariant();
    }
}
