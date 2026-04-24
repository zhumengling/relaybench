using System.Windows.Media.Imaging;

namespace RelayBench.App.Services;

public sealed record ProxyTrendChartRenderResult(
    bool HasChart,
    string Summary,
    BitmapSource? ChartImage,
    string? Error,
    IReadOnlyList<ProxyChartHitRegion>? HitRegions = null,
    IReadOnlyList<ProxyChartActivityRegion>? ActivityRegions = null);
