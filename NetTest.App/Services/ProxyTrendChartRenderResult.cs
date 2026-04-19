using System.Windows.Media.Imaging;

namespace NetTest.App.Services;

public sealed record ProxyTrendChartRenderResult(
    bool HasChart,
    string Summary,
    BitmapSource? ChartImage,
    string? Error);
