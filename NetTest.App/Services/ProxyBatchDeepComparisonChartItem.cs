namespace NetTest.App.Services;

public enum ProxyBatchDeepComparisonBadgeState
{
    Pending,
    Running,
    Pass,
    Warn,
    Fail
}

public sealed record ProxyBatchDeepComparisonBadge(
    string Label,
    string Value,
    ProxyBatchDeepComparisonBadgeState State,
    string Title,
    string Description,
    string? DetailText = null);

public sealed record ProxyBatchDeepComparisonChartItem(
    int Rank,
    string Name,
    string BaseUrl,
    double? QuickChatLatencyMs,
    double? QuickTtftMs,
    string QuickCapabilityText,
    int CompletedCount,
    int TotalCount,
    string StageText,
    string IssueText,
    string Verdict,
    string UpdatedAtText,
    bool IsRunning,
    bool IsCompleted,
    IReadOnlyList<ProxyBatchDeepComparisonBadge> Badges);
