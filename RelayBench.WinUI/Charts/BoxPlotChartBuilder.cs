using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using SkiaSharp;

namespace RelayBench.WinUI.Charts;

/// <summary>
/// Builds a box-plot-style chart comparing latency distributions across multiple sites.
/// Since LiveCharts2 rc6.3 does not include a dedicated BoxSeries, this uses a
/// ColumnSeries showing the P50 (median) latency per site as a fallback visualization.
/// </summary>
public static class BoxPlotChartBuilder
{
    /// <summary>
    /// Builds a column series showing the median (P50) latency for each site.
    /// Requires at least 2 sites to produce a meaningful comparison.
    /// </summary>
    /// <param name="sites">A list of site names paired with their latency measurements.</param>
    /// <param name="theme">The current application theme for color selection.</param>
    /// <returns>A tuple containing the series array, X-axes with site labels, and whether the data is empty.</returns>
    public static (ISeries[] Series, Axis[] XAxes, bool IsEmpty) Build(
        IReadOnlyList<(string SiteName, IReadOnlyList<double> Latencies)> sites,
        ElementTheme theme)
    {
        if (sites.Count == 0)
        {
            return ([], [], true);
        }

        LiveChartsInitializer.EnsureInitialized();
        var colors = ChartPalette.ForTheme(theme);
        var labelPaint = ChartPalette.LegendPaint(theme);

        var medians = new double[sites.Count];
        var siteLabels = new string[sites.Count];

        for (var i = 0; i < sites.Count; i++)
        {
            siteLabels[i] = sites[i].SiteName;
            medians[i] = ComputeMedian(sites[i].Latencies);
        }

        var series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = medians,
                Name = "P50 延迟",
                Fill = new SolidColorPaint(colors[0]),
                Stroke = null,
            },
        };

        var xAxes = new Axis[]
        {
            new Axis
            {
                Labels = siteLabels,
                LabelsRotation = 0,
                LabelsPaint = labelPaint,
            },
        };

        return (series, xAxes, false);
    }

    /// <summary>
    /// Builds the Y-axes for the box plot chart.
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
                Name = "延迟 (ms)",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];
    }

    private static double ComputeMedian(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;

        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;

        if (sorted.Count % 2 == 0)
        {
            return (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        return sorted[mid];
    }
}
