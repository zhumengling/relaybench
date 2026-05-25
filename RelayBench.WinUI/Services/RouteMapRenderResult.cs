namespace RelayBench.WinUI.Services;

public sealed record RouteMapRenderResult(
    bool HasMap,
    string Summary,
    string GeoSummary,
    string? MapImagePath,
    string? Error);
