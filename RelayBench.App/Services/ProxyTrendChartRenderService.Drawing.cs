using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

public sealed partial class ProxyTrendChartRenderService
{
    private static MetricSeries BuildMetricSeries(
        string title,
        IReadOnlyList<ProxyTrendEntry> records,
        Func<ProxyTrendEntry, double?> selector,
        double minValue,
        double maxValue,
        Func<double, string> formatter,
        Color accentColor)
    {
        var values = records.Select(selector).ToArray();
        return new MetricSeries(
            title,
            values,
            minValue,
            Math.Max(minValue + 1, maxValue),
            formatter,
            accentColor);
    }

    private static void DrawHeader(DrawingContext context, string targetLabel, IReadOnlyList<ProxyTrendEntry> records)
    {
        var headerRect = new Rect(14, 12, ChartWidth - 28, HeaderHeight - 14);
        context.DrawRoundedRectangle(CreateBrush(247, 249, 252), new Pen(CreateBrush(224, 231, 239), 1), headerRect, 14, 14);

        DrawText(context, "接口稳定性趋势图", new Point(28, 20), 21, FontWeights.SemiBold, CreateBrush(16, 24, 40));

        var subtitle =
            $"{ProxyTrendStore.NormalizeBaseUrl(targetLabel)}  |  样本 {records.Count}  |  最新 {records.Last().Timestamp:yyyy-MM-dd HH:mm:ss}";
        DrawText(context, subtitle, new Point(30, 46), 11.5, FontWeights.Normal, CreateBrush(102, 112, 133));
    }

    private static void DrawMetricPanel(DrawingContext context, Rect rect, MetricSeries series, bool higherIsBetter)
    {
        context.DrawRoundedRectangle(CreateBrush(255, 255, 255), new Pen(CreateBrush(226, 232, 240), 1), rect, 14, 14);

        DrawText(context, series.Title, new Point(rect.X + 14, rect.Y + 11), 15.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(
            context,
            BuildSeriesMeta(series, higherIsBetter),
            new Point(rect.Right - 238, rect.Y + 13),
            11.2,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));

        var plotRect = new Rect(rect.X + 14, rect.Y + 40, rect.Width - 28, rect.Height - 54);
        DrawGrid(context, plotRect, series);
        DrawSeries(context, plotRect, series);
    }

    private static string BuildSeriesMeta(MetricSeries series, bool higherIsBetter)
    {
        var latest = series.Values.LastOrDefault(value => value.HasValue);
        var trendHint = higherIsBetter ? "越高越好" : "越低越好";
        return latest.HasValue
            ? $"最新值：{series.Formatter(latest.Value)}  |  {trendHint}"
            : $"暂无有效数据  |  {trendHint}";
    }

    private static void DrawGrid(DrawingContext context, Rect plotRect, MetricSeries series)
    {
        var gridPen = new Pen(CreateBrush(203, 213, 225, 180), 1);
        for (var index = 0; index <= 4; index++)
        {
            var ratio = index / 4d;
            var y = plotRect.Top + (plotRect.Height * ratio);
            context.DrawLine(gridPen, new Point(plotRect.Left, y), new Point(plotRect.Right, y));

            var value = series.MaxValue - ((series.MaxValue - series.MinValue) * ratio);
            DrawText(
                context,
                series.Formatter(value),
                new Point(plotRect.Left + 6, y - 12),
                9.8,
                FontWeights.Normal,
                CreateBrush(102, 112, 133));
        }

        if (series.Values.Count <= 1)
        {
            return;
        }

        var xStep = plotRect.Width / (series.Values.Count - 1d);
        for (var index = 0; index < series.Values.Count; index++)
        {
            var x = plotRect.Left + (xStep * index);
            context.DrawLine(gridPen, new Point(x, plotRect.Top), new Point(x, plotRect.Bottom));
        }
    }

    private static void DrawSeries(DrawingContext context, Rect plotRect, MetricSeries series)
    {
        var points = BuildPoints(plotRect, series);
        if (points.Count == 0)
        {
            DrawText(
                context,
                "暂无有效采样点",
                new Point(plotRect.Left + 12, plotRect.Top + 14),
                11,
                FontWeights.Normal,
                CreateBrush(102, 112, 133));
            return;
        }

        var accentBrush = new SolidColorBrush(series.AccentColor);
        accentBrush.Freeze();
        var glowBrush = CreateBrush(series.AccentColor.R, series.AccentColor.G, series.AccentColor.B, 42);
        var linePen = new Pen(accentBrush, 2.6);

        if (points.Count >= 2)
        {
            context.DrawGeometry(glowBrush, null, BuildAreaGeometry(points, plotRect.Bottom));
            context.DrawGeometry(null, linePen, BuildLineGeometry(points));
        }

        foreach (var point in points)
        {
            context.DrawEllipse(accentBrush, new Pen(Brushes.White, 1.1), point, 3.4, 3.4);
        }

        var latestValue = series.Values.Last(value => value.HasValue)!.Value;
        var latestPoint = points[^1];
        var badgeRect = new Rect(latestPoint.X - 78, latestPoint.Y - 28, 92, 22);
        context.DrawRoundedRectangle(CreateBrush(255, 255, 255), new Pen(CreateBrush(series.AccentColor.R, series.AccentColor.G, series.AccentColor.B), 1), badgeRect, 8, 8);
        DrawText(context, series.Formatter(latestValue), new Point(badgeRect.X + 8, badgeRect.Y + 3), 10.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private static List<Point> BuildPoints(Rect plotRect, MetricSeries series)
    {
        List<Point> points = [];
        if (series.Values.Count == 0)
        {
            return points;
        }

        var divisor = Math.Max(1, series.Values.Count - 1);
        for (var index = 0; index < series.Values.Count; index++)
        {
            var value = series.Values[index];
            if (!value.HasValue)
            {
                continue;
            }

            var x = plotRect.Left + (plotRect.Width * index / divisor);
            var y = MapValueToY(value.Value, plotRect, series.MinValue, series.MaxValue);
            points.Add(new Point(x, y));
        }

        return points;
    }

    private static Geometry BuildLineGeometry(IReadOnlyList<Point> points)
    {
        StreamGeometry geometry = new();
        using var context = geometry.Open();
        context.BeginFigure(points[0], false, false);
        context.PolyLineTo(points.Skip(1).ToArray(), true, true);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry BuildAreaGeometry(IReadOnlyList<Point> points, double bottomY)
    {
        StreamGeometry geometry = new();
        using var context = geometry.Open();
        context.BeginFigure(new Point(points[0].X, bottomY), true, true);
        context.LineTo(points[0], true, true);
        context.PolyLineTo(points.Skip(1).ToArray(), true, true);
        context.LineTo(new Point(points[^1].X, bottomY), true, true);
        geometry.Freeze();
        return geometry;
    }

    private static double MapValueToY(double value, Rect plotRect, double minValue, double maxValue)
    {
        if (maxValue <= minValue)
        {
            return plotRect.Bottom;
        }

        var normalized = (value - minValue) / (maxValue - minValue);
        normalized = Math.Clamp(normalized, 0, 1);
        return plotRect.Bottom - (plotRect.Height * normalized);
    }

    private static void DrawFooter(DrawingContext context)
    {
        DrawText(
            context,
            "读图说明：稳定性越高越好；普通延迟越低越好；TTFT 越低越好。",
            new Point(20, ChartHeight - 22),
            10.5,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        Point origin,
        double fontSize,
        FontWeight fontWeight,
        Brush brush)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            brush,
            1.0);

        context.DrawText(formattedText, origin);
    }
}
