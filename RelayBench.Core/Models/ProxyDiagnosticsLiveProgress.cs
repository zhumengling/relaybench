namespace RelayBench.Core.Models;

public sealed record ProxyDiagnosticsLiveProgress(
    DateTimeOffset ReportedAt,
    string BaseUrl,
    string RequestedModel,
    string? EffectiveModel,
    int CompletedScenarioCount,
    int TotalScenarioCount,
    int ModelCount,
    IReadOnlyList<string> SampleModels,
    ProxyProbeScenarioKind CurrentScenario,
    ProxyProbeScenarioResult CurrentScenarioResult,
    IReadOnlyList<ProxyProbeScenarioResult> ScenarioResults);
