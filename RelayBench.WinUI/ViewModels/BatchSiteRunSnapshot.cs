namespace RelayBench.WinUI.ViewModels;

public sealed record BatchSiteRunSnapshot(
    string Name,
    double LatencyMs,
    double? TtftMs,
    double Throughput,
    double SuccessRate,
    double Score,
    int PassCount = 0,
    int TotalCount = 0,
    string CapabilitySummary = "--",
    string CacheState = "--",
    string ProtocolSummary = "未探测",
    string LatestResult = "--",
    string VerdictText = "--",
    string SecondaryText = "--",
    int RunCount = 1)
{
    public string CapabilityText => TotalCount > 0
        ? $"{Math.Clamp(PassCount, 0, TotalCount)}/{TotalCount}"
        : "--";

    public string RunCountText => RunCount > 0 ? $"{RunCount} 轮" : "待运行";
}
