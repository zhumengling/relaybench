using System.Windows.Media.Imaging;

namespace RelayBench.App.Services;

public sealed record RouteMapRenderResult(
    bool HasMap,
    string Summary,
    string GeoSummary,
    BitmapSource? MapImage,
    string? Error);
