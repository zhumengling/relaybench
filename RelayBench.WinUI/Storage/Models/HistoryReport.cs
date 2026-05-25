namespace RelayBench.WinUI.Storage;

/// <summary>
/// Represents a full history report including the raw payload JSON.
/// </summary>
public sealed record HistoryReport(
    string RunId,
    DateTime CreatedAtUtc,
    string TestType,
    string Endpoint,
    string Summary,
    double? Score,
    int? DurationMs,
    string PayloadJson);
