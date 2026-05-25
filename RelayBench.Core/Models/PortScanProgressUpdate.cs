namespace RelayBench.Core.Models;

public sealed record PortScanProgressUpdate(
    DateTimeOffset Timestamp,
    int CompletedEndpointCount,
    int TotalEndpointCount,
    int OpenEndpointCount,
    string? CurrentEndpoint,
    string Message,
    PortScanFinding? Finding);
