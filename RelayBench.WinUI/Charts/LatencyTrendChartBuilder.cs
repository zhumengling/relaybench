using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using SkiaSharp;

namespace RelayBench.WinUI.Charts;

/// <summary>
/// Builds a latency trend chart with P50, P95, and P99 percentile lines.
/// </summary>
public static class LatencyTrendChartBuilder
{
    /// <summary>
    /// Builds three line series representing P50, P95, and P99 latency percentiles.
    /// </summary>
    /// <param name="p50">P50 延迟 values per request.</param>
    /// <param name="p95">P95 latency values per request.</param>
    /// <param name="p99">P99 latency values per request.</param>
    /// <param name="theme">The current application theme for color selection.</param>
    /// <returns>A tuple containing the series array and whether the data is empty.</returns>
    public static (ISeries[] Series, bool IsEmpty) BuildP50P95P99(
        IReadOnlyList<double> p50,
        IReadOnlyList<double> p95,
        IReadOnlyList<double> p99,
        ElementTheme theme)
    {
        LiveChartsInitializer.EnsureInitialized();
        var colors = ChartPalette.ForTheme(theme);
        var isEmpty = p50.Count == 0 && p95.Count == 0 && p99.Count == 0;
        var p50Values = p50.Count == 0 ? [0d] : p50.ToArray();
        var p95Values = p95.Count == 0 ? [0d] : p95.ToArray();
        var p99Values = p99.Count == 0 ? [0d] : p99.ToArray();

        var series = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = p50Values,
                Name = "P50",
                GeometrySize = 0,
                LineSmoothness = 0.5,
                Stroke = new SolidColorPaint(colors[0]) { StrokeThickness = 2 },
                Fill = null,
            },
            new LineSeries<double>
            {
                Values = p95Values,
                Name = "P95",
                GeometrySize = 0,
                LineSmoothness = 0.5,
                Stroke = new SolidColorPaint(colors[1]) { StrokeThickness = 2 },
                Fill = null,
            },
            new LineSeries<double>
            {
                Values = p99Values,
                Name = "P99",
                GeometrySize = 0,
                LineSmoothness = 0.5,
                Stroke = new SolidColorPaint(colors[2]) { StrokeThickness = 2 },
                Fill = null,
            },
        };

        return (series, isEmpty);
    }

    /// <summary>
    /// Builds the X and Y axes for the latency trend chart.
    /// </summary>
    /// <param name="theme">The current application theme for label colors.</param>
    /// <returns>An array containing the Y-axis and X-axis configurations.</returns>
    public static Axis[] BuildAxes(ElementTheme theme)
    {
        LiveChartsInitializer.EnsureInitialized();
        var labelPaint = ChartPalette.AxisTextPaint(theme);
        var separatorPaint = ChartPalette.SeparatorPaint(theme);

        return
        [
            new Axis
            {
                Name = "延迟 (ms)",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
                SeparatorsPaint = separatorPaint,
                MinLimit = 0,
                TextSize = 11,
                NameTextSize = 12,
            },
        ];
    }

    /// <summary>
    /// Builds the X-axes for the latency trend chart.
    /// </summary>
    /// <param name="theme">The current application theme for label colors.</param>
    /// <returns>An array containing the X-axis configuration.</returns>
    public static Axis[] BuildXAxes(ElementTheme theme)
    {
        LiveChartsInitializer.EnsureInitialized();
        var labelPaint = ChartPalette.AxisTextPaint(theme);
        var separatorPaint = ChartPalette.SeparatorPaint(theme);

        return
        [
            new Axis
            {
                Name = "请求序号",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
                SeparatorsPaint = separatorPaint,
                MinStep = 1,
                TextSize = 11,
                NameTextSize = 12,
            },
        ];
    }
}
