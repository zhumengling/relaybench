using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetTest.App.Services;

public sealed class ProxyBatchComparisonChartRenderService
{
    private const int DefaultChartWidth = 1240;
    private const int MinChartWidth = 980;
    private const double HorizontalPadding = 20;
    private const double HeaderHeight = 86;
    private const double FooterHeight = 36;
    private const double RowHeight = 58;
    private const double TableHeaderHeight = 28;
    private const double RankColumnX = 28;
    private const double EntryColumnX = 86;
    private const double ChatBarX = 376;
    private const double ChatBarWidth = 132;
    private const double TokensBarX = 524;
    private const double TokensBarWidth = 132;
    private const double TtftBarX = 672;
    private const double TtftBarWidth = 132;
    private const double StabilityBarX = 820;
    private const double StabilityBarWidth = 116;
    private const double VerdictColumnX = 952;
    private const double VerdictBadgeWidth = 138;
    private const double DefaultContentWidth = DefaultChartWidth - (HorizontalPadding * 2);
    private int _chartWidth = DefaultChartWidth;

    public ProxyTrendChartRenderResult Render(IReadOnlyList<ProxyBatchComparisonChartItem> items, int? preferredWidth = null)
    {
        if (items.Count == 0)
        {
            return new ProxyTrendChartRenderResult(
                false,
                "当前没有入口组结果，暂时无法生成 URL 稳定性对比图。",
                null,
                "当前没有入口组结果，暂时无法生成 URL 稳定性对比图。");
        }

        var ordered = items
            .OrderBy(item => item.Rank)
            .ToArray();
        _chartWidth = ResolveChartWidth(preferredWidth);
        var chartHeight = (int)Math.Ceiling(HeaderHeight + TableHeaderHeight + FooterHeight + (ordered.Length * RowHeight) + 18);
        var chatLatencyMax = PickChatLatencyMax(ordered);
        var ttftMax = PickTtftMax(ordered);
        var tokensPerSecondMax = PickTokensPerSecondMax(ordered);

        DrawingVisual visual = new();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(CreateBrush(255, 255, 255), null, new Rect(0, 0, _chartWidth, chartHeight));
            DrawHeader(context, ordered);
            DrawTableHeader(context, HeaderHeight);

            for (var index = 0; index < ordered.Length; index++)
            {
                DrawRow(context, ordered[index], index, HeaderHeight + TableHeaderHeight + (index * RowHeight), chatLatencyMax, tokensPerSecondMax, ttftMax);
            }

            DrawFooter(context, chartHeight);
        }

        RenderTargetBitmap bitmap = new(_chartWidth, chartHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var best = ordered[0];
        var summary =
            $"入口组对比图已生成：共 {ordered.Length} 个 URL，已累计 {ordered.Max(item => item.RunCount)} 轮整组，当前推荐 {best.Name}，" +
            $"平均普通延迟 {FormatMilliseconds(best.ChatLatencyMs)}，每秒生成 token 数 {FormatTokensPerSecond(best.TokensPerSecond)}，平均 TTFT {FormatMilliseconds(best.TtftMs)}，综合能力 {best.StabilityText}，稳定性 {BuildStabilityLabel(best)}。";

        return new ProxyTrendChartRenderResult(true, summary, bitmap, null);
    }

    private void DrawHeader(DrawingContext context, IReadOnlyList<ProxyBatchComparisonChartItem> items)
    {
        var headerRect = new Rect(ScaleX(14), 12, _chartWidth - (ScaleX(14) * 2), HeaderHeight - 14);
        context.DrawRoundedRectangle(CreateBrush(247, 249, 252), new Pen(CreateBrush(224, 231, 239), 1), headerRect, 14, 14);

        DrawText(context, "中转站入口组累计对比图", new Point(ScaleX(28), 20), 21, FontWeights.SemiBold, CreateBrush(16, 24, 40));

        var best = items[0];
        var roundCount = items.Max(item => item.RunCount);
        var subtitle =
            $"累计 {roundCount} 轮整组测试，共 {items.Count} 个 URL；TOP 1：{TrimText(best.Name, 26)}  |  平均普通 {FormatMilliseconds(best.ChatLatencyMs)}  |  tok/s {FormatTokensPerSecond(best.TokensPerSecond)}  |  平均 TTFT {FormatMilliseconds(best.TtftMs)}  |  {BuildStabilityLabel(best)}";
        DrawText(context, subtitle, new Point(ScaleX(30), 48), 11.5, FontWeights.Normal, CreateBrush(102, 112, 133));
    }

    private void DrawTableHeader(DrawingContext context, double top)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), TableHeaderHeight);
        context.DrawRoundedRectangle(CreateBrush(245, 247, 250), new Pen(CreateBrush(226, 232, 240), 1), rect, 8, 8);

        DrawText(context, "排名", new Point(ScaleX(RankColumnX), top + 6), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "入口 / URL", new Point(ScaleX(EntryColumnX), top + 6), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "平均普通延迟", new Point(ScaleX(ChatBarX + 4), top + 6), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "每秒生成 token 数", new Point(ScaleX(TokensBarX + 4), top + 6), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "平均 TTFT", new Point(ScaleX(TtftBarX + 4), top + 6), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "稳定性", new Point(ScaleX(StabilityBarX + 4), top + 6), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "结论", new Point(ScaleX(VerdictColumnX + 8), top + 6), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private void DrawRow(
        DrawingContext context,
        ProxyBatchComparisonChartItem item,
        int index,
        double top,
        double chatLatencyMax,
        double tokensPerSecondMax,
        double ttftMax)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), RowHeight - 4);
        var background = index % 2 == 0
            ? CreateBrush(255, 255, 255)
            : CreateBrush(250, 251, 253);
        context.DrawRoundedRectangle(background, new Pen(CreateBrush(229, 231, 235), 1), rect, 10, 10);

        DrawRankBadge(context, item.Rank, top + 10);

        DrawText(context, TrimText(item.Name, 24), new Point(ScaleX(EntryColumnX), top + 8), 13, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, TrimMiddle(item.BaseUrl, 34), new Point(ScaleX(EntryColumnX), top + 28), 10.4, FontWeights.Normal, CreateBrush(102, 112, 133));

        DrawBarCell(
            context,
            new Rect(ScaleX(ChatBarX), top + 12, ScaleWidth(ChatBarWidth), 16),
            item.ChatLatencyMs.HasValue ? Math.Max(0, chatLatencyMax - item.ChatLatencyMs.Value) : 0,
            chatLatencyMax,
            FormatMilliseconds(item.ChatLatencyMs),
            Color.FromRgb(37, 99, 235));
        DrawBarCell(
            context,
            new Rect(ScaleX(TokensBarX), top + 12, ScaleWidth(TokensBarWidth), 16),
            item.TokensPerSecond ?? 0,
            tokensPerSecondMax,
            FormatTokensPerSecond(item.TokensPerSecond),
            Color.FromRgb(124, 58, 237));
        DrawBarCell(
            context,
            new Rect(ScaleX(TtftBarX), top + 12, ScaleWidth(TtftBarWidth), 16),
            item.TtftMs.HasValue ? Math.Max(0, ttftMax - item.TtftMs.Value) : 0,
            ttftMax,
            FormatMilliseconds(item.TtftMs),
            Color.FromRgb(245, 158, 11));
        DrawBarCell(
            context,
            new Rect(ScaleX(StabilityBarX), top + 12, ScaleWidth(StabilityBarWidth), 16),
            item.StabilityRatio,
            100,
            item.StabilityText,
            Color.FromRgb(22, 163, 74));

        DrawStatusBadge(context, item, top + 11);
        DrawText(
            context,
            TrimText(item.SecondaryText, 44),
            new Point(ScaleX(ChatBarX), top + 32),
            10.2,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));
    }

    private void DrawRankBadge(DrawingContext context, int rank, double top)
    {
        var accent = rank switch
        {
            1 => CreateBrush(255, 247, 216),
            2 => CreateBrush(242, 244, 247),
            3 => CreateBrush(255, 238, 224),
            _ => CreateBrush(245, 247, 250)
        };

        var rect = new Rect(ScaleX(RankColumnX), top, ScaleWidth(40), 24);
        var border = rank switch
        {
            1 => CreateBrush(214, 175, 55),
            2 => CreateBrush(152, 162, 179),
            3 => CreateBrush(239, 156, 102),
            _ => CreateBrush(208, 213, 221)
        };
        context.DrawRoundedRectangle(accent, new Pen(border, 1), rect, 12, 12);
        DrawText(context, $"#{rank}", new Point(rect.X + Math.Max(7, ScaleWidth(9)), rect.Y + 4), 10.8, FontWeights.SemiBold, CreateBrush(16, 24, 40));
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
        var fillRect = new Rect(rect.X, rect.Y, rect.Width * ratio, rect.Height);
        context.DrawRoundedRectangle(CreateBrush(color.R, color.G, color.B), null, fillRect, 7, 7);
        DrawText(context, label, new Point(rect.X + 7, rect.Y + 0.5), 10.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private void DrawStatusBadge(DrawingContext context, ProxyBatchComparisonChartItem item, double top)
    {
        var statusText = BuildStabilityLabel(item);

        var badgeBrush = statusText switch
        {
            "稳定" => CreateBrush(236, 253, 245),
            "可用" => CreateBrush(255, 247, 237),
            _ => CreateBrush(254, 242, 242)
        };
        var borderBrush = statusText switch
        {
            "稳定" => CreateBrush(22, 163, 74),
            "可用" => CreateBrush(245, 158, 11),
            _ => CreateBrush(220, 38, 38)
        };
        var textBrush = statusText switch
        {
            "稳定" => CreateBrush(21, 128, 61),
            "可用" => CreateBrush(180, 83, 9),
            _ => CreateBrush(185, 28, 28)
        };

        var rect = new Rect(ScaleX(VerdictColumnX), top, ScaleWidth(VerdictBadgeWidth), 22);
        context.DrawRoundedRectangle(badgeBrush, new Pen(borderBrush, 1), rect, 12, 12);
        DrawText(context, statusText, new Point(rect.X + Math.Max(18, ScaleWidth(28)), rect.Y + 3), 10.4, FontWeights.SemiBold, textBrush);

        var verdict = string.IsNullOrWhiteSpace(item.Verdict) ? "未给出结论" : TrimText(item.Verdict, 10);
        DrawText(context, verdict, new Point(ScaleX(VerdictColumnX + 2), top + 26), 10.1, FontWeights.Normal, CreateBrush(102, 112, 133));
    }

    private void DrawFooter(DrawingContext context, int chartHeight)
    {
        DrawText(
            context,
            "读图说明：蓝条比较平均普通对话延迟，越长代表越快；紫条比较每秒生成 token 数，越长代表生成越快（快速测试默认按 3 次采样均值）；橙条比较平均 TTFT，越长代表首字响应越快；绿条显示入口组综合能力。",
            new Point(20, chartHeight - 22),
            10.4,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));
    }

    private int ResolveChartWidth(int? preferredWidth)
    {
        if (!preferredWidth.HasValue || preferredWidth.Value <= 0)
        {
            return DefaultChartWidth;
        }

        return Math.Clamp(preferredWidth.Value, MinChartWidth, 2600);
    }

    private double ContentScale
        => (_chartWidth - (HorizontalPadding * 2)) / DefaultContentWidth;

    private double ScaleX(double value)
        => HorizontalPadding + ((value - HorizontalPadding) * ContentScale);

    private double ScaleWidth(double value)
        => value * ContentScale;

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

    private static string TrimText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(1, maxLength - 1)] + "…";
    }

    private static string TrimMiddle(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        var leftLength = Math.Max(8, (maxLength - 1) / 2);
        var rightLength = Math.Max(6, maxLength - leftLength - 1);
        return $"{text[..leftLength]}…{text[^rightLength..]}";
    }

    private static string FormatMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:F0} ms" : "--";

    private static string FormatTokensPerSecond(double? value)
        => value.HasValue ? $"{value.Value:F1} tok/s" : "--";

    private static double PickTtftMax(IEnumerable<ProxyBatchComparisonChartItem> items)
    {
        var maxObserved = items
            .Select(item => item.TtftMs)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(1200)
            .Max();

        return Math.Max(600, Math.Ceiling(maxObserved * 1.15 / 100d) * 100d);
    }

    private static double PickTokensPerSecondMax(IEnumerable<ProxyBatchComparisonChartItem> items)
    {
        var maxObserved = items
            .Select(item => item.TokensPerSecond)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(12)
            .Max();

        return Math.Max(6, Math.Ceiling(maxObserved * 1.15));
    }

    private static double PickChatLatencyMax(IEnumerable<ProxyBatchComparisonChartItem> items)
    {
        var maxObserved = items
            .Select(item => item.ChatLatencyMs)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(1800)
            .Max();

        return Math.Max(800, Math.Ceiling(maxObserved * 1.15 / 100d) * 100d);
    }

    private static string BuildStabilityLabel(ProxyBatchComparisonChartItem item)
    {
        if (item.StabilityRatio >= 80 &&
            (!item.ChatLatencyMs.HasValue || item.ChatLatencyMs.Value <= 1800) &&
            (!item.TtftMs.HasValue || item.TtftMs.Value <= 1800))
        {
            return "稳定";
        }

        if (item.StabilityRatio >= 60)
        {
            return "可用";
        }

        return "待复核";
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
