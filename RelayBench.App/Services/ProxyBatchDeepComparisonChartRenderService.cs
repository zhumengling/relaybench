using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RelayBench.App.Services;

public sealed class ProxyBatchDeepComparisonChartRenderService
{
    private const int DefaultChartWidth = 1560;
    private const int MinChartWidth = 1180;
    private const double HorizontalPadding = 20;
    private const double HeaderHeight = 92;
    private const double FooterHeight = 30;
    private const double TableHeaderHeight = 28;
    private const double RowHeight = 70;
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
        List<ProxyChartActivityRegion> activityRegions = [];

        DrawingVisual visual = new();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(CreateBrush(255, 255, 255), null, new Rect(0, 0, _chartWidth, chartHeight));
            DrawHeader(context, ordered);
            DrawTableHeader(context, HeaderHeight);

            for (var index = 0; index < ordered.Length; index++)
            {
                DrawRow(context, ordered[index], index, HeaderHeight + TableHeaderHeight + (index * RowHeight), hitRegions, activityRegions);
            }

            DrawFooter(context, chartHeight);
        }

        RenderTargetBitmap bitmap = new(_chartWidth, chartHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var completedCount = ordered.Count(item => item.IsCompleted);
        var runningCount = ordered.Count(item => item.IsRunning);
        var best = ordered[0];
        var summary = runningCount == 0
            ? $"候选站点深度测试总览图已生成：共 {ordered.Length} 个候选项，已完成 {completedCount}/{ordered.Length}，当前 TOP 1 为 {best.Name}。"
            : $"候选站点深度测试进行中：已完成 {completedCount}/{ordered.Length}，当前并发执行 {runningCount} 个候选项，排行榜保留 TOP 1 {best.Name}。";

        return new ProxyTrendChartRenderResult(true, summary, bitmap, null, hitRegions, activityRegions);
    }

    private void DrawHeader(DrawingContext context, IReadOnlyList<ProxyBatchDeepComparisonChartItem> items)
    {
        var completed = items.Count(item => item.IsCompleted);
        var runningCount = items.Count(item => item.IsRunning);
        var healthyCount = items.Count(item => item.IsCompleted && IsHealthyVerdict(item.Verdict));
        var reviewCount = items.Count(item => item.IsCompleted && !IsHealthyVerdict(item.Verdict));
        var headerRect = new Rect(ScaleX(14), 10, _chartWidth - (ScaleX(14) * 2), HeaderHeight - 12);
        context.DrawRoundedRectangle(CreateBrush(247, 249, 252), new Pen(CreateBrush(224, 231, 239), 1), headerRect, 14, 14);

        DrawText(context, "候选站点深度测试总览图", new Point(ScaleX(28), 18), 21, FontWeights.SemiBold, CreateBrush(16, 24, 40));

        var tiles = new[]
        {
            new MetricTile("已完成", $"{completed}/{items.Count}"),
            new MetricTile("并发执行", runningCount == 0 ? "无" : runningCount.ToString(CultureInfo.InvariantCulture)),
            new MetricTile("可用 / 稳定", healthyCount.ToString(CultureInfo.InvariantCulture)),
            new MetricTile("复核 / 异常", reviewCount.ToString(CultureInfo.InvariantCulture))
        };

        var tileWidth = ScaleWidth(118);
        var tileGap = ScaleWidth(8);
        var top = 23d;
        var startX = Math.Max(ScaleX(620), _chartWidth - ScaleWidth(28 + (tiles.Length * 118) + ((tiles.Length - 1) * 8)));
        var subtitleWidth = Math.Max(260, startX - ScaleX(30) - ScaleWidth(22));
        var subtitle =
            $"共 {items.Count} 个候选项，TOP 1 #{items[0].Rank}；快速基线、实时进度和深度探针矩阵合并展示。";
        DrawWrappedText(
            context,
            subtitle,
            new Point(ScaleX(30), 46),
            11.2,
            FontWeights.Normal,
            CreateBrush(102, 112, 133),
            subtitleWidth,
            18);

        for (var index = 0; index < tiles.Length; index++)
        {
            var x = startX + (index * (tileWidth + tileGap));
            var tileRect = new Rect(x, top, tileWidth, 40);
            context.DrawRoundedRectangle(CreateBrush(255, 255, 255), new Pen(CreateBrush(220, 226, 234), 1), tileRect, 12, 12);
            DrawText(context, tiles[index].Label, new Point(x + ScaleWidth(10), top + 7), 10, FontWeights.Normal, CreateBrush(102, 112, 133));
            DrawText(context, tiles[index].Value, new Point(x + ScaleWidth(10), top + 21), 13.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        }
    }

    private void DrawTableHeader(DrawingContext context, double top)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), TableHeaderHeight);
        context.DrawRoundedRectangle(CreateBrush(245, 247, 250), new Pen(CreateBrush(226, 232, 240), 1), rect, 8, 8);

        DrawText(context, "排行", new Point(ScaleX(RankColumnX), top + 6), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "站点 / URL", new Point(ScaleX(EntryColumnX), top + 6), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "快速基线", new Point(ScaleX(QuickColumnX), top + 6), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "进度", new Point(ScaleX(ProgressColumnX), top + 6), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "当前阶段 / 摘要", new Point(ScaleX(StageColumnX), top + 6), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "深测矩阵", new Point(ScaleX(MatrixColumnX), top + 6), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "结论", new Point(ScaleX(VerdictColumnX + 12), top + 6), 11.2, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private void DrawRow(
        DrawingContext context,
        ProxyBatchDeepComparisonChartItem item,
        int index,
        double top,
        ICollection<ProxyChartHitRegion> hitRegions,
        ICollection<ProxyChartActivityRegion> activityRegions)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), RowHeight - 5);
        var background = item.IsRunning
            ? CreateBrush(248, 251, 255)
            : index % 2 == 0
            ? CreateBrush(255, 255, 255)
            : CreateBrush(250, 251, 253);
        var border = item.IsRunning
            ? CreateBrush(147, 197, 253)
            : CreateBrush(229, 231, 235);
        context.DrawRoundedRectangle(background, new Pen(border, 1), rect, 11, 11);
        if (item.IsRunning)
        {
            context.DrawRoundedRectangle(CreateBrush(37, 99, 235), null, new Rect(rect.X, rect.Y + 9, 3, rect.Height - 18), 2, 2);
        }

        DrawRankBadge(context, item.Rank, top + 12);

        DrawText(context, TrimText(item.Name, 24), new Point(ScaleX(EntryColumnX), top + 8), 12.8, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, TrimMiddle(item.BaseUrl, 40), new Point(ScaleX(EntryColumnX), top + 27), 10.2, FontWeights.Normal, CreateBrush(102, 112, 133));
        DrawText(context, item.UpdatedAtText, new Point(ScaleX(EntryColumnX), top + 44), 9.6, FontWeights.Normal, CreateBrush(148, 163, 184));

        DrawWrappedText(
            context,
            $"普通 {FormatMilliseconds(item.QuickChatLatencyMs)} / TTFT {FormatMilliseconds(item.QuickTtftMs)}",
            new Point(ScaleX(QuickColumnX), top + 9),
            11.1,
            FontWeights.SemiBold,
            CreateBrush(16, 24, 40),
            ScaleWidth(246),
            18);
        DrawWrappedText(
            context,
            item.QuickCapabilityText,
            new Point(ScaleX(QuickColumnX), top + 29),
            10.1,
            FontWeights.Normal,
            CreateBrush(102, 112, 133),
            ScaleWidth(246),
            28);

        DrawProgressBlock(context, item, top + 10);
        DrawStageBlock(context, item, top + 8);
        DrawBadgeMatrix(context, item.Badges, top + 8, hitRegions);
        DrawVerdictBlock(context, item, top + 11);
        DrawRowActivityTrack(context, rect, item, activityRegions);
    }

    private void DrawRowActivityTrack(
        DrawingContext context,
        Rect rowRect,
        ProxyBatchDeepComparisonChartItem item,
        ICollection<ProxyChartActivityRegion> activityRegions)
    {
        var lineLeft = ScaleX(EntryColumnX) + 8;
        var lineRight = ScaleX(StageColumnX + StageColumnWidth) - 8;
        var lineWidth = Math.Max(120, lineRight - lineLeft);
        var lineTop = rowRect.Bottom - 5;
        var trackRect = new Rect(lineLeft, lineTop, lineWidth, 2.5);
        context.DrawRoundedRectangle(CreateBrush(219, 234, 254, 150), null, trackRect, 2, 2);

        if (!item.IsRunning)
        {
            return;
        }

        activityRegions.Add(new ProxyChartActivityRegion(new Rect(lineLeft, lineTop - 0.5, lineWidth, 3.5)));
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
        DrawText(context, label, new Point(ScaleX(ProgressColumnX), top), 12.3, FontWeights.SemiBold, CreateBrush(16, 24, 40));

        var barRect = new Rect(ScaleX(ProgressColumnX), top + 22, ScaleWidth(ProgressColumnWidth), 7);
        context.DrawRoundedRectangle(CreateBrush(226, 234, 246), null, barRect, 4, 4);
        var ratio = item.TotalCount <= 0 ? 0 : Math.Clamp((double)item.CompletedCount / item.TotalCount, 0d, 1d);
        if (ratio > 0)
        {
            var fillRect = new Rect(barRect.X, barRect.Y, barRect.Width * ratio, barRect.Height);
            var fill = item.IsCompleted
                ? CreateGradientBrush(Color.FromRgb(16, 185, 129), Color.FromRgb(6, 182, 212))
                : CreateGradientBrush(Color.FromRgb(37, 99, 235), Color.FromRgb(6, 182, 212));
            context.DrawRoundedRectangle(fill, null, fillRect, 4, 4);

            if (item.IsRunning)
            {
                var sheenWidth = Math.Min(ScaleWidth(24), Math.Max(8, fillRect.Width * 0.45));
                var sheenRect = new Rect(Math.Max(fillRect.X, fillRect.Right - sheenWidth), fillRect.Y, sheenWidth, fillRect.Height);
                context.DrawRoundedRectangle(CreateProgressSheenBrush(), null, sheenRect, 4, 4);
            }
        }

        DrawText(
            context,
            item.IsCompleted ? "已结束" : item.IsRunning ? "实时刷新" : "待执行",
            new Point(ScaleX(ProgressColumnX), top + 36),
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
            CreateBrush(16, 24, 40),
            ScaleWidth(StageColumnWidth),
            18);
        DrawWrappedText(
            context,
            item.IssueText,
            new Point(ScaleX(StageColumnX), top + 19),
            9.9,
            FontWeights.Normal,
            CreateBrush(102, 112, 133),
            ScaleWidth(StageColumnWidth),
            30);
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
            var y = top + (rowIndex * 22);
            for (var index = 0; index < rows[rowIndex].Length; index++)
            {
                var badge = rows[rowIndex][index];
                var x = ScaleX(MatrixColumnX + (index * 76));
                DrawMatrixBadge(context, badge, x, y, ScaleWidth(72), 17, hitRegions);
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
        context.DrawRoundedRectangle(background, new Pen(border, 1), rect, 8, 8);
        DrawText(context, badge.Label, new Point(x + 6, y + 1.5), 9.2, FontWeights.SemiBold, foreground);
        DrawText(context, badge.Value, new Point(x + width - ScaleWidth(24), y + 1.5), 9, FontWeights.SemiBold, foreground);
        hitRegions.Add(new ProxyChartHitRegion(
            rect,
            $"{badge.Title} ({badge.Label} {badge.Value})",
            BuildBadgeTooltipDescriptionWithDetail(badge)));
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
            new Point(ScaleX(VerdictColumnX), top + 29),
            9.8,
            FontWeights.Normal,
            CreateBrush(102, 112, 133),
            ScaleWidth(VerdictColumnWidth),
            24);
    }

    private void DrawFooter(DrawingContext context, int chartHeight)
    {
        DrawWrappedText(
            context,
            "图例：B5 基础项；Sys/Fn/Err/Str/Ref/MM/Cch/Iso 为深度探针。蓝=运行，绿=通过，橙=待复核，红=异常。",
            new Point(20, chartHeight - 22),
            9.8,
            FontWeights.Normal,
            CreateBrush(102, 112, 133),
            _chartWidth - 40,
            18);
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
        => $"{badge.Description}{Environment.NewLine}状态说明：{ResolveBadgeValueExplanation(badge)}";

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
                return $"基础 5 项里已通过 {passCount} 项，共 {totalCount} 项。";
            }

            return badge.State switch
            {
                ProxyBatchDeepComparisonBadgeState.Running => "基础 5 项仍在执行，当前结果还未全部返回。",
                ProxyBatchDeepComparisonBadgeState.Pending => "基础 5 项尚未开始。",
                _ => $"基础 5 项当前显示 {badge.Value}。"
            };
        }

        return badge.Value switch
        {
            "OK" => "已通过，表示该专项探针结果符合预期。",
            "NO" => "未通过，表示该专项探针已执行，但当前入口未满足预期。",
            "RV" => "待复核，表示已拿到结果，但需要人工复核后再下结论。",
            "SK" => "已跳过，表示本轮没有执行该专项探针或被策略跳过。",
            "Off" => "未启用，表示当前执行计划没有开启该专项探针。",
            "--" when badge.State == ProxyBatchDeepComparisonBadgeState.Running => "执行中，表示该专项探针尚未返回最终结果。",
            "--" => "未开始，表示该专项探针还没跑到这一项。",
            _ => $"当前显示 {badge.Value}。"
        };
    }

    private static string BuildBadgeTooltipDescriptionWithDetail(ProxyBatchDeepComparisonBadge badge)
    {
        if (!string.IsNullOrWhiteSpace(badge.DetailText))
        {
            return $"{badge.Description}{Environment.NewLine}\u72b6\u6001\u8bf4\u660e\uff1a{Environment.NewLine}{badge.DetailText}";
        }

        return BuildBadgeTooltipDescription(badge);
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

    private static LinearGradientBrush CreateGradientBrush(Color start, Color end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        brush.GradientStops.Add(new GradientStop(start, 0));
        brush.GradientStops.Add(new GradientStop(end, 1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateProgressSheenBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(180, 255, 255, 255), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed record MetricTile(string Label, string Value);
}
