using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetTest.App.Infrastructure;

namespace NetTest.App.Services;

public sealed class ProxySingleCapabilityChartRenderService
{
    private const int DefaultChartWidth = 1040;
    private const int MinChartWidth = 860;
    private const double HeaderHeight = 86;
    private const double FooterHeight = 34;
    private const double MinRowHeight = 62;
    private const double SectionHeaderHeight = 30;
    private const double TableHeaderHeight = 30;
    private const double HorizontalPadding = 20;
    private const double RowTopPadding = 8;
    private const double RowBottomPadding = 10;
    private const double NameColumnX = 30;
    private const double StatusColumnX = 278;
    private const double CodeColumnX = 398;
    private const double MetricColumnX = 472;
    private const double MetricBarWidth = 176;
    private const double PreviewColumnX = 668;
    private const double NameFontSize = 12.4;
    private const double DetailFontSize = 9.6;
    private const double PreviewFontSize = 10.2;
    private const double StatusFontSize = 10.2;
    private const int MaxDetailLines = 2;
    private const int MaxPreviewLines = 4;
    private int _chartWidth = DefaultChartWidth;

    public ProxyTrendChartRenderResult Render(
        string baseUrl,
        string? model,
        IReadOnlyList<ProxySingleCapabilityChartItem> items,
        int completedCount,
        int totalCount,
        string footerText,
        int? preferredWidth = null)
    {
        _chartWidth = ResolveChartWidth(preferredWidth);
        var ordered = items
            .OrderBy(item => item.Order)
            .ToArray();
        var rowHeights = ordered
            .Select(MeasureRowHeight)
            .ToArray();
        var sectionCount = ordered
            .Select(item => item.SectionName)
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var chartHeight = (int)Math.Ceiling(
            HeaderHeight +
            TableHeaderHeight +
            FooterHeight +
            18 +
            rowHeights.Sum() +
            (sectionCount * SectionHeaderHeight));
        var metricMax = PickMetricMax(ordered);

        DrawingVisual visual = new();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(CreateBrush(255, 255, 255), null, new Rect(0, 0, _chartWidth, chartHeight));
            DrawHeader(context, baseUrl, model, ordered, completedCount, totalCount);
            DrawTableHeader(context, HeaderHeight);

            var currentTop = HeaderHeight + TableHeaderHeight;
            string? currentSection = null;

            for (var index = 0; index < ordered.Length; index++)
            {
                var item = ordered[index];
                if (!string.Equals(currentSection, item.SectionName, StringComparison.Ordinal))
                {
                    DrawSectionHeader(context, item.SectionName, item.SectionHint, currentTop);
                    currentSection = item.SectionName;
                    currentTop += SectionHeaderHeight;
                }

                DrawRow(context, item, index, currentTop, rowHeights[index], metricMax);
                currentTop += rowHeights[index];
            }

            DrawFooter(context, chartHeight, footerText);
        }

        RenderTargetBitmap bitmap = new(_chartWidth, chartHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var sectionSummary = string.Join(
            "；",
            ordered
                .GroupBy(item => item.SectionName)
                .Select(group => $"{group.Key} {group.Count()} 项"));
        var summary = string.IsNullOrWhiteSpace(sectionSummary)
            ? $"单次诊断图表已生成：已完成 {completedCount}/{totalCount} 项。"
            : $"单次诊断图表已生成：{sectionSummary}；已完成 {completedCount}/{totalCount} 项。";
        return new ProxyTrendChartRenderResult(true, summary, bitmap, null);
    }

    private void DrawHeader(
        DrawingContext context,
        string baseUrl,
        string? model,
        IReadOnlyList<ProxySingleCapabilityChartItem> items,
        int completedCount,
        int totalCount)
    {
        var headerRect = new Rect(14, 12, _chartWidth - 28, HeaderHeight - 14);
        context.DrawRoundedRectangle(CreateBrush(247, 249, 252), new Pen(CreateBrush(224, 231, 239), 1), headerRect, 14, 14);

        DrawText(context, "单站测试图表", new Point(28, 20), 21, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        var subtitle =
            $"{ProxyTrendStore.NormalizeBaseUrl(baseUrl)}  |  模型 {(!string.IsNullOrWhiteSpace(model) ? model : "--")}  |  已完成 {completedCount}/{totalCount}";
        DrawText(context, subtitle, new Point(30, 46), 11.5, FontWeights.Normal, CreateBrush(102, 112, 133));

        var sectionText = string.Join(
            "  ·  ",
            items
                .Select(item => item.SectionName)
                .Distinct(StringComparer.Ordinal));
        if (!string.IsNullOrWhiteSpace(sectionText))
        {
            DrawText(context, $"分区：{sectionText}", new Point(30, 63), 10.6, FontWeights.Normal, CreateBrush(37, 99, 235));
        }
    }

    private void DrawTableHeader(DrawingContext context, double top)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), TableHeaderHeight);
        context.DrawRoundedRectangle(CreateBrush(245, 247, 250), new Pen(CreateBrush(226, 232, 240), 1), rect, 8, 8);

        DrawText(context, "检测项", new Point(NameColumnX, top + 7), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "状态", new Point(StatusColumnX, top + 7), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "状态码", new Point(CodeColumnX, top + 7), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "主指标", new Point(MetricColumnX + 4, top + 7), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
        DrawText(context, "摘要 / 说明", new Point(PreviewColumnX, top + 7), 11.5, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private void DrawSectionHeader(DrawingContext context, string sectionName, string sectionHint, double top)
    {
        var rect = new Rect(HorizontalPadding, top + 6, _chartWidth - (HorizontalPadding * 2), SectionHeaderHeight - 8);
        context.DrawRoundedRectangle(CreateBrush(239, 246, 255), new Pen(CreateBrush(191, 219, 254), 1), rect, 8, 8);
        DrawText(context, sectionName, new Point(rect.X + 10, rect.Y + 3), 11.8, FontWeights.SemiBold, CreateBrush(30, 64, 175));
        DrawText(context, sectionHint, new Point(rect.X + 110, rect.Y + 4), 9.8, FontWeights.Normal, CreateBrush(102, 112, 133));
    }

    private void DrawRow(
        DrawingContext context,
        ProxySingleCapabilityChartItem item,
        int index,
        double top,
        double rowHeight,
        double metricMax)
    {
        var rect = new Rect(HorizontalPadding, top, _chartWidth - (HorizontalPadding * 2), rowHeight - 4);
        var background = index % 2 == 0
            ? CreateBrush(255, 255, 255)
            : CreateBrush(250, 251, 253);
        context.DrawRoundedRectangle(background, new Pen(CreateBrush(229, 231, 235), 1), rect, 10, 10);

        var detailColumnWidth = GetDetailColumnWidth();
        var previewColumnWidth = GetPreviewColumnWidth();
        var nameOrigin = new Point(NameColumnX, top + RowTopPadding);
        var nameHeight = DrawWrappedText(
            context,
            item.Name,
            nameOrigin,
            NameFontSize,
            FontWeights.SemiBold,
            CreateBrush(16, 24, 40),
            detailColumnWidth,
            maxLines: 1);
        DrawWrappedText(
            context,
            item.DetailText,
            new Point(NameColumnX, nameOrigin.Y + nameHeight + 3),
            DetailFontSize,
            FontWeights.Normal,
            CreateBrush(102, 112, 133),
            detailColumnWidth,
            MaxDetailLines);

        var badgeHeight = 22d;
        var rowCenterTop = top + ((rect.Height - badgeHeight) / 2);
        DrawStatusBadge(context, item, rowCenterTop);
        DrawText(
            context,
            item.StatusCode?.ToString() ?? "--",
            new Point(CodeColumnX + 8, top + ((rect.Height - 15) / 2)),
            10.6,
            FontWeights.SemiBold,
            CreateBrush(16, 24, 40));

        var metricValue = item.IsCompleted && item.MetricValueMs.HasValue
            ? Math.Min(item.MetricValueMs.Value, metricMax)
            : 0;
        var barValue = item.IsCompleted && item.MetricValueMs.HasValue
            ? Math.Max(0, metricMax - metricValue)
            : 0;
        DrawBarCell(
            context,
            new Rect(MetricColumnX, top + ((rect.Height - 16) / 2), MetricBarWidth, 16),
            barValue,
            metricMax,
            item.MetricText,
            ResolveMetricColor(item));

        DrawWrappedText(
            context,
            item.PreviewText,
            new Point(PreviewColumnX, top + RowTopPadding),
            PreviewFontSize,
            FontWeights.Normal,
            CreateBrush(102, 112, 133),
            previewColumnWidth,
            MaxPreviewLines);
    }

    private static void DrawStatusBadge(DrawingContext context, ProxySingleCapabilityChartItem item, double top)
    {
        var brush = item.StatusText switch
        {
            "支持" or "成功" or "通过" => CreateBrush(236, 253, 245),
            "进行中" => CreateBrush(239, 246, 255),
            "等待中" or "未运行" or "未启用" => CreateBrush(242, 244, 247),
            "待复核" => CreateBrush(255, 247, 237),
            _ => CreateBrush(254, 242, 242)
        };

        var label = item.ReceivedDone ? $"{item.StatusText}/DONE" : item.StatusText;
        var badgeWidth = Math.Clamp(Math.Ceiling(MeasureTextWidth(label, StatusFontSize, FontWeights.SemiBold) + 24), 78, 112);
        var rect = new Rect(StatusColumnX, top, badgeWidth, 22);
        context.DrawRoundedRectangle(brush, null, rect, 11, 11);
        DrawText(
            context,
            label,
            new Point(rect.X + 12, rect.Y + 3),
            StatusFontSize,
            FontWeights.SemiBold,
            CreateBrush(16, 24, 40));
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
        DrawText(context, label, new Point(rect.X + 7, rect.Y + 0.5), 10.1, FontWeights.SemiBold, CreateBrush(16, 24, 40));
    }

    private static void DrawFooter(DrawingContext context, int chartHeight, string footerText)
    {
        DrawText(
            context,
            TrimText(string.IsNullOrWhiteSpace(footerText) ? "单次诊断会先跑基础能力，再按需补充增强测试和深度测试。" : footerText, 112),
            new Point(20, chartHeight - 22),
            10.4,
            FontWeights.Normal,
            CreateBrush(102, 112, 133));
    }

    private static Color ResolveMetricColor(ProxySingleCapabilityChartItem item)
    {
        if (!item.IsCompleted)
        {
            return Color.FromRgb(148, 163, 184);
        }

        if (!item.Success)
        {
            return Color.FromRgb(239, 68, 68);
        }

        return Color.FromRgb(37, 99, 235);
    }

    private double MeasureRowHeight(ProxySingleCapabilityChartItem item)
    {
        var nameHeight = MeasureWrappedTextHeight(item.Name, NameFontSize, FontWeights.SemiBold, GetDetailColumnWidth(), maxLines: 1);
        var detailHeight = MeasureWrappedTextHeight(item.DetailText, DetailFontSize, FontWeights.Normal, GetDetailColumnWidth(), MaxDetailLines);
        var previewHeight = MeasureWrappedTextHeight(item.PreviewText, PreviewFontSize, FontWeights.Normal, GetPreviewColumnWidth(), MaxPreviewLines);
        var leftHeight = nameHeight + 3 + detailHeight;
        var contentHeight = Math.Max(Math.Max(leftHeight, previewHeight), 22);
        return Math.Max(MinRowHeight, RowTopPadding + contentHeight + RowBottomPadding);
    }

    private static double PickMetricMax(IEnumerable<ProxySingleCapabilityChartItem> items)
    {
        var observed = items
            .Select(item => item.MetricValueMs)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();

        if (observed.Length == 0)
        {
            return 1200;
        }

        var percentileIndex = Math.Clamp((int)Math.Floor((observed.Length - 1) * 0.85), 0, observed.Length - 1);
        var percentile = observed[percentileIndex];
        var maxObserved = observed[^1];
        var chosenMax = Math.Max(percentile, Math.Min(maxObserved, percentile * 1.35));
        return Math.Max(800, Math.Ceiling(chosenMax * 1.1 / 100d) * 100d);
    }

    private static string TrimText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(1, maxLength - 1)] + "…";
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        Point origin,
        double fontSize,
        FontWeight fontWeight,
        Brush brush)
    {
        context.DrawText(CreateFormattedText(text, fontSize, fontWeight, brush), origin);
    }

    private static double DrawWrappedText(
        DrawingContext context,
        string text,
        Point origin,
        double fontSize,
        FontWeight fontWeight,
        Brush brush,
        double maxWidth,
        int maxLines)
    {
        var formattedText = CreateWrappedFormattedText(text, fontSize, fontWeight, brush, maxWidth, maxLines);
        context.DrawText(formattedText, origin);
        return EstimateWrappedTextHeight(text, fontSize, fontWeight, maxWidth, maxLines);
    }

    private static double MeasureWrappedTextHeight(
        string text,
        double fontSize,
        FontWeight fontWeight,
        double maxWidth,
        int maxLines)
    {
        return EstimateWrappedTextHeight(text, fontSize, fontWeight, maxWidth, maxLines);
    }

    private static double MeasureTextWidth(string text, double fontSize, FontWeight fontWeight)
        => Math.Ceiling(CreateFormattedText(text, fontSize, fontWeight, Brushes.Transparent).WidthIncludingTrailingWhitespace);

    private static FormattedText CreateFormattedText(
        string text,
        double fontSize,
        FontWeight fontWeight,
        Brush brush)
        => new(
            string.IsNullOrWhiteSpace(text) ? " " : text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            brush,
            1.0);

    private static FormattedText CreateWrappedFormattedText(
        string text,
        double fontSize,
        FontWeight fontWeight,
        Brush brush,
        double maxWidth,
        int maxLines)
    {
        var formattedText = CreateFormattedText(text, fontSize, fontWeight, brush);
        formattedText.MaxTextWidth = Math.Max(24, maxWidth);
        formattedText.MaxTextHeight = Math.Max(fontSize * 1.45, fontSize * 1.45 * maxLines);
        formattedText.Trimming = TextTrimming.CharacterEllipsis;
        return formattedText;
    }

    private static double EstimateWrappedTextHeight(
        string text,
        double fontSize,
        FontWeight fontWeight,
        double maxWidth,
        int maxLines)
    {
        var lines = EstimateWrappedLineCount(text, fontSize, fontWeight, maxWidth, maxLines);
        return Math.Ceiling(lines * (fontSize * 1.42));
    }

    private static int EstimateWrappedLineCount(
        string text,
        double fontSize,
        FontWeight fontWeight,
        double maxWidth,
        int maxLines)
    {
        if (maxLines <= 1)
        {
            return 1;
        }

        var normalized = string.IsNullOrWhiteSpace(text)
            ? " "
            : text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lineCount = 0;

        foreach (var logicalLine in normalized.Split('\n'))
        {
            lineCount++;
            if (lineCount >= maxLines)
            {
                return maxLines;
            }

            if (string.IsNullOrEmpty(logicalLine))
            {
                continue;
            }

            var currentWidth = 0d;
            foreach (var character in logicalLine)
            {
                var charWidth = MeasureTextWidth(character.ToString(), fontSize, fontWeight);
                if (currentWidth > 0 && currentWidth + charWidth > maxWidth)
                {
                    lineCount++;
                    if (lineCount >= maxLines)
                    {
                        return maxLines;
                    }

                    currentWidth = charWidth;
                    continue;
                }

                currentWidth += charWidth;
            }
        }

        return Math.Clamp(lineCount, 1, maxLines);
    }

    private double GetDetailColumnWidth()
        => StatusColumnX - NameColumnX - 18;

    private double GetPreviewColumnWidth()
        => _chartWidth - PreviewColumnX - HorizontalPadding - 20;

    private static int ResolveChartWidth(int? preferredWidth)
    {
        if (!preferredWidth.HasValue)
        {
            return DefaultChartWidth;
        }

        return Math.Max(MinChartWidth, preferredWidth.Value);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
