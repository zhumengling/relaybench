using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using SkiaSharp;

namespace RelayBench.WinUI.Charts;

/// <summary>
/// Builds an area chart (line series with fill) showing throughput in tokens per second.
/// </summary>
public static class ThroughputAreaChartBuilder
{
    /// <summary>
    /// Builds a line series with fill representing throughput over sequential requests.
    /// Uses theme color index 3 (green) for the area fill.
    /// </summary>
    /// <param name="tokensPerSec">Tokens per second values per request.</param>
    /// <param name="theme">The current application theme for color selection.</param>
    /// <returns>A tuple containing the series array and whether the data is empty.</returns>
    public static (ISeries[] Series, bool IsEmpty) Build(
        IReadOnlyList<double> tokensPerSec,
        ElementTheme theme)
    {
        LiveChartsInitializer.EnsureInitialized();
        var colors = ChartPalette.ForTheme(theme);
        var areaColor = colors[3];
        var fillColor = areaColor.WithAlpha(80);
        var values = tokensPerSec.Count == 0 ? [0d] : tokensPerSec.ToArray();

        var series = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = values,
                Name = "\u541E\u5410\u91CF",
                GeometrySize = 0,
                LineSmoothness = 0.5,
                Stroke = new SolidColorPaint(areaColor) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(fillColor),
            },
        };

        return (series, false);
    }

    /// <summary>
    /// Builds the Y-axes for the throughput area chart.
    /// </summary>
    /// <param name="theme">The current application theme for label colors.</param>
    /// <returns>An array containing the Y-axis configuration.</returns>
    public static Axis[] BuildYAxes(ElementTheme theme)
    {
        LiveChartsInitializer.EnsureInitialized();
        var labelPaint = ChartPalette.AxisTextPaint(theme);
        var separatorPaint = ChartPalette.SeparatorPaint(theme);

        return
        [
            new Axis
            {
                Name = "tokens/s",
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
    /// Builds the X-axes for the throughput area chart.
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
                Name = "\u8BF7\u6C42\u5E8F\u53F7",
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
