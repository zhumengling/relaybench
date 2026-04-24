using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

public sealed class ProxyConcurrencyChartRowViewModel
{
    private const double MinimumVisibleRatio = 0.08d;

    private ProxyConcurrencyChartRowViewModel(
        ProxyConcurrencyChartItem item,
        double maxP50ChatLatencyMs,
        double maxP95TtftMs,
        double maxTokensPerSecond)
    {
        Concurrency = item.Concurrency;
        IsPending = item.TotalRequests <= 0;
        ConcurrencyText = $"x{item.Concurrency}";
        SuccessText = IsPending ? "--" : $"{item.SuccessCount}/{Math.Max(item.TotalRequests, 1)}";
        SuccessRateText = IsPending ? "等待" : $"{item.SuccessRate:0.#}%";
        P50ChatLatencyText = IsPending ? "--" : FormatMilliseconds(item.P50ChatLatencyMs);
        P95TtftText = IsPending ? "--" : FormatMilliseconds(item.P95TtftMs);
        TokensPerSecondText = IsPending ? "--" : FormatTokensPerSecond(item.AverageTokensPerSecond);
        FailureText = IsPending ? "等待执行" : $"429 {item.RateLimitedCount} 路 5xx {item.ServerErrorCount} 路 TO {item.TimeoutCount}";
        Verdict = string.IsNullOrWhiteSpace(item.Verdict) ? (IsPending ? "等待中" : "瑙傚療") : item.Verdict;
        Summary = string.IsNullOrWhiteSpace(item.Summary) ? (IsPending ? "等待该并发档位执行。" : "鏆傛棤鎽樿") : item.Summary;
        IsStableLimit = item.IsStableLimit;
        IsRateLimitStart = item.IsRateLimitStart;
        IsHighRisk = item.IsHighRisk;
        SuccessRatio = IsPending ? 0 : NormalizeDirect(item.SuccessRate, 100);
        P50ChatLatencyRatio = IsPending ? 0 : NormalizeInverse(item.P50ChatLatencyMs, maxP50ChatLatencyMs);
        P95TtftRatio = IsPending ? 0 : NormalizeInverse(item.P95TtftMs, maxP95TtftMs);
        TokensPerSecondRatio = IsPending ? 0 : NormalizeDirect(item.AverageTokensPerSecond, maxTokensPerSecond);
        StatusTone = ResolveStatusTone(item);
    }

    public int Concurrency { get; }

    public bool IsPending { get; }

    public string ConcurrencyText { get; }

    public string SuccessText { get; }

    public string SuccessRateText { get; }

    public string P50ChatLatencyText { get; }

    public string P95TtftText { get; }

    public string TokensPerSecondText { get; }

    public string FailureText { get; }

    public string Verdict { get; }

    public string Summary { get; }

    public bool IsStableLimit { get; }

    public bool IsRateLimitStart { get; }

    public bool IsHighRisk { get; }

    public double SuccessRatio { get; }

    public double P50ChatLatencyRatio { get; }

    public double P95TtftRatio { get; }

    public double TokensPerSecondRatio { get; }

    public string StatusTone { get; }

    public static IReadOnlyList<ProxyConcurrencyChartRowViewModel> CreateRows(
        IReadOnlyList<ProxyConcurrencyChartItem> items)
    {
        if (items.Count == 0)
        {
            return Array.Empty<ProxyConcurrencyChartRowViewModel>();
        }

        var maxP50 = items.Select(item => item.P50ChatLatencyMs).Where(value => value is > 0).DefaultIfEmpty(0).Max() ?? 0;
        var maxTtft = items.Select(item => item.P95TtftMs).Where(value => value is > 0).DefaultIfEmpty(0).Max() ?? 0;
        var maxTokens = items.Select(item => item.AverageTokensPerSecond).Where(value => value is > 0).DefaultIfEmpty(0).Max() ?? 0;

        return items
            .OrderBy(item => item.Concurrency)
            .Select(item => new ProxyConcurrencyChartRowViewModel(item, maxP50, maxTtft, maxTokens))
            .ToArray();
    }

    private static string ResolveStatusTone(ProxyConcurrencyChartItem item)
    {
        if (item.TotalRequests <= 0)
        {
            return "Running";
        }

        if (item.IsHighRisk || item.SuccessRate < 80 || item.TimeoutCount > 0 || item.ServerErrorCount > 0)
        {
            return "Error";
        }

        if (item.IsRateLimitStart || item.RateLimitedCount > 0 || item.SuccessRate < 95)
        {
            return "Warn";
        }

        return "Success";
    }

    private static double NormalizeDirect(double? value, double maxValue)
    {
        if (value is not > 0 || maxValue <= 0)
        {
            return 0;
        }

        return Math.Clamp(value.Value / maxValue, MinimumVisibleRatio, 1d);
    }

    private static double NormalizeInverse(double? value, double maxValue)
    {
        if (value is not > 0 || maxValue <= 0)
        {
            return 0;
        }

        return Math.Clamp((maxValue - value.Value) / maxValue, MinimumVisibleRatio, 1d);
    }

    private static string FormatMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:0} ms" : "-";

    private static string FormatTokensPerSecond(double? value)
        => value.HasValue ? $"{value.Value:0.0} tok/s" : "-";
}
