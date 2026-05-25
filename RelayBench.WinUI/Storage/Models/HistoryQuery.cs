namespace RelayBench.WinUI.Storage;

/// <summary>
/// Filter parameters for querying history reports.
/// All fields are optional; non-null values are combined with AND logic.
/// </summary>
public sealed record HistoryQuery(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? TestType = null,
    string? EndpointContains = null,
    int Limit = 500);
