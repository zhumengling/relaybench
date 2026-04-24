using System.Windows;
using System.Windows.Media;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class ProxyStabilityTrendChartRowViewModel
{
    private ProxyStabilityTrendChartRowViewModel(
        string title,
        string valueText,
        string statusText,
        string hintText,
        string valueSummaryText,
        PointCollection sparklinePoints,
        IReadOnlyList<ProxyStabilityTrendSparklineMarker> sparklineMarkers,
        string strokeBrush,
        bool isRunning,
        bool isGood)
    {
        Title = title;
        ValueText = valueText;
        StatusText = statusText;
        HintText = hintText;
        ValueSummaryText = valueSummaryText;
        SparklinePoints = sparklinePoints;
        SparklineMarkers = sparklineMarkers;
        StrokeBrush = strokeBrush;
        IsRunning = isRunning;
        Tone = isGood ? "Success" : "Warn";
    }

    public string Title { get; }

    public string ValueText { get; }

    public string StatusText { get; }

    public string HintText { get; }

    public string ValueSummaryText { get; }

    public PointCollection SparklinePoints { get; }

    public IReadOnlyList<ProxyStabilityTrendSparklineMarker> SparklineMarkers { get; }

    public string StrokeBrush { get; }

    public bool IsRunning { get; }

    public string Tone { get; }

    public string RunningText => IsRunning ? "实时中" : string.Empty;

    public static IReadOnlyList<ProxyStabilityTrendChartRowViewModel> CreateRows(
        IReadOnlyList<ProxyTrendEntry> entries,
        int requestedRounds,
        bool isRunning)
    {
        var realEntries = entries
            .Where(entry => entry.ChatLatencyMs is not null || entry.StreamFirstTokenLatencyMs is not null || entry.FullSuccessRate is not null)
            .ToArray();

        if (realEntries.Length == 0)
        {
            return
            [
                CreatePlaceholder("稳定性", "0%", "#16A34A", requestedRounds, isRunning),
                CreatePlaceholder("普通延迟", "-", "#2563EB", requestedRounds, isRunning),
                CreatePlaceholder("TTFT", "-", "#F97316", requestedRounds, isRunning)
            ];
        }

        var latest = realEntries[^1];
        var completedCount = realEntries.Length;
        var stabilityValues = realEntries.Select(entry => entry.FullSuccessRate).ToArray();
        var chatValues = realEntries.Select(entry => entry.ChatLatencyMs).ToArray();
        var ttftValues = realEntries.Select(entry => entry.StreamFirstTokenLatencyMs).ToArray();
        var stability = latest.FullSuccessRate ?? 0;
        var chatMax = MaxPositive(chatValues);
        var ttftMax = MaxPositive(ttftValues);
        var failedRounds = stabilityValues.Count(value => value is < 100);

        return
        [
            BuildStabilityRow(stabilityValues, completedCount, requestedRounds, isRunning, stability, failedRounds),
            new(
                "普通延迟",
                FormatMilliseconds(latest.ChatLatencyMs),
                BuildLatencyStatus(chatValues),
                latest.ChatLatencyMs is null ? "暂无有效数据 | 越低越好" : "最新一轮普通对话 | 越低越好",
                BuildMillisecondsSummary(chatValues),
                BuildSparkline(chatValues, chatMax, inverse: true, verticalOffset: 0),
                BuildSparklineMarkers(chatValues, chatMax, inverse: true, highlightLowValues: false, verticalOffset: 0),
                "#2563EB",
                isRunning,
                latest.ChatLatencyMs is > 0),
            new(
                "TTFT",
                FormatMilliseconds(latest.StreamFirstTokenLatencyMs),
                BuildLatencyStatus(ttftValues),
                latest.StreamFirstTokenLatencyMs is null ? "暂无有效数据 | 越低越好" : "最新一轮首字响应 | 越低越好",
                BuildMillisecondsSummary(ttftValues),
                BuildSparkline(ttftValues, ttftMax, inverse: true, verticalOffset: 5),
                BuildSparklineMarkers(ttftValues, ttftMax, inverse: true, highlightLowValues: false, verticalOffset: 5),
                "#F97316",
                isRunning,
                latest.StreamFirstTokenLatencyMs is > 0)
        ];
    }

    private static ProxyStabilityTrendChartRowViewModel BuildStabilityRow(
        IReadOnlyList<double?> stabilityValues,
        int completedCount,
        int requestedRounds,
        bool isRunning,
        double stability,
        int failedRounds)
    {
        var hasFlatPerfectLine = stabilityValues.Count(value => value is > 0) > 0 &&
                                 stabilityValues.All(value => value is >= 100);

        return new ProxyStabilityTrendChartRowViewModel(
            "稳定性",
            $"{stability:0}%",
            stability >= 100 ? "全部通过" : failedRounds > 0 ? $"{failedRounds} 轮异常" : "部分通过",
            BuildProgressHint(completedCount, requestedRounds),
            BuildPercentSummary(stabilityValues),
            hasFlatPerfectLine ? BuildFlatSparkline(0.62) : BuildSparkline(stabilityValues, 100, inverse: false, verticalOffset: 0),
            hasFlatPerfectLine ? BuildFlatMarkers(0.62) : BuildSparklineMarkers(stabilityValues, 100, inverse: false, highlightLowValues: true, verticalOffset: 0),
            "#16A34A",
            isRunning,
            stability >= 80);
    }

    private static ProxyStabilityTrendChartRowViewModel CreatePlaceholder(
        string title,
        string valueText,
        string strokeBrush,
        int requestedRounds,
        bool isRunning)
        => new(
            title,
            valueText,
            "等待结果",
            title == "稳定性" ? BuildProgressHint(0, requestedRounds) : "暂无有效数据 | 越低越好",
            "尚未完成首轮",
            BuildFlatSparkline(0),
            BuildFlatMarkers(0),
            strokeBrush,
            isRunning,
            false);

    private static string BuildProgressHint(int completedCount, int requestedRounds)
        => requestedRounds > 0
            ? $"已完成 {completedCount}/{requestedRounds} 轮 | 越高越好"
            : $"已完成 {completedCount} 轮 | 越高越好";

    private static string FormatMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:0} ms" : "-";

    private static double MaxPositive(IEnumerable<double?> values)
        => values.Where(value => value is > 0).DefaultIfEmpty(0).Max() ?? 0;

    private static PointCollection BuildFlatSparkline(double ratio)
    {
        var y = 8 + ((1 - Math.Clamp(ratio, 0, 1)) * 30);
        return [new Point(0, y), new Point(520, y)];
    }

    private static IReadOnlyList<ProxyStabilityTrendSparklineMarker> BuildFlatMarkers(double ratio)
    {
        var y = 8 + ((1 - Math.Clamp(ratio, 0, 1)) * 30);
        return [new ProxyStabilityTrendSparklineMarker(0, y, true, false), new ProxyStabilityTrendSparklineMarker(520, y, true, true)];
    }

    private static PointCollection BuildSparkline(IEnumerable<double?> values, double maxValue, bool inverse, double verticalOffset)
    {
        var numericValues = values.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        if (numericValues.Length == 0 || maxValue <= 0)
        {
            return BuildFlatSparkline(0);
        }

        const double width = 520d;
        const double topPadding = 3d;
        const double plotHeight = 42d;
        var localMin = numericValues.Min();
        var localMax = numericValues.Max();
        var points = new PointCollection();
        if (numericValues.Length == 1)
        {
            var ratio = NormalizeSparklineRatio(numericValues[0], localMin, localMax, inverse);
            var y = ClampSparklineY(topPadding + ((1 - ratio) * plotHeight) + verticalOffset);
            points.Add(new Point(0, y));
            points.Add(new Point(width, y));
            return points;
        }

        for (var index = 0; index < numericValues.Length; index++)
        {
            var x = width * index / (numericValues.Length - 1);
            var ratio = NormalizeSparklineRatio(numericValues[index], localMin, localMax, inverse);
            var y = ClampSparklineY(topPadding + ((1 - ratio) * plotHeight) + verticalOffset);
            points.Add(new Point(x, y));
        }

        return points;
    }

    private static IReadOnlyList<ProxyStabilityTrendSparklineMarker> BuildSparklineMarkers(
        IEnumerable<double?> values,
        double maxValue,
        bool inverse,
        bool highlightLowValues,
        double verticalOffset)
    {
        var numericValues = values.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        if (numericValues.Length == 0 || maxValue <= 0)
        {
            return BuildFlatMarkers(0);
        }

        const double width = 520d;
        const double topPadding = 3d;
        const double plotHeight = 42d;
        var localMin = numericValues.Min();
        var localMax = numericValues.Max();
        var markers = new List<ProxyStabilityTrendSparklineMarker>(numericValues.Length == 1 ? 2 : numericValues.Length);
        if (numericValues.Length == 1)
        {
            var ratio = NormalizeSparklineRatio(numericValues[0], localMin, localMax, inverse);
            var y = ClampSparklineY(topPadding + ((1 - ratio) * plotHeight) + verticalOffset);
            var abnormal = highlightLowValues && numericValues[0] < 100;
            markers.Add(new ProxyStabilityTrendSparklineMarker(0, y, abnormal, false));
            markers.Add(new ProxyStabilityTrendSparklineMarker(width, y, abnormal, true));
            return markers;
        }

        for (var index = 0; index < numericValues.Length; index++)
        {
            var value = numericValues[index];
            var x = width * index / (numericValues.Length - 1);
            var ratio = NormalizeSparklineRatio(value, localMin, localMax, inverse);
            var y = ClampSparklineY(topPadding + ((1 - ratio) * plotHeight) + verticalOffset);
            var abnormal = highlightLowValues ? value < 100 : value >= maxValue;
            markers.Add(new ProxyStabilityTrendSparklineMarker(x, y, abnormal, index == numericValues.Length - 1));
        }

        return markers;
    }

    private static double NormalizeSparklineRatio(double value, double minValue, double maxValue, bool inverse)
    {
        if (maxValue <= 0 || value <= 0)
        {
            return 0;
        }

        if (Math.Abs(maxValue - minValue) < 0.0001)
        {
            return 0.55;
        }

        var ratio = (value - minValue) / (maxValue - minValue);
        if (inverse)
        {
            ratio = 1 - ratio;
        }

        return Math.Clamp(ratio, 0, 1);
    }

    private static double ClampSparklineY(double y)
        => Math.Clamp(y, 3d, 45d);

    private static string BuildPercentSummary(IEnumerable<double?> values)
    {
        var numericValues = values.Where(value => value is >= 0).Select(value => value!.Value).ToArray();
        return numericValues.Length == 0
            ? "暂无有效数据"
            : $"最低 {numericValues.Min():0}% · 最高 {numericValues.Max():0}% · 平均 {numericValues.Average():0}%";
    }

    private static string BuildMillisecondsSummary(IEnumerable<double?> values)
    {
        var numericValues = values.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        return numericValues.Length == 0
            ? "暂无有效数据"
            : $"最低 {numericValues.Min():0} ms · 最高 {numericValues.Max():0} ms · 平均 {numericValues.Average():0} ms";
    }

    private static string BuildLatencyStatus(IEnumerable<double?> values)
    {
        var numericValues = values.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        if (numericValues.Length < 2)
        {
            return numericValues.Length == 0 ? "等待结果" : "首轮结果";
        }

        var average = numericValues.Average();
        if (average <= 0)
        {
            return "等待结果";
        }

        var swing = (numericValues.Max() - numericValues.Min()) / average;
        return swing switch
        {
            < 0.18 => "稳定",
            < 0.35 => "轻微波动",
            _ => "波动偏高"
        };
    }
}

public sealed record ProxyStabilityTrendSparklineMarker(double X, double Y, bool IsAbnormal, bool IsLatest);
