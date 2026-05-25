namespace RelayBench.Core.Models;

public sealed record ProxyCapabilityMatrixResult(
    IReadOnlyList<ProxyProbeScenarioResult> Scenarios,
    string Summary);
