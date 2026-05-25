namespace RelayBench.WinUI.Storage;

/// <summary>
/// Static helper that any ViewModel can call to record a test run into the history database.
/// </summary>
public static class RunHistoryRecorder
{
    private static readonly IHistoryRepository Repository = new HistoryRepository();

    /// <summary>
    /// Records a test run into the history database.
    /// </summary>
    /// <param name="type">Test type (e.g., "Single Station", "Batch", "Network", "Data Safety", "Proxy").</param>
    /// <param name="endpoint">The endpoint or target tested.</param>
    /// <param name="summary">A brief summary of the result.</param>
    /// <param name="score">Optional score (0-100).</param>
    /// <param name="durationMs">Optional duration in milliseconds.</param>
    /// <param name="payloadJson">Optional JSON payload with detailed results.</param>
    /// <returns>The RunId of the saved report.</returns>
    public static Task<string> RecordAsync(
        string type,
        string endpoint,
        string summary,
        double? score = null,
        int? durationMs = null,
        string? payloadJson = null)
    {
        var report = new HistoryReport(
            RunId: $"RPT-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            CreatedAtUtc: DateTime.UtcNow,
            TestType: type,
            Endpoint: endpoint,
            Summary: summary,
            Score: score,
            DurationMs: durationMs,
            PayloadJson: payloadJson ?? "{}");

        return Repository.SaveAsync(report);
    }
}
