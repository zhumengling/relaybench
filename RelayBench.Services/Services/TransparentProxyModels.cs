using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.Core.Services;

namespace RelayBench.Services;

public sealed class TransparentProxyRoute
{
    public TransparentProxyRoute(
        string id,
        string name,
        string baseUrl,
        string apiKey,
        string model,
        string? preferredWireApi = null,
        bool? chatCompletionsSupported = null,
        bool? responsesSupported = null,
        bool? anthropicMessagesSupported = null,
        DateTimeOffset? protocolCheckedAt = null,
        IReadOnlyList<string>? models = null,
        int priority = 0,
        string? prefix = null,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyList<TransparentProxyModelMapping>? modelMappings = null,
        IReadOnlyList<string>? excludedModelPatterns = null,
        string? outboundProxy = null,
        int? requestRetry = null,
        int? maxRetryIntervalSeconds = null,
        int? modelCooldownSeconds = null,
        string? payloadRulesText = null,
        string? authMode = null,
        string? oauthProvider = null,
        string? oauthCredentialId = null,
        string? codexBackendBaseUrl = null,
        int? runtimePriority = null,
        bool codexOAuthFastMode = false)
    {
        Id = id;
        Name = name;
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        Model = model;
        ModelMappings = NormalizeModelMappings(modelMappings, models, model);
        Models = ModelMappings
            .Select(static mapping => mapping.Name)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Priority = Math.Max(0, priority);
        RuntimePriority = Math.Max(0, runtimePriority ?? Priority);
        Prefix = prefix?.Trim().Trim('/') ?? string.Empty;
        Headers = new Dictionary<string, string>(
            headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        ExcludedModelPatterns = (excludedModelPatterns ?? Array.Empty<string>())
            .Select(static item => item?.Trim() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OutboundProxy = outboundProxy?.Trim() ?? string.Empty;
        RequestRetry = requestRetry;
        MaxRetryIntervalSeconds = maxRetryIntervalSeconds;
        ModelCooldownSeconds = modelCooldownSeconds;
        PayloadRulesText = payloadRulesText?.Trim() ?? string.Empty;
        AuthMode = TransparentProxyRouteAuthModes.Normalize(authMode);
        OAuthProvider = oauthProvider?.Trim() ?? string.Empty;
        OAuthCredentialId = oauthCredentialId?.Trim() ?? string.Empty;
        CodexBackendBaseUrl = codexBackendBaseUrl?.Trim() ?? string.Empty;
        CodexOAuthFastMode = codexOAuthFastMode && string.Equals(AuthMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase);
        CacheScopeId = BuildCacheScopeId(Id, BaseUrl, Prefix, ModelMappings, PayloadRulesText, CodexOAuthFastMode);
        PreferredWireApi = ProxyWireApiProbeService.NormalizeWireApi(preferredWireApi);
        ChatCompletionsSupported = chatCompletionsSupported;
        ResponsesSupported = responsesSupported;
        AnthropicMessagesSupported = anthropicMessagesSupported;
        ProtocolCheckedAt = protocolCheckedAt;
    }

    public string Id { get; }

    public string Name { get; }

    public string BaseUrl { get; }

    public string ApiKey { get; }

    public string Model { get; }

    public IReadOnlyList<string> Models { get; }

    public IReadOnlyList<TransparentProxyModelMapping> ModelMappings { get; }

    public int Priority { get; }

    public int RuntimePriority { get; }

    public string Prefix { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public IReadOnlyList<string> ExcludedModelPatterns { get; }

    public string OutboundProxy { get; }

    public int? RequestRetry { get; }

    public int? MaxRetryIntervalSeconds { get; }

    public int? ModelCooldownSeconds { get; }

    public string PayloadRulesText { get; }

    public string AuthMode { get; }

    public string OAuthProvider { get; }

    public string OAuthCredentialId { get; }

    public string CodexBackendBaseUrl { get; }

    public bool CodexOAuthFastMode { get; }

    public bool IsCodexOAuth
        => string.Equals(AuthMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(OAuthProvider, CodexOAuthConstants.Provider, StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(OAuthCredentialId);

    public string CacheScopeId { get; }

    public string? PreferredWireApi { get; }

    public bool? ChatCompletionsSupported { get; }

    public bool? ResponsesSupported { get; }

    public bool? AnthropicMessagesSupported { get; }

    public DateTimeOffset? ProtocolCheckedAt { get; }

    public TransparentProxyRoute WithProtocol(
        string? preferredWireApi,
        bool? chatCompletionsSupported,
        bool? responsesSupported,
        bool? anthropicMessagesSupported,
        DateTimeOffset? protocolCheckedAt)
        => new(
            Id,
            Name,
            BaseUrl,
            ApiKey,
            Model,
            preferredWireApi,
            chatCompletionsSupported,
            responsesSupported,
            anthropicMessagesSupported,
            protocolCheckedAt,
            Models,
            Priority,
            Prefix,
            Headers,
            ModelMappings,
            ExcludedModelPatterns,
            OutboundProxy,
            RequestRetry,
            MaxRetryIntervalSeconds,
            ModelCooldownSeconds,
            PayloadRulesText,
            AuthMode,
            OAuthProvider,
            OAuthCredentialId,
            CodexBackendBaseUrl,
            RuntimePriority,
            CodexOAuthFastMode)
        {
            CircuitOpenUntil = CircuitOpenUntil
        };

    public TransparentProxyRoute WithModels(IReadOnlyList<string> models)
        => new(
            Id,
            Name,
            BaseUrl,
            ApiKey,
            Model,
            PreferredWireApi,
            ChatCompletionsSupported,
            ResponsesSupported,
            AnthropicMessagesSupported,
            ProtocolCheckedAt,
            models,
            Priority,
            Prefix,
            Headers,
            MergeModelMappings(models, ModelMappings),
            ExcludedModelPatterns,
            OutboundProxy,
            RequestRetry,
            MaxRetryIntervalSeconds,
            ModelCooldownSeconds,
            PayloadRulesText,
            AuthMode,
            OAuthProvider,
            OAuthCredentialId,
            CodexBackendBaseUrl,
            RuntimePriority,
            CodexOAuthFastMode)
        {
            CircuitOpenUntil = CircuitOpenUntil
        };

    public TransparentProxyRoute WithRuntimePriority(int runtimePriority)
        => new(
            Id,
            Name,
            BaseUrl,
            ApiKey,
            Model,
            PreferredWireApi,
            ChatCompletionsSupported,
            ResponsesSupported,
            AnthropicMessagesSupported,
            ProtocolCheckedAt,
            Models,
            Priority,
            Prefix,
            Headers,
            ModelMappings,
            ExcludedModelPatterns,
            OutboundProxy,
            RequestRetry,
            MaxRetryIntervalSeconds,
            ModelCooldownSeconds,
            PayloadRulesText,
            AuthMode,
            OAuthProvider,
            OAuthCredentialId,
            CodexBackendBaseUrl,
            runtimePriority,
            CodexOAuthFastMode)
        {
            CircuitOpenUntil = CircuitOpenUntil
        };

    internal DateTimeOffset CircuitOpenUntil { get; set; } = DateTimeOffset.MinValue;

    private static IReadOnlyList<TransparentProxyModelMapping> NormalizeModelMappings(
        IReadOnlyList<TransparentProxyModelMapping>? mappings,
        IReadOnlyList<string>? models,
        string fallbackModel)
    {
        List<TransparentProxyModelMapping> normalized = [];
        foreach (var mapping in mappings ?? Array.Empty<TransparentProxyModelMapping>())
        {
            AddModelMapping(normalized, mapping.Name, mapping.Alias);
        }

        foreach (var model in models ?? Array.Empty<string>())
        {
            AddModelMapping(normalized, model, string.Empty);
        }

        AddModelMapping(normalized, fallbackModel, string.Empty);
        return normalized.ToArray();
    }

    private static IReadOnlyList<TransparentProxyModelMapping> MergeModelMappings(
        IReadOnlyList<string> models,
        IReadOnlyList<TransparentProxyModelMapping> existing)
    {
        var aliasByName = existing
            .Where(static mapping => !string.IsNullOrWhiteSpace(mapping.Name))
            .GroupBy(static mapping => mapping.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Alias, StringComparer.OrdinalIgnoreCase);
        List<TransparentProxyModelMapping> merged = [];
        foreach (var model in models)
        {
            var name = model?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            AddModelMapping(merged, name, aliasByName.TryGetValue(name, out var alias) ? alias : name);
        }

        return merged.ToArray();
    }

    private static void AddModelMapping(List<TransparentProxyModelMapping> mappings, string? name, string? alias)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedName) ||
            mappings.Any(item => string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var normalizedAlias = alias?.Trim() ?? string.Empty;
        mappings.Add(new TransparentProxyModelMapping(normalizedName, normalizedAlias));
    }

    private static string BuildCacheScopeId(
        string id,
        string baseUrl,
        string prefix,
        IReadOnlyList<TransparentProxyModelMapping> mappings,
        string payloadRulesText,
        bool codexOAuthFastMode)
    {
        StringBuilder builder = new();
        builder.Append(id).Append('\u001F')
            .Append(baseUrl.Trim()).Append('\u001F')
            .Append(prefix.Trim()).Append('\u001F')
            .Append(payloadRulesText.Trim()).Append('\u001F')
            .Append(codexOAuthFastMode ? "codex-fast" : "codex-normal").Append('\u001F');
        foreach (var mapping in mappings.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.Alias, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(mapping.Name.Trim()).Append("=>").Append(mapping.Alias.Trim()).Append('\u001F');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return $"{id}:{Convert.ToHexString(hash[..6])}";
    }
}

public sealed record TransparentProxyModelMapping(string Name, string Alias)
{
    public string EffectiveAlias
        => string.IsNullOrWhiteSpace(Alias) ? Name.Trim() : Alias.Trim();
}

public static class TransparentProxyRouteStrategies
{
    public const string Smart = "smart";
    public const string Priority = "priority";
    public const string RoundRobin = "round-robin";
    public const string FillFirst = "fill-first";
    public const string LowestLatency = "lowest-latency";
    public const string SessionAffinity = "session-affinity";

    public static string Normalize(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Priority => Priority,
            RoundRobin => RoundRobin,
            "roundrobin" => RoundRobin,
            FillFirst => FillFirst,
            "fillfirst" => FillFirst,
            LowestLatency => LowestLatency,
            "lowestlatency" => LowestLatency,
            SessionAffinity => SessionAffinity,
            "sessionaffinity" => SessionAffinity,
            _ => Smart
        };
}

public sealed record TransparentProxyServerConfig(
    int Port,
    IReadOnlyList<TransparentProxyRoute> Routes,
    int RateLimitPerMinute,
    int MaxConcurrency,
    bool EnableFallback,
    bool EnableCache,
    int CacheTtlSeconds,
    bool RewriteModel,
    bool IgnoreTlsErrors,
    int UpstreamTimeoutSeconds)
{
    public int CacheMaxBytes { get; init; } = 2 * 1024 * 1024;

    public string RouteStrategy { get; init; } = TransparentProxyRouteStrategies.Smart;

    public int RequestRetry { get; init; } = 1;

    public int MaxRetryIntervalSeconds { get; init; } = 8;

    public int SessionAffinityTtlSeconds { get; init; } = 30 * 60;

    public int ModelCooldownSeconds { get; init; } = 120;

    public bool AllowRemoteManagement { get; init; }

    public string ManagementSecret { get; init; } = string.Empty;
}

public sealed record TransparentProxyLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Method,
    string Path,
    string RouteName,
    int StatusCode,
    long ElapsedMs,
    string Message,
    string ModelName = "-",
    string RequestId = "",
    string WireApi = "",
    string AttemptSummary = "",
    string IngressKind = "",
    string SourceApplication = "",
    string CaptureMode = "",
    string TargetHost = "",
    bool WasTunnelOnly = false,
    string ErrorType = "",
    string CacheState = "",
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheTokens = 0,
    string TraceId = "");

internal sealed record TransparentProxyIngressContext(
    string IngressKind,
    string SourceApplication,
    string CaptureMode,
    string TargetHost,
    bool WasTunnelOnly)
{
    public static TransparentProxyIngressContext Default { get; } =
        new("UnifiedLocalEndpoint", "本地统一出口", "显式 Base URL", string.Empty, false);
}

public sealed record TransparentProxyMetricsSnapshot(
    bool IsRunning,
    int Port,
    int ActiveRequests,
    int TotalRequests,
    int SuccessRequests,
    int FailedRequests,
    int FallbackRequests,
    int CacheHits,
    int RateLimitedRequests,
    long P50LatencyMs,
    long P95LatencyMs,
    int CacheEntryCount,
    IReadOnlyList<TransparentProxyRouteMetrics> Routes,
    long TotalOutputTokens,
    double TokensPerSecond,
    DateTimeOffset? LastTokenActivityAt,
    long PromptCacheTokens = 0,
    int ResponseCacheEntryCount = 0,
    int ModelListCacheEntryCount = 0,
    long ResponseCacheHits = 0,
    long ResponseCacheMisses = 0,
    long ResponseCacheStores = 0,
    long ResponseCacheEvictions = 0,
    int PromptSessionCacheEntryCount = 0,
    long PromptSessionCacheHits = 0,
    long PromptSessionCacheMisses = 0,
    IReadOnlyList<TransparentProxyModelPoolSnapshot>? ModelPools = null,
    int ResponseCacheInFlightKeys = 0,
    long ResponseCacheLeaseWaits = 0,
    IReadOnlyList<TransparentProxyUsageEvent>? RecentUsageEvents = null,
    IReadOnlyList<TransparentProxyIngressMetricsSnapshot>? Ingresses = null,
    long TotalInputTokens = 0);

public sealed record TransparentProxyIngressMetricsSnapshot(
    string IngressKind,
    string SourceApplication,
    string CaptureMode,
    long Requests,
    long Successes,
    long Failures,
    long TunnelOnlyRequests,
    long OutputTokens,
    long PromptCacheTokens,
    DateTimeOffset? LastRequestAt,
    DateTimeOffset? LastTokenActivityAt);

internal sealed class TransparentProxyIngressRuntimeState
{
    public TransparentProxyIngressRuntimeState(
        string ingressKind,
        string sourceApplication,
        string captureMode)
    {
        IngressKind = ingressKind;
        SourceApplication = sourceApplication;
        CaptureMode = captureMode;
    }

    public string IngressKind { get; }

    public string SourceApplication { get; }

    public string CaptureMode { get; }

    public long Requests { get; set; }

    public long Successes { get; set; }

    public long Failures { get; set; }

    public long TunnelOnlyRequests { get; set; }

    public long OutputTokens { get; set; }

    public long PromptCacheTokens { get; set; }

    public DateTimeOffset? LastRequestAt { get; set; }

    public DateTimeOffset? LastTokenActivityAt { get; set; }

    public TransparentProxyIngressMetricsSnapshot ToSnapshot()
        => new(
            IngressKind,
            SourceApplication,
            CaptureMode,
            Requests,
            Successes,
            Failures,
            TunnelOnlyRequests,
            OutputTokens,
            PromptCacheTokens,
            LastRequestAt,
            LastTokenActivityAt);
}

public sealed record TransparentProxyRouteMetrics(
    string Id,
    string Name,
    int Sent,
    int Success,
    int Failed,
    int LastStatusCode,
    long LastLatencyMs,
    int ConsecutiveFailures,
    int ConsecutiveSuccesses,
    string CircuitState,
    DateTimeOffset CircuitOpenUntil,
    DateTimeOffset LastSeenAt,
    string? PreferredWireApi,
    bool? ChatCompletionsSupported,
    bool? ResponsesSupported,
    bool? AnthropicMessagesSupported,
    DateTimeOffset? ProtocolCheckedAt,
    IReadOnlyList<TransparentProxyModelCooldownSnapshot>? ModelCooldowns = null);

public sealed record TransparentProxyModelCooldownSnapshot(
    string ModelName,
    int ConsecutiveFailures,
    DateTimeOffset CooldownUntil);

internal sealed record TransparentProxyCachedResponse(
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    int StatusCode,
    string ContentType,
    byte[] Body,
    string ModelName,
    int HitCount = 0);

internal sealed record TransparentProxyCachedModelsList(
    string Key,
    byte[] Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    DateTimeOffset ExpiresAt);

internal sealed record TransparentProxyUpstreamAttempt(bool DeliveredToClient, string RouteName, int StatusCode, string Message);

internal sealed record TransparentProxySessionRouteBinding(string RouteId, DateTimeOffset ExpiresAt);

internal sealed record TransparentProxyPreparedRequest(
    string WireApi,
    string ResponseClientWireApi,
    string UpstreamUrl,
    byte[] Body,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    bool NormalizeToChatCompletions,
    string ResponseModel,
    string UpstreamModel,
    bool IsToolExchange,
    bool PreferJsonStreamExtraction,
    IReadOnlyDictionary<string, string>? ToolNameAliases = null);
