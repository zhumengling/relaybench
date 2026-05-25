namespace RelayBench.WinUI.Storage;

/// <summary>
/// A lightweight summary of a history report without the full payload JSON.
/// Used for list views and query results.
/// </summary>
public sealed record HistoryReportSummary(
    string RunId,
    DateTime CreatedAtUtc,
    string TestType,
    string Endpoint,
    string Summary,
    double? Score,
    int? DurationMs);
