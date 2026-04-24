using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

public sealed class ProxyBatchComparisonChartRowViewModel
{
    private const double MinimumVisibleMetricRatio = 0.08d;

    private ProxyBatchComparisonChartRowViewModel(
        ProxyBatchComparisonChartItem item,
        double maxChatLatencyMs,
        double maxTtftMs,
        double maxTokensPerSecond,
        bool shouldAnimateReveal)
    {
        Rank = item.Rank;
        RankText = $"#{item.Rank}";
        Name = string.IsNullOrWhiteSpace(item.Name) ? "未命名入口" : item.Name;
        BaseUrl = string.IsNullOrWhiteSpace(item.BaseUrl) ? "-" : item.BaseUrl;
        CompositeText = string.IsNullOrWhiteSpace(item.CompositeText) ? "-" : item.CompositeText;
        StabilityText = string.IsNullOrWhiteSpace(item.StabilityText) ? "-" : item.StabilityText;
        ChatLatencyText = FormatMilliseconds(item.ChatLatencyMs);
        TtftText = FormatMilliseconds(item.TtftMs);
        TokensPerSecondText = FormatTokensPerSecond(item.TokensPerSecond);
        Verdict = string.IsNullOrWhiteSpace(item.Verdict) ? "等待结果" : item.Verdict;
        SecondaryText = string.IsNullOrWhiteSpace(item.SecondaryText) ? "等待入口组返回阶段结果" : item.SecondaryText;
        RunCountText = item.RunCount > 0 ? $"{item.RunCount} 轮" : "待运行";
        IsRunning = item.RunCount <= 0;
        IsCompleted = item.RunCount > 0;
        StatusTone = ResolveStatusTone(item);
        StatusText = ResolveStatusText(item);
        CompositeRatio = NormalizeDirectRatio(item.CompositeScore, 100);
        ChatLatencyRatio = NormalizeInverseRatio(item.ChatLatencyMs, maxChatLatencyMs);
        TtftRatio = NormalizeInverseRatio(item.TtftMs, maxTtftMs);
        TokensPerSecondRatio = NormalizeDirectRatio(item.TokensPerSecond, maxTokensPerSecond);
        ShouldAnimateReveal = shouldAnimateReveal;
    }

    public int Rank { get; }

    public string RankText { get; }

    public string Name { get; }

    public string BaseUrl { get; }

    public string StatusText { get; }

    public string StatusTone { get; }

    public string CompositeText { get; }

    public string StabilityText { get; }

    public string ChatLatencyText { get; }

    public string TtftText { get; }

    public string TokensPerSecondText { get; }

    public string Verdict { get; }

    public string SecondaryText { get; }

    public string RunCountText { get; }

    public bool IsRunning { get; }

    public bool IsCompleted { get; }

    public double CompositeRatio { get; }

    public double ChatLatencyRatio { get; }

    public double TtftRatio { get; }

    public double TokensPerSecondRatio { get; }

    public bool ShouldAnimateReveal { get; }

    public static IReadOnlyList<ProxyBatchComparisonChartRowViewModel> CreateRows(
        IReadOnlyList<ProxyBatchComparisonChartItem> items,
        IReadOnlySet<string>? revealKeys = null)
    {
        if (items.Count == 0)
        {
            return Array.Empty<ProxyBatchComparisonChartRowViewModel>();
        }

        var maxChatLatencyMs = items
            .Select(item => item.ChatLatencyMs)
            .Where(value => value is > 0)
            .DefaultIfEmpty(0)
            .Max() ?? 0;
        var maxTtftMs = items
            .Select(item => item.TtftMs)
            .Where(value => value is > 0)
            .DefaultIfEmpty(0)
            .Max() ?? 0;
        var maxTokensPerSecond = items
            .Select(item => item.TokensPerSecond)
            .Where(value => value is > 0)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

        return items
            .OrderBy(item => item.Rank)
            .Select(item => new ProxyBatchComparisonChartRowViewModel(
                item,
                maxChatLatencyMs,
                maxTtftMs,
                maxTokensPerSecond,
                revealKeys?.Contains(CreateRevealKey(item)) == true))
            .ToArray();
    }

    public static string CreateRevealKey(ProxyBatchComparisonChartItem item)
        => string.Concat(item.Name, "|", item.BaseUrl);

    private static string ResolveStatusTone(ProxyBatchComparisonChartItem item)
    {
        if (item.RunCount <= 0)
        {
            return "Running";
        }

        if (item.CompositeScore >= 80 || item.Rank == 1)
        {
            return "Success";
        }

        if (item.CompositeScore >= 55)
        {
            return "Warn";
        }

        return "Error";
    }

    private static string ResolveStatusText(ProxyBatchComparisonChartItem item)
    {
        if (item.RunCount <= 0)
        {
            return "进行中";
        }

        if (item.Rank == 1)
        {
            return "当前推荐";
        }

        return item.CompositeScore >= 55 ? "可用" : "偏弱";
    }

    private static string FormatMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:0} ms" : "-";

    private static string FormatTokensPerSecond(double? value)
        => value.HasValue ? $"{value.Value:0.0} tok/s" : "-";

    private static double NormalizeDirectRatio(double? value, double maxValue)
    {
        if (value is not > 0 || maxValue <= 0)
        {
            return 0;
        }

        return Math.Clamp(value.Value / maxValue, MinimumVisibleMetricRatio, 1d);
    }

    private static double NormalizeInverseRatio(double? value, double maxValue)
    {
        if (value is not > 0 || maxValue <= 0)
        {
            return 0;
        }

        return Math.Clamp((maxValue - value.Value) / maxValue, MinimumVisibleMetricRatio, 1d);
    }
}
