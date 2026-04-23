using System.Windows;

namespace RelayBench.App.Services;

public sealed record ProxyChartHitRegion(
    Rect Bounds,
    string Title,
    string Description);
