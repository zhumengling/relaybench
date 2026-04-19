namespace NetTest.App.Infrastructure;

public sealed record ProxyTrendEntry(
    DateTimeOffset Timestamp,
    string BaseUrl,
    string Label,
    string Mode,
    string RequestedModel,
    string? EffectiveModel,
    bool ModelsSuccess,
    bool ChatSuccess,
    bool StreamSuccess,
    double? FullSuccessRate,
    double? ChatSuccessRate,
    double? StreamSuccessRate,
    double? ChatLatencyMs,
    double? StreamFirstTokenLatencyMs,
    int? HealthScore,
    int? BatchScore,
    string Summary,
    string? Error);
