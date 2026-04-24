using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

public sealed class ProxySingleCapabilityChartRowViewModel
{
    private const double MinimumVisibleMetricRatio = 0.08d;

    private ProxySingleCapabilityChartRowViewModel(
        ProxySingleCapabilityChartItem item,
        bool isSectionFirst,
        double maxMetricValueMs,
        bool shouldAnimateMetricReveal)
    {
        Order = item.Order;
        SectionName = item.SectionName;
        SectionHint = item.SectionHint;
        Name = item.Name;
        StatusText = string.IsNullOrWhiteSpace(item.StatusText) ? "-" : item.StatusText;
        IsCompleted = item.IsCompleted;
        Success = item.Success;
        IsRunning = !item.IsCompleted;
        StatusCodeText = item.StatusCode?.ToString() ?? "-";
        MetricText = string.IsNullOrWhiteSpace(item.MetricText) ? "-" : item.MetricText;
        MetricRatio = NormalizeMetricRatio(item.MetricValueMs, maxMetricValueMs);
        ReceivedDoneText = item.ReceivedDone ? "done" : "no done";
        DetailText = string.IsNullOrWhiteSpace(item.DetailText) ? "-" : item.DetailText;
        PreviewText = string.IsNullOrWhiteSpace(item.PreviewText) ? "-" : item.PreviewText;
        IsSectionFirst = isSectionFirst;
        StatusTone = ResolveStatusTone(item);
        ShouldAnimateMetricReveal = shouldAnimateMetricReveal;
    }

    public int Order { get; }

    public string SectionName { get; }

    public string SectionHint { get; }

    public string Name { get; }

    public string StatusText { get; }

    public bool IsCompleted { get; }

    public bool Success { get; }

    public bool IsRunning { get; }

    public string StatusCodeText { get; }

    public string MetricText { get; }

    public double MetricRatio { get; }

    public string ReceivedDoneText { get; }

    public string DetailText { get; }

    public string PreviewText { get; }

    public bool IsSectionFirst { get; }

    public string StatusTone { get; }

    public bool ShouldAnimateMetricReveal { get; }

    public static IReadOnlyList<ProxySingleCapabilityChartRowViewModel> CreateRows(
        IReadOnlyList<ProxySingleCapabilityChartItem> items,
        IReadOnlySet<string>? metricRevealKeys = null)
    {
        if (items.Count == 0)
        {
            return Array.Empty<ProxySingleCapabilityChartRowViewModel>();
        }

        var maxMetricValueMs = items
            .Select(item => item.MetricValueMs)
            .Where(value => value is > 0)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

        var rows = new List<ProxySingleCapabilityChartRowViewModel>(items.Count);
        string? previousSectionName = null;
        foreach (var item in items.OrderBy(item => item.Order))
        {
            var isSectionFirst = !string.Equals(previousSectionName, item.SectionName, StringComparison.Ordinal);
            rows.Add(new ProxySingleCapabilityChartRowViewModel(
                item,
                isSectionFirst,
                maxMetricValueMs,
                metricRevealKeys?.Contains(CreateMetricRevealKey(item)) == true));
            previousSectionName = item.SectionName;
        }

        return rows;
    }

    public static string CreateMetricRevealKey(ProxySingleCapabilityChartItem item)
        => string.Concat(item.SectionName, "|", item.Order.ToString(System.Globalization.CultureInfo.InvariantCulture), "|", item.Name);

    private static double NormalizeMetricRatio(double? metricValueMs, double maxMetricValueMs)
    {
        if (metricValueMs is not > 0 || maxMetricValueMs <= 0)
        {
            return 0;
        }

        return Math.Clamp(metricValueMs.Value / maxMetricValueMs, MinimumVisibleMetricRatio, 1d);
    }

    private static string ResolveStatusTone(ProxySingleCapabilityChartItem item)
    {
        if (!item.IsCompleted)
        {
            return "Running";
        }

        if (item.Success)
        {
            return "Success";
        }

        return item.StatusCode is null && IsPendingStatus(item.StatusText) ? "Pending" : "Error";
    }

    private static bool IsPendingStatus(string statusText)
        => statusText.Contains("未运行", StringComparison.Ordinal) ||
           statusText.Contains("等待", StringComparison.Ordinal) ||
           statusText.Contains("待", StringComparison.Ordinal);
}
