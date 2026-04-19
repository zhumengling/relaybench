using System.Windows;

namespace NetTest.App.Services;

public sealed record ProxyChartHitRegion(
    Rect Bounds,
    string Title,
    string Description);
