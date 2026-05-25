using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using SkiaSharp;

namespace RelayBench.WinUI.Charts;

/// <summary>
/// Builds a column chart showing the distribution of Time-To-First-Token (TTFT) values
/// bucketed into predefined millisecond ranges.
/// </summary>
public static class TtftDistributionChartBuilder
{
    private static readonly string[] BucketLabels =
    [
        "<500ms",
        "500ms-1s",
        "1s-2s",
        "2s-3s",
        "3s-5s",
        ">5s",
    ];

    /// <summary>
    /// Builds a column series representing the frequency distribution of TTFT values.
    /// Values are bucketed into ranges: &lt;500, 500-1000, 1000-2000, 2000-3000, 3000-5000, &gt;5000 ms.
    /// </summary>
    /// <param name="ttftValues">Raw TTFT values in milliseconds.</param>
    /// <param name="theme">The current application theme for color selection.</param>
    /// <returns>A tuple containing the series array and whether the data is empty.</returns>
    public static (ISeries[] Series, bool IsEmpty) Build(
        IReadOnlyList<double> ttftValues,
        ElementTheme theme)
    {
        LiveChartsInitializer.EnsureInitialized();
        var seriesColors = ChartPalette.ForTheme(theme);

        if (ttftValues.Count == 0)
        {
            return (
                [
                    new ColumnSeries<double>
                    {
                        Values = new double[6],
                        Name = "TTFT 分布",
                        Fill = new SolidColorPaint(seriesColors[0]),
                        Stroke = null,
                    },
                ],
                false);
        }

        var buckets = new double[6];

        foreach (var value in ttftValues)
        {
            var index = value switch
            {
                < 500 => 0,
                < 1000 => 1,
                < 2000 => 2,
                < 3000 => 3,
                < 5000 => 4,
                _ => 5,
            };
            buckets[index]++;
        }

        var colors = ChartPalette.ForTheme(theme);

        var series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = buckets,
                Name = "TTFT 分布",
                Fill = new SolidColorPaint(seriesColors[0]),
                Stroke = null,
            },
        };

        return (series, false);
    }

    /// <summary>
    /// Builds the X-axes with bucket labels for the TTFT distribution chart.
    /// </summary>
    /// <param name="theme">The current application theme for label colors.</param>
    /// <returns>An array containing the X-axis configuration with bucket labels.</returns>
    public static Axis[] BuildXAxes(ElementTheme theme)
    {
        LiveChartsInitializer.EnsureInitialized();
        var labelPaint = ChartPalette.LegendPaint(theme);

        return
        [
            new Axis
            {
                Labels = BucketLabels,
                LabelsRotation = 0,
                LabelsPaint = labelPaint,
            },
        ];
    }

    /// <summary>
    /// Builds the Y-axes for the TTFT distribution chart.
    /// </summary>
    /// <param name="theme">The current application theme for label colors.</param>
    /// <returns>An array containing the Y-axis configuration.</returns>
    public static Axis[] BuildYAxes(ElementTheme theme)
    {
        LiveChartsInitializer.EnsureInitialized();
        var labelPaint = ChartPalette.LegendPaint(theme);

        return
        [
            new Axis
            {
                Name = "次数",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];
    }
}
