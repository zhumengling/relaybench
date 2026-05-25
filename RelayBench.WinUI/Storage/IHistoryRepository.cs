namespace RelayBench.WinUI.Storage;

/// <summary>
/// Provides CRUD operations for history reports stored in SQLite.
/// </summary>
public interface IHistoryRepository
{
    /// <summary>
    /// Saves a history report. If a report with the same RunId already exists, it is updated.
    /// </summary>
    /// <returns>The RunId of the saved report.</returns>
    Task<string> SaveAsync(HistoryReport report, CancellationToken ct = default);

    /// <summary>
    /// Queries history report summaries using the specified filter criteria.
    /// Results are ordered by CreatedAtUtc descending.
    /// </summary>
    Task<IReadOnlyList<HistoryReportSummary>> QueryAsync(HistoryQuery query, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a full history report by RunId, including the PayloadJson.
    /// </summary>
    /// <returns>The report if found; otherwise null.</returns>
    Task<HistoryReport?> GetAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Deletes history reports matching the specified RunIds.
    /// </summary>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteAsync(IEnumerable<string> runIds, CancellationToken ct = default);
}
