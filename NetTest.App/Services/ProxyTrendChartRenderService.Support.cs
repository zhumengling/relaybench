using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetTest.App.Infrastructure;

namespace NetTest.App.Services;

public sealed partial class ProxyTrendChartRenderService
{
    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static double ResolveSuccessRate(ProxyTrendEntry entry)
    {
        if (entry.FullSuccessRate.HasValue)
        {
            return entry.FullSuccessRate.Value;
        }

        var score = 0d;
        score += entry.ModelsSuccess ? 100d / 3d : 0;
        score += entry.ChatSuccess ? 100d / 3d : 0;
        score += entry.StreamSuccess ? 100d / 3d : 0;
        return Math.Round(score, 1);
    }

    private static double PickTtftMax(IReadOnlyList<ProxyTrendEntry> records)
    {
        var maxObserved = records
            .Select(record => record.StreamFirstTokenLatencyMs)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(1200)
            .Max();

        return Math.Max(600, Math.Ceiling(maxObserved * 1.15 / 100d) * 100d);
    }

    private static double PickChatLatencyMax(IReadOnlyList<ProxyTrendEntry> records)
    {
        var maxObserved = records
            .Select(record => record.ChatLatencyMs)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(1800)
            .Max();

        return Math.Max(800, Math.Ceiling(maxObserved * 1.15 / 100d) * 100d);
    }

    private sealed record MetricSeries(
        string Title,
        IReadOnlyList<double?> Values,
        double MinValue,
        double MaxValue,
        Func<double, string> Formatter,
        Color AccentColor);
}
