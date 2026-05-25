using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

/// <summary>
/// Detects protocol support for each model after a fetch operation.
/// Wraps the existing ModelProtocolProbeService for background detection.
/// Priority order: Anthropic > Responses > V1Chat.
/// </summary>
public sealed class ModelProtocolDetectionService
{
    private readonly ModelProtocolProbeService _probeService;

    public ModelProtocolDetectionService(
        ProxyEndpointProtocolProbeService protocolProbeService,
        ProxyEndpointModelCacheService cacheService)
    {
        _probeService = new ModelProtocolProbeService(protocolProbeService, cacheService);
    }

    /// <summary>
    /// Detects protocol support for all models in the background.
    /// Results are sorted by priority: Anthropic > Responses > V1Chat.
    /// Results are automatically saved to the SQLite cache.
    /// </summary>
    public async Task<IReadOnlyList<ModelProtocolInfo>> DetectAllAsync(
        ProxyEndpointSettings endpoint,
        IReadOnlyList<string> models,
        IProgress<ModelProtocolProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcomes = await _probeService.ProbeModelsAsync(
            endpoint,
            models,
            new ModelProtocolProbeOptions(
                ForceProbe: true,
                UseCache: false,
                SaveResult: true,
                MaxConcurrency: 8),
            progress,
            cancellationToken);

        return outcomes
            .Select(o => new ModelProtocolInfo(
                o.Model,
                ResolveProtocol(o),
                o.CheckedAt.UtcDateTime))
            .OrderBy(m => m.Protocol) // Anthropic=0, Responses=1, V1Chat=2, Unknown=3
            .ToList();
    }

    private static DetectedProtocol ResolveProtocol(ProxyModelProtocolProbeOutcome outcome)
    {
        if (outcome.AnthropicMessagesSupported) return DetectedProtocol.Anthropic;
        if (outcome.ResponsesSupported) return DetectedProtocol.Responses;
        if (outcome.ChatCompletionsSupported) return DetectedProtocol.V1Chat;
        return DetectedProtocol.Unknown;
    }
}

/// <summary>
/// Detected protocol for a model.
/// </summary>
public enum DetectedProtocol
{
    Anthropic = 0,
    Responses = 1,
    V1Chat = 2,
    Unknown = 3
}

/// <summary>
/// Protocol detection result for a single model.
/// </summary>
public sealed record ModelProtocolInfo(
    string Model,
    DetectedProtocol Protocol,
    DateTime DetectedAtUtc);
