using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using SkiaSharp;

namespace RelayBench.WinUI.Charts;

/// <summary>
/// Builds a grouped bar (column) chart comparing throughput across multiple sites.
/// </summary>
public static class GroupedBarChartBuilder
{
    /// <summary>
    /// Builds a column series showing throughput per site with site names on the X-axis.
    /// Requires at least 2 sites to produce a meaningful comparison.
    /// </summary>
    /// <param name="sites">A list of site names paired with their throughput values.</param>
    /// <param name="theme">The current application theme for color selection.</param>
    /// <returns>A tuple containing the series array, X-axes with site labels, and whether the data is empty.</returns>
    public static (ISeries[] Series, Axis[] XAxes, bool IsEmpty) Build(
        IReadOnlyList<(string SiteName, double Throughput)> sites,
        ElementTheme theme)
    {
        if (sites.Count == 0)
        {
            return ([], [], true);
        }

        LiveChartsInitializer.EnsureInitialized();
        var colors = ChartPalette.ForTheme(theme);
        var labelPaint = ChartPalette.LegendPaint(theme);

        var throughputs = new double[sites.Count];
        var siteLabels = new string[sites.Count];

        for (var i = 0; i < sites.Count; i++)
        {
            siteLabels[i] = sites[i].SiteName;
            throughputs[i] = sites[i].Throughput;
        }

        var series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = throughputs,
                Name = "Throughput",
                Fill = new SolidColorPaint(colors[3]),
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
    /// Builds the Y-axes for the grouped bar chart.
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
                Name = "tokens/s",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];
    }
}
