using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetTest.App.Infrastructure;

namespace NetTest.App.Services;

public sealed class ProxyConcurrencyChartRenderService
{
    private const int DefaultChartWidth = 1480;
    private const int MinChartWidth = 1120;
    private const double HorizontalPadding = 20;
    private const double HeaderHeight = 122;
    private const double TableHeaderHeight = 30;
    private const double FooterHeight = 42;
    private const double RowHeight = 64;
    private const double ConcurrencyColumnX = 28;
    private const double SuccessRateColumnX = 112;
    private const double SuccessRateColumnWidth = 148;
    private const double FailureColumnX = 280;
    private const double P50ColumnX = 444;
    private const double P50ColumnWidth = 136;
    private const double TtftColumnX = 600;
    private const double TtftColumnWidth = 148;
    private const double TokenColumnX = 768;
    private const double TokenColumnWidth = 136;
    private const double VerdictColumnX = 924;
    private const double VerdictColumnWidth = 118;
    private const double SummaryColumnX = 1062;
    private const double DefaultContentWidth = DefaultChartWidth - (HorizontalPadding * 2);
    private int _chartWidth = DefaultChartWidth;

    public ProxyTrendChartRenderResult Render(
        string baseUrl,
        string? model,
        IReadOnlyList<ProxyConcurrencyChartItem> items,
        string summary,
        int? stableConcurrencyLimit,
        int? rateLimitStartConcurrency,
        int? highRiskConcurrency,
        string? error,
        int? preferredWidth = null)
    {
        if (items.Count == 0)
        {
            return new ProxyTrendChartRenderResult(
                false,
                "\u5F53\u524D\u8FD8\u6CA1\u6709\u5E76\u53D1\u538B\u6D4B\u5206\u6863\u7ED3\u679C\uFF0C\u6682\u65F6\u65E0\u6CD5\u751F\u6210\u56FE\u8868\u3002",
                null,
                "\u5F53\u524D\u8FD8\u6CA1\u6709\u5E76\u53D1\u538B\u6D4B\u5206\u6863\u7ED3\u679C\uFF0C\u6682\u65F6\u65E0\u6CD5\u751F\u6210\u56FE\u8868\u3002");
        }

        var ordered = items
            .OrderBy(item => item.Concurrency)
            .ToArray();
        _chartWidth = ResolveChartWidth(preferredWidth);

        var chartHeight = (int)Math.Ceiling(HeaderHeight + TableHeaderHeight + FooterHeight + (ordered.Length * RowHeight) + 18);
        var p50Max = PickLatencyMax(ordered.Select(static item => item.P50ChatLatencyMs));
        var p95TtftMax = PickLatencyMax(ordered.Select(static item => item.P95TtftMs));
        var tokenMax = PickTokenMax(ordered);
        List<ProxyChartHitRegion> hitRegions = [];

        DrawingVisual visual = new();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(CreateBrush(255, 255, 255), null, new Rect(0, 0, _chartWidth, chartHeight));
            DrawHeader(context, baseUrl, model, ordered, stableConcurrencyLimit, rateLimitStartConcurrency, highRiskConcurrency, error);
            DrawTableHeader(context, HeaderHeight);

            for (var index = 0; index < ordered.Length; index++)
            {
                DrawRow(
                    context,
                    ordered[index],
                    index,
                    HeaderHeight + TableHeaderHeight + (index * RowHeight),
                    p50Max,
                    p95TtftMax,
                    tokenMax,
                    hitRegions);
            }

            DrawFooter(context, chartHeight, summary);
        }

        RenderTargetBitmap bitmap = new(_chartWidth, chartHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        return new ProxyTrendChartRenderResult(
            true,
            string.IsNullOrWhiteSpace(summary)
                ? "\u5E76\u53D1\u538B\u6D4B\u56FE\u5DF2\u751F\u6210\u3002"
                : summary,
            bitmap,
            null,
            hitRegions);
    }

    private void DrawHeader(
        DrawingContext context,
        string baseUrl,
        string? model,
        IReadOnlyList<ProxyConcurrencyChartItem> items,
        int? stableConcurrencyLimit,
        int? rateLimitStartConcurrency,
        int? highRiskConcurrency,
        string? error)
    {
        var headerRect = new Rect(ScaleX(14), 12, _chartWidth - (ScaleX(14) * 2), HeaderHeight - 16);
        context.DrawRoundedRectangle(CreateBrush(247, 249, 252), new Pen(CreateBrush(224, 231, 239), 1), headerRect, 15, 15);

        DrawText(context, "\u63A5\u53E3\u5E76\u53D1\u538B\u6D4B\u56FE\u8868", new Point(ScaleX(30), 20), 21, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(
            context,
            $"{TrimMiddle(ProxyTrendStore.NormalizeBaseUrl(baseUrl), 48)}  |  \u6A21\u578B {TrimText(string.IsNullOrWhiteSpace(model) ? "--" : model!, 28)}  |  \u5206\u6863 {items.Count}",
            new Point(ScaleX(30), 47),
            11.4,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));

        var tileTop = 70d;
        var tiles =
            new[]
            {
                new MetricTile("\u7A33\u5B9A\u5E76\u53D1\u4E0A\u9650", FormatConcurrencyValue(stableConcurrencyLimit)),
                new MetricTile("\u9650\u6D41\u8D77\u70B9", FormatConcurrencyValue(rateLimitStartConcurrency)),
                new MetricTile("\u9AD8\u98CE\u9669\u6863", FormatConcurrencyValue(highRiskConcurrency)),
                new MetricTile("429 \u6863\u4F4D\u6570", items.Count(static item => item.RateLimitedCount > 0).ToString(CultureInfo.InvariantCulture)),
                new MetricTile("\u8D85\u65F6\u6863\u4F4D\u6570", items.Count(static item => item.TimeoutCount > 0).ToString(CultureInfo.InvariantCulture))
            };

        var tileWidth = ScaleWidth(166);
        var tileGap = ScaleWidth(10);
        var tileStartX = ScaleX(28);
        for (var index = 0; index < tiles.Length; index++)
        {
            var x = tileStartX + (index * (tileWidth + tileGap));
            var tileRect = new Rect(x, tileTop, tileWidth, 38);
            context.DrawRoundedRectangle(CreateBrush(255, 255, 255), new Pen(CreateBrush(220, 226, 234), 1), tileRect, 12, 12);
            DrawText(context, tiles[index].Label, new Point(x + 10, tileTop + 7), 10.2, FontWeights.Normal, CreateBrush(102, 112, 133));
            DrawText(context, tiles[index].Value, new Point(x + 10, tileTop + 20), 12.7, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            DrawText(
                context,
                "\u672C\u8F6E\u542B\u9519\u8BEF\u4FE1\u606F\uFF1A" + TrimText(error, 52),
                new Point(ScaleX(912), tileTop + 18),
                10.6,
                FontWeights.SemiBold,
                CreateBrush(185, 28, 28));
        }
    }

    private void DrawTableHeader(DrawingContext context, double top)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), TableHeaderHeight);
        context.DrawRoundedRectangle(CreateBrush(245, 247, 250), new Pen(CreateBrush(226, 232, 240), 1), rect, 8, 8);

        DrawText(context, "\u6863\u4F4D", new Point(ScaleX(ConcurrencyColumnX), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "\u6210\u529F\u7387", new Point(ScaleX(SuccessRateColumnX + 4), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "\u5931\u8D25\u8BA1\u6570", new Point(ScaleX(FailureColumnX), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "p50 \u666E\u901A", new Point(ScaleX(P50ColumnX + 4), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "p95 TTFT", new Point(ScaleX(TtftColumnX + 4), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "tok/s", new Point(ScaleX(TokenColumnX + 4), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "\u5224\u5B9A", new Point(ScaleX(VerdictColumnX + 8), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "\u6458\u8981", new Point(ScaleX(SummaryColumnX), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private void DrawRow(
        DrawingContext context,
        ProxyConcurrencyChartItem item,
        int index,
        double top,
        double p50Max,
        double p95TtftMax,
        double tokenMax,
        ICollection<ProxyChartHitRegion> hitRegions)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), RowHeight - 4);
        var background = index % 2 == 0
            ? CreateBrush(255, 255, 255)
            : CreateBrush(250, 251, 253);
        context.DrawRoundedRectangle(background, new Pen(CreateBrush(229, 231, 235), 1), rect, 10, 10);

        DrawText(context, $"x{item.Concurrency}", new Point(ScaleX(ConcurrencyColumnX), top + 10), 13, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(
            context,
            $"{item.SuccessCount}/{Math.Max(item.TotalRequests, 1)}",
            new Point(ScaleX(ConcurrencyColumnX), top + 31),
            10.2,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));

        DrawBarCell(
            context,
            new Rect(ScaleX(SuccessRateColumnX), top + 14, ScaleWidth(SuccessRateColumnWidth), 16),
            item.SuccessRate,
            100d,
            $"{item.SuccessRate:F1}%",
            Color.FromRgb(22, 163, 74));
        DrawText(
            context,
            "\u6210\u529F\u7387",
            new Point(ScaleX(SuccessRateColumnX), top + 36),
            9.8,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));

        DrawText(
            context,
            $"429 {item.RateLimitedCount}  |  5xx {item.ServerErrorCount}  |  TO {item.TimeoutCount}",
            new Point(ScaleX(FailureColumnX), top + 17),
            10.8,
            FontWeights.SemiBold,
            ResolveFailureTextBrush(item));
        DrawText(
            context,
            "\u89E6\u53D1\u9650\u6D41 / \u670D\u52A1\u9519\u8BEF / \u8D85\u65F6",
            new Point(ScaleX(FailureColumnX), top + 37),
            9.6,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));

        DrawLatencyBarCell(
            context,
            new Rect(ScaleX(P50ColumnX), top + 14, ScaleWidth(P50ColumnWidth), 16),
            item.P50ChatLatencyMs,
            p50Max,
            Color.FromRgb(37, 99, 235));
        DrawLatencyBarCell(
            context,
            new Rect(ScaleX(TtftColumnX), top + 14, ScaleWidth(TtftColumnWidth), 16),
            item.P95TtftMs,
            p95TtftMax,
            Color.FromRgb(245, 158, 11));
        DrawTokenBarCell(
            context,
            new Rect(ScaleX(TokenColumnX), top + 14, ScaleWidth(TokenColumnWidth), 16),
            item.AverageTokensPerSecond,
            tokenMax,
            Color.FromRgb(124, 58, 237));

        DrawVerdictBadge(context, item, top + 12);
        DrawWrappedText(
            context,
            item.Summary,
            new Point(ScaleX(SummaryColumnX), top + 10),
            10.1,
            FontWeights.Normal,
            CreateBrush(71, 84, 103),
            ScaleWidth(376),
            34);

        hitRegions.Add(new ProxyChartHitRegion(rect, BuildHitRegionTitle(item), BuildHitRegionDescription(item)));
    }

    private void DrawVerdictBadge(DrawingContext context, ProxyConcurrencyChartItem item, double top)
    {
        ResolveVerdictPalette(item, out var fill, out var border, out var text);
        var rect = new Rect(ScaleX(VerdictColumnX), top, ScaleWidth(VerdictColumnWidth), 24);
        context.DrawRoundedRectangle(fill, new Pen(border, 1), rect, 12, 12);
        DrawWrappedText(
            context,
            item.Verdict,
            new Point(rect.X + ScaleWidth(9), rect.Y + 4),
            10.1,
            FontWeights.SemiBold,
            text,
            rect.Width - ScaleWidth(14),
            16);
    }

    private void DrawLatencyBarCell(
        DrawingContext context,
        Rect rect,
        double? value,
        double maxValue,
        Color color)
    {
        var displayText = FormatMilliseconds(value);
        context.DrawRoundedRectangle(CreateBrush(234, 236, 240), null, rect, 7, 7);
        if (value.HasValue && maxValue > 0)
        {
            var normalized = Math.Max(0, maxValue - value.Value);
            var fillRect = new Rect(rect.X, rect.Y, rect.Width * Math.Clamp(normalized / maxValue, 0, 1), rect.Height);
            context.DrawRoundedRectangle(CreateBrush(color.R, color.G, color.B), null, fillRect, 7, 7);
        }

        DrawText(context, displayText, new Point(rect.X + 7, rect.Y + 0.5), 10.1, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private void DrawTokenBarCell(
        DrawingContext context,
        Rect rect,
        double? value,
        double maxValue,
        Color color)
    {
        var displayText = FormatTokensPerSecond(value);
        context.DrawRoundedRectangle(CreateBrush(234, 236, 240), null, rect, 7, 7);
        if (value.HasValue && maxValue > 0)
        {
            var fillRect = new Rect(rect.X, rect.Y, rect.Width * Math.Clamp(value.Value / maxValue, 0, 1), rect.Height);
            context.DrawRoundedRectangle(CreateBrush(color.R, color.G, color.B), null, fillRect, 7, 7);
        }

        DrawText(context, displayText, new Point(rect.X + 7, rect.Y + 0.5), 10.1, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private static void DrawBarCell(
        DrawingContext context,
        Rect rect,
        double value,
        double maxValue,
        string label,
        Color color)
    {
        context.DrawRoundedRectangle(CreateBrush(234, 236, 240), null, rect, 7, 7);
        var ratio = maxValue <= 0 ? 0 : Math.Clamp(value / maxValue, 0, 1);
        if (ratio > 0)
        {
            var fillRect = new Rect(rect.X, rect.Y, rect.Width * ratio, rect.Height);
            context.DrawRoundedRectangle(CreateBrush(color.R, color.G, color.B), null, fillRect, 7, 7);
        }

        DrawText(context, label, new Point(rect.X + 7, rect.Y + 0.5), 10.1, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private void DrawFooter(DrawingContext context, int chartHeight, string summary)
    {
        DrawWrappedText(
            context,
            string.IsNullOrWhiteSpace(summary)
                ? "\u8BFB\u56FE\u8BF4\u660E\uFF1A\u7EFF\u6761\u5BF9\u5E94\u6210\u529F\u7387\uFF0C\u84DD\u6761\u5BF9\u5E94 p50 \u666E\u901A\u5EF6\u8FDF\uFF0C\u6A59\u6761\u5BF9\u5E94 p95 TTFT\uFF0C\u7D2B\u6761\u5BF9\u5E94 tok/s\u3002"
                : summary,
            new Point(20, chartHeight - 30),
            10,
            FontWeights.Normal,
            CreateBrush(102, 112, 133),
            _chartWidth - 40,
            24);
    }

    private static Brush ResolveFailureTextBrush(ProxyConcurrencyChartItem item)
    {
        if (item.TimeoutCount > 0 || item.ServerErrorCount > 0)
        {
            return CreateBrush(185, 28, 28);
        }

        if (item.RateLimitedCount > 0)
        {
            return CreateBrush(180, 83, 9);
        }

        return CreateBrush(16, 24, 40);
    }

    private static string BuildHitRegionTitle(ProxyConcurrencyChartItem item)
    {
        var marker = item.IsStableLimit
            ? "\uFF08\u7A33\u5B9A\u4E0A\u9650\uFF09"
            : item.IsRateLimitStart
                ? "\uFF08\u9650\u6D41\u8D77\u70B9\uFF09"
                : item.IsHighRisk
                    ? "\uFF08\u9AD8\u98CE\u9669\u6863\uFF09"
                    : string.Empty;
        return $"\u5E76\u53D1 x{item.Concurrency} {marker}".TrimEnd();
    }

    private static string BuildHitRegionDescription(ProxyConcurrencyChartItem item)
        => string.Join(
            Environment.NewLine,
            $"\u7ED3\u679C\uFF1A{item.Verdict}",
            $"\u6210\u529F\uFF1A{item.SuccessCount}/{Math.Max(item.TotalRequests, 1)} \uFF08{item.SuccessRate:F1}%\uFF09",
            $"429\uFF1A{item.RateLimitedCount}  |  5xx\uFF1A{item.ServerErrorCount}  |  \u8D85\u65F6\uFF1A{item.TimeoutCount}",
            $"p50 \u666E\u901A\u5EF6\u8FDF\uFF1A{FormatMilliseconds(item.P50ChatLatencyMs)}",
            $"p95 TTFT\uFF1A{FormatMilliseconds(item.P95TtftMs)}",
            $"\u5E73\u5747 tok/s\uFF1A{FormatTokensPerSecond(item.AverageTokensPerSecond)}",
            $"\u6458\u8981\uFF1A{item.Summary}");

    private static void ResolveVerdictPalette(
        ProxyConcurrencyChartItem item,
        out SolidColorBrush fill,
        out SolidColorBrush border,
        out SolidColorBrush text)
    {
        if (item.IsHighRisk)
        {
            fill = CreateBrush(254, 242, 242);
            border = CreateBrush(239, 68, 68);
            text = CreateBrush(185, 28, 28);
            return;
        }

        if (item.IsRateLimitStart || item.RateLimitedCount > 0)
        {
            fill = CreateBrush(255, 247, 237);
            border = CreateBrush(245, 158, 11);
            text = CreateBrush(180, 83, 9);
            return;
        }

        if (item.IsStableLimit || item.SuccessRate >= 95d)
        {
            fill = CreateBrush(236, 253, 245);
            border = CreateBrush(16, 185, 129);
            text = CreateBrush(4, 120, 87);
            return;
        }

        fill = CreateBrush(239, 246, 255);
        border = CreateBrush(59, 130, 246);
        text = CreateBrush(29, 78, 216);
    }

    private int ResolveChartWidth(int? preferredWidth)
    {
        if (!preferredWidth.HasValue || preferredWidth.Value <= 0)
        {
            return DefaultChartWidth;
        }

        return Math.Clamp(preferredWidth.Value, MinChartWidth, 3200);
    }

    private double ContentScale
        => (_chartWidth - (HorizontalPadding * 2)) / DefaultContentWidth;

    private double ScaleX(double value)
        => HorizontalPadding + ((value - HorizontalPadding) * ContentScale);

    private double ScaleWidth(double value)
        => value * ContentScale;

    private static double PickLatencyMax(IEnumerable<double?> values)
    {
        var maxObserved = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty(1200d)
            .Max();

        return Math.Max(600d, Math.Ceiling(maxObserved * 1.15d / 100d) * 100d);
    }

    private static double PickTokenMax(IEnumerable<ProxyConcurrencyChartItem> items)
    {
        var maxObserved = items
            .Select(static item => item.AverageTokensPerSecond)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty(8d)
            .Max();

        return Math.Max(4d, Math.Ceiling(maxObserved * 1.15d));
    }

    private static string FormatConcurrencyValue(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "--";

    private static string FormatMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:F0} ms" : "--";

    private static string FormatTokensPerSecond(double? value)
        => value.HasValue ? $"{value.Value:F1} tok/s" : "--";

    private static string TrimText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(1, maxLength - 1)] + "\u2026";
    }

    private static string TrimMiddle(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        var leftLength = Math.Max(8, (maxLength - 1) / 2);
        var rightLength = Math.Max(6, maxLength - leftLength - 1);
        return $"{text[..leftLength]}\u2026{text[^rightLength..]}";
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

    private static void DrawWrappedText(
        DrawingContext context,
        string text,
        Point origin,
        double fontSize,
        FontWeight fontWeight,
        Brush brush,
        double maxWidth,
        double maxHeight)
    {
        var formattedText = new FormattedText(
            string.IsNullOrWhiteSpace(text) ? " " : text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            brush,
            1.0)
        {
            MaxTextWidth = Math.Max(12, maxWidth),
            MaxTextHeight = Math.Max(12, maxHeight),
            Trimming = TextTrimming.CharacterEllipsis
        };

        context.DrawText(formattedText, origin);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed record MetricTile(string Label, string Value);
}
