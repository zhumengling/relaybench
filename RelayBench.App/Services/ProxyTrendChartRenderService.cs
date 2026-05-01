using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

public sealed partial class ProxyTrendChartRenderService
{
    private const int DefaultChartWidth = 1040;
    private const int MinChartWidth = 860;
    private const int MaxChartWidth = 2600;
    private const int ChartHeight = 620;
    private const double HorizontalPadding = 24;
    private const double HeaderHeight = 72;
    private const double FooterHeight = 34;
    private const double PanelGap = 12;
    private int _chartWidth = DefaultChartWidth;

    public ProxyTrendChartRenderResult Render(
        IReadOnlyList<ProxyTrendEntry> records,
        string targetLabel,
        int? preferredWidth = null)
    {
        if (records.Count == 0)
        {
            return new ProxyTrendChartRenderResult(
                false,
                "当前没有趋势样本，暂时无法生成稳定性图表。",
                null,
                "当前没有趋势样本，暂时无法生成稳定性图表。");
        }

        _chartWidth = ResolveChartWidth(preferredWidth);
        var ordered = records.OrderBy(record => record.Timestamp).ToArray();
        var stabilitySeries = BuildMetricSeries(
            "稳定性",
            ordered,
            record => (double?)ResolveSuccessRate(record),
            0,
            100,
            value => $"{value:F0}%",
            Color.FromRgb(37, 99, 235));
        var chatLatencySeries = BuildMetricSeries(
            "普通延迟",
            ordered,
            record => record.ChatLatencyMs,
            0,
            PickChatLatencyMax(ordered),
            value => $"{value:F0} ms",
            Color.FromRgb(245, 158, 11));
        var ttftSeries = BuildMetricSeries(
            "TTFT（首字延迟）",
            ordered,
            record => record.StreamFirstTokenLatencyMs,
            0,
            PickTtftMax(ordered),
            value => $"{value:F0} ms",
            Color.FromRgb(22, 163, 74));

        DrawingVisual visual = new();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(CreateBrush(255, 255, 255), null, new Rect(0, 0, _chartWidth, ChartHeight));
            DrawHeader(context, targetLabel, ordered);

            var panelHeight = (ChartHeight - HeaderHeight - FooterHeight - (PanelGap * 2) - 18) / 3d;
            var stabilityRect = new Rect(HorizontalPadding, HeaderHeight, _chartWidth - (HorizontalPadding * 2), panelHeight);
            var chatLatencyRect = new Rect(HorizontalPadding, stabilityRect.Bottom + PanelGap, _chartWidth - (HorizontalPadding * 2), panelHeight);
            var ttftRect = new Rect(HorizontalPadding, chatLatencyRect.Bottom + PanelGap, _chartWidth - (HorizontalPadding * 2), panelHeight);

            DrawMetricPanel(context, stabilityRect, stabilitySeries, higherIsBetter: true);
            DrawMetricPanel(context, chatLatencyRect, chatLatencySeries, higherIsBetter: false);
            DrawMetricPanel(context, ttftRect, ttftSeries, higherIsBetter: false);
            DrawFooter(context);
        }

        RenderTargetBitmap bitmap = new(_chartWidth, ChartHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var summary =
            $"趋势图已生成：{ordered.Length} 个样本，时间范围 {ordered.First().Timestamp:MM-dd HH:mm} ~ {ordered.Last().Timestamp:MM-dd HH:mm}。"
            + " 当前显示稳定性、普通延迟与 TTFT 三条曲线。";

        return new ProxyTrendChartRenderResult(true, summary, bitmap, null);
    }
}
