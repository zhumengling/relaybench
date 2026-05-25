using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using SkiaSharp;

namespace RelayBench.WinUI.Charts;

/// <summary>
/// Provides theme-aware color palettes for LiveCharts2 chart rendering.
/// Supports Light and Dark themes with colors optimized for readability
/// on their respective backgrounds.
/// </summary>
public static class ChartPalette
{
    // Light theme palette: blues, greens, oranges suitable for white backgrounds
    private static readonly SKColor[] LightColors =
    [
        new SKColor(0x33, 0x7A, 0xB7),  // Steel blue (P50)
        new SKColor(0xE6, 0x7E, 0x22),  // Orange (P95)
        new SKColor(0xC0, 0x39, 0x2B),  // Red-orange (P99)
        new SKColor(0x27, 0xAE, 0x60),  // Green (Throughput)
        new SKColor(0x29, 0x80, 0xB9),  // Cerulean (Cache Hit)
        new SKColor(0x8E, 0x44, 0xAD),  // Purple (Cache Miss)
        new SKColor(0x16, 0xA0, 0x85),  // Teal
        new SKColor(0xD3, 0x54, 0x00),  // Dark orange
    ];

    // Dark theme palette: brighter/lighter versions for dark backgrounds
    private static readonly SKColor[] DarkColors =
    [
        new SKColor(0x5D, 0xAE, 0xF7),  // Light blue (P50)
        new SKColor(0xF3, 0x9C, 0x12),  // Bright orange (P95)
        new SKColor(0xE7, 0x4C, 0x3C),  // Bright red (P99)
        new SKColor(0x2E, 0xCC, 0x71),  // Bright green (Throughput)
        new SKColor(0x52, 0xC4, 0xEB),  // Sky blue (Cache Hit)
        new SKColor(0xBB, 0x8F, 0xCE),  // Light purple (Cache Miss)
        new SKColor(0x1A, 0xBC, 0x9C),  // Bright teal
        new SKColor(0xF0, 0x7B, 0x3F),  // Light orange
    ];

    /// <summary>
    /// Returns an array of <see cref="SKColor"/> values appropriate for the given theme.
    /// The palette contains at least 8 colors suitable for charting series such as
    /// P50, P95, P99, throughput, cache hit, cache miss, and additional metrics.
    /// </summary>
    /// <param name="theme">The current application theme.</param>
    /// <returns>An array of theme-appropriate chart colors.</returns>
    public static SKColor[] ForTheme(ElementTheme theme) =>
        ResolveTheme(theme) == ElementTheme.Dark ? DarkColors : LightColors;

    public static ElementTheme ResolveTheme(ElementTheme theme) =>
        theme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark;

    public static bool IsDarkTheme(ElementTheme theme) =>
        ResolveTheme(theme) == ElementTheme.Dark;

    /// <summary>
    /// Returns a <see cref="SolidColorPaint"/> suitable for chart legend text
    /// in the given theme.
    /// </summary>
    /// <param name="theme">The current application theme.</param>
    /// <returns>A paint configured with the appropriate legend text color.</returns>
    public static SolidColorPaint LegendPaint(ElementTheme theme) =>
        IsDarkTheme(theme)
            ? new SolidColorPaint(new SKColor(0xF8, 0xFA, 0xFC))
            : new SolidColorPaint(new SKColor(0x2C, 0x2C, 0x2C));

    public static SolidColorPaint AxisTextPaint(ElementTheme theme) =>
        IsDarkTheme(theme)
            ? new SolidColorPaint(new SKColor(0xDD, 0xE6, 0xF3))
            : new SolidColorPaint(new SKColor(0x1F, 0x29, 0x37));

    public static SolidColorPaint SeparatorPaint(ElementTheme theme) =>
        IsDarkTheme(theme)
            ? new SolidColorPaint(new SKColor(0x8A, 0xA1, 0xC3, 0x42)) { StrokeThickness = 1 }
            : new SolidColorPaint(new SKColor(0x94, 0xA3, 0xB8, 0x7A)) { StrokeThickness = 1 };

    public static SolidColorPaint TooltipBackgroundPaint(ElementTheme theme) =>
        IsDarkTheme(theme)
            ? new SolidColorPaint(new SKColor(0x10, 0x18, 0x26, 0xF0))
            : new SolidColorPaint(new SKColor(0xFF, 0xFF, 0xFF, 0xF0));
}
