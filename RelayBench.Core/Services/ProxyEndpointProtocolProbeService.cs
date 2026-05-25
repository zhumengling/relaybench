using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class ProxyEndpointProtocolProbeService
{
    private readonly ProxyDiagnosticsService _diagnosticsService;
    private readonly ProxyEndpointModelCacheService? _cacheService;

    public ProxyEndpointProtocolProbeService()
        : this(new ProxyDiagnosticsService(), null)
    {
    }

    public ProxyEndpointProtocolProbeService(
        ProxyDiagnosticsService diagnosticsService,
        ProxyEndpointModelCacheService? cacheService = null)
    {
        _diagnosticsService = diagnosticsService;
        _cacheService = cacheService;
    }

    public async Task<ProxyEndpointProtocolProbeResolution> ResolveAsync(
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ProxyEndpointProtocolProbeOptions.Default;
        if (!options.ForceProbe && options.UseCache && _cacheService is not null)
        {
            var cached = await _cacheService.TryResolveAsync(
                settings.BaseUrl,
                settings.ApiKey,
                settings.Model,
                cancellationToken).ConfigureAwait(false);
            if (TryBuildCurrentCachedResult(settings, cached, out var cachedResult))
            {
                return new ProxyEndpointProtocolProbeResolution(cachedResult, FromCache: true, cached);
            }
        }

        var result = await _diagnosticsService
            .ProbeProtocolAsync(settings, cancellationToken)
            .ConfigureAwait(false);
        if (options.SaveResult && _cacheService is not null)
        {
            try
            {
                await _cacheService.SaveProtocolProbeAsync(settings, result, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        return new ProxyEndpointProtocolProbeResolution(result, FromCache: false, CachedInfo: null);
    }

    private static bool TryBuildCurrentCachedResult(
        ProxyEndpointSettings settings,
        CachedProxyEndpointModelInfo? cached,
        out ProxyEndpointProtocolProbeResult result)
    {
        result = default!;
        if (cached is null ||
            (cached.ProtocolProbeVersion ?? 0) < ProxyWireApiProbeService.CurrentProtocolProbeVersion ||
            !HasProtocolProbe(cached))
        {
            return false;
        }

        var chatSupported = cached.ChatCompletionsSupported == true;
        var responsesSupported = cached.ResponsesSupported == true;
        var anthropicSupported = cached.AnthropicMessagesSupported == true;
        var preferredWireApi = ProxyWireApiProbeService.ResolvePreferredWireApi(
            chatSupported,
            responsesSupported,
            anthropicSupported) ??
            ProxyWireApiProbeService.NormalizeWireApi(cached.PreferredWireApi);
        result = new ProxyEndpointProtocolProbeResult(
            cached.CheckedAt,
            FirstNonEmpty(cached.BaseUrl, settings.BaseUrl),
            FirstNonEmpty(cached.Model, settings.Model),
            chatSupported,
            responsesSupported,
            anthropicSupported,
            preferredWireApi,
            BuildCachedSummary(
                FirstNonEmpty(cached.Model, settings.Model),
                chatSupported,
                responsesSupported,
                anthropicSupported,
                preferredWireApi,
                cached.CheckedAt),
            Error: null);
        return true;
    }

    private static bool HasProtocolProbe(CachedProxyEndpointModelInfo cached)
        => cached.ChatCompletionsSupported.HasValue ||
           cached.ResponsesSupported.HasValue ||
           cached.AnthropicMessagesSupported.HasValue ||
           !string.IsNullOrWhiteSpace(cached.PreferredWireApi);

    private static string BuildCachedSummary(
        string model,
        bool chatSupported,
        bool responsesSupported,
        bool anthropicSupported,
        string? preferredWireApi,
        DateTimeOffset checkedAt)
        => ProxyWireApiProbeService.BuildSummary(
               model,
               chatSupported,
               responsesSupported,
               anthropicSupported,
               preferredWireApi) +
           $" Cached at {checkedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}.";

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}

public sealed record ProxyEndpointProtocolProbeOptions(
    bool ForceProbe,
    bool UseCache,
    bool SaveResult)
{
    public static ProxyEndpointProtocolProbeOptions Default { get; } = new(
        ForceProbe: false,
        UseCache: true,
        SaveResult: true);
}

public sealed record ProxyEndpointProtocolProbeResolution(
    ProxyEndpointProtocolProbeResult Result,
    bool FromCache,
    CachedProxyEndpointModelInfo? CachedInfo);
