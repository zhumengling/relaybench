using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetTest.App.Services;

public sealed class ProxyBatchDeepComparisonChartRenderService
{
    private const int DefaultChartWidth = 1560;
    private const int MinChartWidth = 1180;
    private const double HorizontalPadding = 20;
    private const double HeaderHeight = 134;
    private const double FooterHeight = 40;
    private const double TableHeaderHeight = 30;
    private const double RowHeight = 78;
    private const double RankColumnX = 28;
    private const double EntryColumnX = 90;
    private const double QuickColumnX = 366;
    private const double ProgressColumnX = 640;
    private const double ProgressColumnWidth = 112;
    private const double StageColumnX = 772;
    private const double StageColumnWidth = 246;
    private const double MatrixColumnX = 1038;
    private const double VerdictColumnX = 1438;
    private const double VerdictColumnWidth = 92;
    private const double DefaultContentWidth = DefaultChartWidth - (HorizontalPadding * 2);
    private int _chartWidth = DefaultChartWidth;

    public ProxyTrendChartRenderResult Render(IReadOnlyList<ProxyBatchDeepComparisonChartItem> items, int? preferredWidth = null)
    {
        if (items.Count == 0)
        {
            return new ProxyTrendChartRenderResult(
                false,
                "当前还没有候选站点深度测试结果，暂时无法生成总览图。",
                null,
                "当前还没有候选站点深度测试结果，暂时无法生成总览图。");
        }

        var ordered = items
            .OrderBy(item => item.Rank)
            .ToArray();
        _chartWidth = ResolveChartWidth(preferredWidth);
        var chartHeight = (int)Math.Ceiling(HeaderHeight + TableHeaderHeight + FooterHeight + (ordered.Length * RowHeight) + 18);
        List<ProxyChartHitRegion> hitRegions = [];

        DrawingVisual visual = new();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(CreateBrush(255, 255, 255), null, new Rect(0, 0, _chartWidth, chartHeight));
            DrawHeader(context, ordered);
            DrawTableHeader(context, HeaderHeight);

            for (var index = 0; index < ordered.Length; index++)
            {
                DrawRow(context, ordered[index], index, HeaderHeight + TableHeaderHeight + (index * RowHeight), hitRegions);
            }

            DrawFooter(context, chartHeight);
        }

        RenderTargetBitmap bitmap = new(_chartWidth, chartHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var completedCount = ordered.Count(item => item.IsCompleted);
        var running = ordered.FirstOrDefault(item => item.IsRunning);
        var best = ordered[0];
        var summary = running is null
            ? $"候选站点深度测试总览图已生成：共 {ordered.Length} 个候选项，已完成 {completedCount}/{ordered.Length}，当前 TOP 1 为 {best.Name}。"
            : $"候选站点深度测试进行中：已完成 {completedCount}/{ordered.Length}，当前执行 {running.Name}，排行榜保留 TOP 1 {best.Name}。";

        return new ProxyTrendChartRenderResult(true, summary, bitmap, null, hitRegions);
    }

    private void DrawHeader(DrawingContext context, IReadOnlyList<ProxyBatchDeepComparisonChartItem> items)
    {
        var headerRect = new Rect(ScaleX(14), 12, _chartWidth - (ScaleX(14) * 2), HeaderHeight - 18);
        context.DrawRoundedRectangle(CreateBrush(247, 249, 252), new Pen(CreateBrush(224, 231, 239), 1), headerRect, 16, 16);

        DrawText(context, "候选站点深度测试总览图", new Point(ScaleX(30), 22), 21, FontWeights.SemiBold, CreateBrush(23, 23, 23));
        DrawText(
            context,
            "参考 benchmark 结果页的高密度组织方式：左侧保留快速对比基线，中间显示实时进度与阶段，右侧矩阵聚合深度探针结果。",
            new Point(ScaleX(30), 49),
            11.5,
            FontWeights.Normal,
            CreateBrush(64, 64, 64));

        var completed = items.Count(item => item.IsCompleted);
        var running = items.FirstOrDefault(item => item.IsRunning);
        var healthyCount = items.Count(item => item.IsCompleted && IsHealthyVerdict(item.Verdict));
        var reviewCount = items.Count(item => item.IsCompleted && !IsHealthyVerdict(item.Verdict));
        var tiles = new[]
        {
            new MetricTile("候选总数", items.Count.ToString(CultureInfo.InvariantCulture)),
            new MetricTile("已完成", $"{completed}/{items.Count}"),
            new MetricTile("当前执行", running is null ? "无" : $"#{running.Rank}"),
            new MetricTile("当前 TOP 1", $"#{items[0].Rank}"),
            new MetricTile("可用 / 稳定", healthyCount.ToString(CultureInfo.InvariantCulture)),
            new MetricTile("待复核 / 异常", reviewCount.ToString(CultureInfo.InvariantCulture))
        };

        var tileWidth = ScaleWidth(170);
        var tileGap = ScaleWidth(10);
        var startX = ScaleX(28);
        var top = 72d;
        for (var index = 0; index < tiles.Length; index++)
        {
            var x = startX + (index * (tileWidth + tileGap));
            var tileRect = new Rect(x, top, tileWidth, 38);
            context.DrawRoundedRectangle(CreateBrush(255, 255, 255), new Pen(CreateBrush(220, 226, 234), 1), tileRect, 12, 12);
            DrawText(context, tiles[index].Label, new Point(x + 10, top + 7), 10.2, FontWeights.Normal, CreateBrush(102, 112, 133));
            DrawText(context, tiles[index].Value, new Point(x + 10, top + 20), 12.8, FontWeights.SemiBold, CreateBrush(23, 23, 23));
        }
    }

    private void DrawTableHeader(DrawingContext context, double top)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), TableHeaderHeight);
        context.DrawRoundedRectangle(CreateBrush(245, 247, 250), new Pen(CreateBrush(226, 232, 240), 1), rect, 8, 8);

        DrawText(context, "排行", new Point(ScaleX(RankColumnX), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(23, 23, 23));
        DrawText(context, "站点 / URL", new Point(ScaleX(EntryColumnX), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(23, 23, 23));
        DrawText(context, "快速基线", new Point(ScaleX(QuickColumnX), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(23, 23, 23));
        DrawText(context, "进度", new Point(ScaleX(ProgressColumnX), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(23, 23, 23));
        DrawText(context, "当前阶段 / 摘要", new Point(ScaleX(StageColumnX), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(23, 23, 23));
        DrawText(context, "深测矩阵", new Point(ScaleX(MatrixColumnX), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(23, 23, 23));
        DrawText(context, "结论", new Point(ScaleX(VerdictColumnX + 12), top + 7), 11.2, FontWeights.SemiBold, CreateBrush(23, 23, 23));
    }

    private void DrawRow(
        DrawingContext context,
        ProxyBatchDeepComparisonChartItem item,
        int index,
        double top,
        ICollection<ProxyChartHitRegion> hitRegions)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), RowHeight - 5);
        var background = index % 2 == 0
            ? CreateBrush(255, 255, 255)
            : CreateBrush(250, 251, 253);
        context.DrawRoundedRectangle(background, new Pen(CreateBrush(229, 231, 235), 1), rect, 10, 10);

        DrawRankBadge(context, item.Rank, top + 10);

        DrawText(context, TrimText(item.Name, 24), new Point(ScaleX(EntryColumnX), top + 10), 13, FontWeights.SemiBold, CreateBrush(23, 23, 23));
        DrawText(context, TrimMiddle(item.BaseUrl, 40), new Point(ScaleX(EntryColumnX), top + 30), 10.2, FontWeights.Normal, CreateBrush(82, 82, 82));
        DrawText(context, item.UpdatedAtText, new Point(ScaleX(EntryColumnX), top + 47), 9.8, FontWeights.Normal, CreateBrush(120, 120, 120));

        DrawWrappedText(
            context,
            $"普通 {FormatMilliseconds(item.QuickChatLatencyMs)} / TTFT {FormatMilliseconds(item.QuickTtftMs)}",
            new Point(ScaleX(QuickColumnX), top + 10),
            11.1,
            FontWeights.SemiBold,
            CreateBrush(23, 23, 23),
            ScaleWidth(246),
            18);
        DrawWrappedText(
            context,
            item.QuickCapabilityText,
            new Point(ScaleX(QuickColumnX), top + 31),
            10.1,
            FontWeights.Normal,
            CreateBrush(82, 82, 82),
            ScaleWidth(246),
            30);

        DrawProgressBlock(context, item, top + 11);
        DrawStageBlock(context, item, top + 10);
        DrawBadgeMatrix(context, item.Badges, top + 8, hitRegions);
        DrawVerdictBlock(context, item, top + 10);
    }

    private void DrawRankBadge(DrawingContext context, int rank, double top)
    {
        var fill = rank switch
        {
            1 => CreateBrush(255, 247, 216),
            2 => CreateBrush(242, 244, 247),
            3 => CreateBrush(255, 238, 224),
            _ => CreateBrush(245, 247, 250)
        };
        var border = rank switch
        {
            1 => CreateBrush(214, 175, 55),
            2 => CreateBrush(152, 162, 179),
            3 => CreateBrush(239, 156, 102),
            _ => CreateBrush(208, 213, 221)
        };

        var rect = new Rect(ScaleX(RankColumnX), top, ScaleWidth(42), 26);
        context.DrawRoundedRectangle(fill, new Pen(border, 1), rect, 13, 13);
        DrawText(context, $"#{rank}", new Point(rect.X + ScaleWidth(10), rect.Y + 5), 10.6, FontWeights.SemiBold, CreateBrush(23, 23, 23));
    }

    private void DrawProgressBlock(DrawingContext context, ProxyBatchDeepComparisonChartItem item, double top)
    {
        var label = $"{item.CompletedCount}/{Math.Max(item.TotalCount, 1)}";
        DrawText(context, label, new Point(ScaleX(ProgressColumnX), top), 12.3, FontWeights.SemiBold, CreateBrush(23, 23, 23));

        var barRect = new Rect(ScaleX(ProgressColumnX), top + 22, ScaleWidth(ProgressColumnWidth), 10);
        context.DrawRoundedRectangle(CreateBrush(234, 236, 240), null, barRect, 5, 5);
        var ratio = item.TotalCount <= 0 ? 0 : Math.Clamp((double)item.CompletedCount / item.TotalCount, 0d, 1d);
        if (ratio > 0)
        {
            var fill = item.IsCompleted
                ? CreateBrush(16, 185, 129)
                : CreateBrush(59, 130, 246);
            var fillRect = new Rect(barRect.X, barRect.Y, barRect.Width * ratio, barRect.Height);
            context.DrawRoundedRectangle(fill, null, fillRect, 5, 5);
        }

        DrawText(
            context,
            item.IsCompleted ? "已结束" : item.IsRunning ? "实时刷新" : "待执行",
            new Point(ScaleX(ProgressColumnX), top + 38),
            9.8,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));
    }

    private void DrawStageBlock(DrawingContext context, ProxyBatchDeepComparisonChartItem item, double top)
    {
        DrawWrappedText(
            context,
            item.StageText,
            new Point(ScaleX(StageColumnX), top),
            11.2,
            FontWeights.SemiBold,
            CreateBrush(23, 23, 23),
            ScaleWidth(StageColumnWidth),
            18);
        DrawWrappedText(
            context,
            item.IssueText,
            new Point(ScaleX(StageColumnX), top + 20),
            9.9,
            FontWeights.Normal,
            CreateBrush(82, 82, 82),
            ScaleWidth(StageColumnWidth),
            34);
    }

    private void DrawBadgeMatrix(
        DrawingContext context,
        IReadOnlyList<ProxyBatchDeepComparisonBadge> badges,
        double top,
        ICollection<ProxyChartHitRegion> hitRegions)
    {
        var rows = new[]
        {
            badges.Take(5).ToArray(),
            badges.Skip(5).Take(4).ToArray()
        };

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var y = top + (rowIndex * 23);
            for (var index = 0; index < rows[rowIndex].Length; index++)
            {
                var badge = rows[rowIndex][index];
                var x = ScaleX(MatrixColumnX + (index * 76));
                DrawMatrixBadge(context, badge, x, y, ScaleWidth(72), 18, hitRegions);
            }
        }
    }

    private void DrawMatrixBadge(
        DrawingContext context,
        ProxyBatchDeepComparisonBadge badge,
        double x,
        double y,
        double width,
        double height,
        ICollection<ProxyChartHitRegion> hitRegions)
    {
        ResolveBadgePalette(
            badge.State,
            out var background,
            out var border,
            out var foreground);

        var rect = new Rect(x, y, width, height);
        context.DrawRoundedRectangle(background, new Pen(border, 1), rect, 7, 7);
        DrawText(context, badge.Label, new Point(x + 6, y + 2), 9.4, FontWeights.SemiBold, foreground);
        DrawText(context, badge.Value, new Point(x + width - 24, y + 2), 9.2, FontWeights.SemiBold, foreground);
        hitRegions.Add(new ProxyChartHitRegion(
            rect,
            $"{badge.Title} ({badge.Label} {badge.Value})",
            BuildBadgeTooltipDescription(badge)));
    }

    private void DrawVerdictBlock(DrawingContext context, ProxyBatchDeepComparisonChartItem item, double top)
    {
        var (fill, border, text) = ResolveVerdictPalette(item);
        var rect = new Rect(ScaleX(VerdictColumnX), top, ScaleWidth(VerdictColumnWidth), 24);
        context.DrawRoundedRectangle(fill, new Pen(border, 1), rect, 12, 12);
        DrawWrappedText(
            context,
            item.Verdict,
            new Point(rect.X + ScaleWidth(8), rect.Y + 4),
            10.2,
            FontWeights.SemiBold,
            text,
            rect.Width - ScaleWidth(12),
            14);
        DrawWrappedText(
            context,
            item.IsRunning ? "当前执行中" : item.IsCompleted ? "本轮已结束" : "尚未开始",
            new Point(ScaleX(VerdictColumnX), top + 30),
            9.8,
            FontWeights.Normal,
            CreateBrush(102, 112, 133),
            ScaleWidth(VerdictColumnWidth),
            26);
    }

    private void DrawFooter(DrawingContext context, int chartHeight)
    {
        DrawWrappedText(
            context,
            "读图说明：B5=基础 5 项，Sys=System Prompt，Fn=Function Calling，Err=错误透传，Str=流式完整性，Ref=官方对照，MM=多模态，Cch=缓存命中，Iso=缓存隔离；蓝色表示进行中，绿色表示通过，橙色表示跳过/待复核，红色表示异常。",
            new Point(20, chartHeight - 26),
            10,
            FontWeights.Normal,
            CreateBrush(82, 82, 82),
            _chartWidth - 40,
            22);
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
            text,
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

    private static string BuildBadgeTooltipDescription(ProxyBatchDeepComparisonBadge badge)
        => $"{badge.Description}{Environment.NewLine}?????{ResolveBadgeValueExplanation(badge)}";

    private static string ResolveBadgeValueExplanation(ProxyBatchDeepComparisonBadge badge)
    {
        if (string.Equals(badge.Label, "B5", StringComparison.Ordinal))
        {
            var parts = badge.Value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var passCount) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalCount) &&
                totalCount > 0)
            {
                return $"?? 5 ???? {passCount} ??? {totalCount} ??";
            }

            return badge.State switch
            {
                ProxyBatchDeepComparisonBadgeState.Running => "?? 5 ??????????????",
                ProxyBatchDeepComparisonBadgeState.Pending => "?? 5 ??????",
                _ => $"?? 5 ?????? {badge.Value}?"
            };
        }

        return badge.Value switch
        {
            "OK" => "????????",
            "NO" => "????????????????????",
            "RV" => "????????????????",
            "SK" => "???????????????????????",
            "Off" => "???????????????????",
            "--" when badge.State == ProxyBatchDeepComparisonBadgeState.Running => "?????????????????",
            "--" => "??????????",
            _ => $"????? {badge.Value}?"
        };
    }

    private static bool IsHealthyVerdict(string verdict)
        => verdict.Contains("稳定", StringComparison.Ordinal) ||
           verdict.Contains("可用", StringComparison.Ordinal) ||
           verdict.Contains("通过", StringComparison.Ordinal);

    private static void ResolveBadgePalette(
        ProxyBatchDeepComparisonBadgeState state,
        out SolidColorBrush background,
        out SolidColorBrush border,
        out SolidColorBrush foreground)
    {
        switch (state)
        {
            case ProxyBatchDeepComparisonBadgeState.Pass:
                background = CreateBrush(236, 253, 245);
                border = CreateBrush(16, 185, 129);
                foreground = CreateBrush(4, 120, 87);
                return;
            case ProxyBatchDeepComparisonBadgeState.Running:
                background = CreateBrush(239, 246, 255);
                border = CreateBrush(59, 130, 246);
                foreground = CreateBrush(29, 78, 216);
                return;
            case ProxyBatchDeepComparisonBadgeState.Warn:
                background = CreateBrush(255, 247, 237);
                border = CreateBrush(245, 158, 11);
                foreground = CreateBrush(180, 83, 9);
                return;
            case ProxyBatchDeepComparisonBadgeState.Fail:
                background = CreateBrush(254, 242, 242);
                border = CreateBrush(239, 68, 68);
                foreground = CreateBrush(185, 28, 28);
                return;
            default:
                background = CreateBrush(248, 250, 252);
                border = CreateBrush(203, 213, 225);
                foreground = CreateBrush(100, 116, 139);
                return;
        }
    }

    private static (SolidColorBrush Fill, SolidColorBrush Border, SolidColorBrush Text) ResolveVerdictPalette(ProxyBatchDeepComparisonChartItem item)
    {
        if (item.IsRunning)
        {
            return (CreateBrush(239, 246, 255), CreateBrush(59, 130, 246), CreateBrush(29, 78, 216));
        }

        if (IsHealthyVerdict(item.Verdict))
        {
            return (CreateBrush(236, 253, 245), CreateBrush(16, 185, 129), CreateBrush(4, 120, 87));
        }

        if (item.IsCompleted)
        {
            return (CreateBrush(255, 247, 237), CreateBrush(245, 158, 11), CreateBrush(180, 83, 9));
        }

        return (CreateBrush(248, 250, 252), CreateBrush(203, 213, 225), CreateBrush(71, 85, 105));
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed record MetricTile(string Label, string Value);
}
