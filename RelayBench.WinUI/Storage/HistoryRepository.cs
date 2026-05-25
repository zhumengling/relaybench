using Microsoft.Data.Sqlite;

namespace RelayBench.WinUI.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IHistoryRepository"/>.
/// All dates are stored as ISO-8601 UTC strings. Queries use parameterized SQL exclusively.
/// </summary>
public sealed class HistoryRepository : IHistoryRepository
{
    private static readonly SemaphoreSlim s_databaseGate = new(1, 1);

    /// <inheritdoc/>
    public async Task<string> SaveAsync(HistoryReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.RunId);

        await s_databaseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = HistoryDatabase.CreateConnection();
                using var cmd = connection.CreateCommand();

                cmd.CommandText = """
                    INSERT INTO history_reports (run_id, created_at, test_type, endpoint, summary, score, duration_ms, payload_json)
                    VALUES (@runId, @createdAt, @testType, @endpoint, @summary, @score, @durationMs, @payloadJson)
                    ON CONFLICT(run_id) DO UPDATE SET
                        created_at   = excluded.created_at,
                        test_type    = excluded.test_type,
                        endpoint     = excluded.endpoint,
                        summary      = excluded.summary,
                        score        = excluded.score,
                        duration_ms  = excluded.duration_ms,
                        payload_json = excluded.payload_json
                    """;

                cmd.Parameters.AddWithValue("@runId", report.RunId);
                cmd.Parameters.AddWithValue("@createdAt", report.CreatedAtUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@testType", report.TestType);
                cmd.Parameters.AddWithValue("@endpoint", report.Endpoint);
                cmd.Parameters.AddWithValue("@summary", report.Summary);
                cmd.Parameters.AddWithValue("@score", report.Score.HasValue ? report.Score.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@durationMs", report.DurationMs.HasValue ? report.DurationMs.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@payloadJson", report.PayloadJson);

                cmd.ExecuteNonQuery();
                return report.RunId;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            s_databaseGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HistoryReportSummary>> QueryAsync(HistoryQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await s_databaseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = HistoryDatabase.CreateConnection();
                using var cmd = connection.CreateCommand();

                var whereClauses = new List<string>();

                if (query.FromUtc.HasValue)
                {
                    whereClauses.Add("created_at >= @fromUtc");
                    cmd.Parameters.AddWithValue("@fromUtc", query.FromUtc.Value.ToString("o"));
                }

                if (query.ToUtc.HasValue)
                {
                    whereClauses.Add("created_at <= @toUtc");
                    cmd.Parameters.AddWithValue("@toUtc", query.ToUtc.Value.ToString("o"));
                }

                if (!string.IsNullOrWhiteSpace(query.TestType))
                {
                    whereClauses.Add("test_type = @testType");
                    cmd.Parameters.AddWithValue("@testType", query.TestType);
                }

                if (!string.IsNullOrWhiteSpace(query.EndpointContains))
                {
                    whereClauses.Add("endpoint LIKE @endpointContains");
                    cmd.Parameters.AddWithValue("@endpointContains", $"%{query.EndpointContains}%");
                }

                var whereClause = whereClauses.Count > 0
                    ? "WHERE " + string.Join(" AND ", whereClauses)
                    : string.Empty;

                cmd.CommandText = $"""
                    SELECT run_id, created_at, test_type, endpoint, summary, score, duration_ms
                    FROM history_reports
                    {whereClause}
                    ORDER BY created_at DESC
                    LIMIT @limit
                    """;

                cmd.Parameters.AddWithValue("@limit", query.Limit);

                using var reader = cmd.ExecuteReader();
                var results = new List<HistoryReportSummary>();

                while (reader.Read())
                {
                    results.Add(new HistoryReportSummary(
                        RunId: reader.GetString(0),
                        CreatedAtUtc: DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                        TestType: reader.GetString(2),
                        Endpoint: reader.GetString(3),
                        Summary: reader.GetString(4),
                        Score: reader.IsDBNull(5) ? null : reader.GetDouble(5),
                        DurationMs: reader.IsDBNull(6) ? null : reader.GetInt32(6)));
                }

                return (IReadOnlyList<HistoryReportSummary>)results;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            s_databaseGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<HistoryReport?> GetAsync(string runId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await s_databaseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = HistoryDatabase.CreateConnection();
                using var cmd = connection.CreateCommand();

                cmd.CommandText = """
                    SELECT run_id, created_at, test_type, endpoint, summary, score, duration_ms, payload_json
                    FROM history_reports
                    WHERE run_id = @runId
                    """;

                cmd.Parameters.AddWithValue("@runId", runId);

                using var reader = cmd.ExecuteReader();

                if (!reader.Read())
                    return (HistoryReport?)null;

                return new HistoryReport(
                    RunId: reader.GetString(0),
                    CreatedAtUtc: DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                    TestType: reader.GetString(2),
                    Endpoint: reader.GetString(3),
                    Summary: reader.GetString(4),
                    Score: reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    DurationMs: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    PayloadJson: reader.GetString(7));
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            s_databaseGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<int> DeleteAsync(IEnumerable<string> runIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runIds);

        await s_databaseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var idList = runIds.ToList();
                if (idList.Count == 0)
                    return 0;

                using var connection = HistoryDatabase.CreateConnection();
                using var transaction = connection.BeginTransaction();

                var totalDeleted = 0;

                // Use parameterized delete for each id to avoid SQL injection
                // Batch them in a single transaction for performance
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;

                // Build parameterized IN clause
                var paramNames = new List<string>(idList.Count);
                for (var i = 0; i < idList.Count; i++)
                {
                    var paramName = $"@id{i}";
                    paramNames.Add(paramName);
                    cmd.Parameters.AddWithValue(paramName, idList[i]);
                }

                cmd.CommandText = $"DELETE FROM history_reports WHERE run_id IN ({string.Join(", ", paramNames)})";
                totalDeleted = cmd.ExecuteNonQuery();

                transaction.Commit();
                return totalDeleted;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            s_databaseGate.Release();
        }
    }
}
