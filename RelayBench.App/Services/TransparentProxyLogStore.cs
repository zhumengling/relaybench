using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

public sealed class TransparentProxyLogStore
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private bool _initialized;

    public TransparentProxyLogStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task AppendAsync(TransparentProxyLogEntry entry, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO transparent_proxy_logs (
                    timestamp_utc,
                    level,
                    method,
                    path,
                    route_name,
                    status_code,
                    elapsed_ms,
                    message,
                    model_name,
                    request_id,
                    wire_api,
                    attempt_summary)
                VALUES (
                    $timestamp_utc,
                    $level,
                    $method,
                    $path,
                    $route_name,
                    $status_code,
                    $elapsed_ms,
                    $message,
                    $model_name,
                    $request_id,
                    $wire_api,
                    $attempt_summary);
                """;
            BindEntry(command, entry);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IReadOnlyList<TransparentProxyLogEntry>> LoadRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    timestamp_utc,
                    level,
                    method,
                    path,
                    route_name,
                    status_code,
                    elapsed_ms,
                    message,
                    model_name,
                    request_id,
                    wire_api,
                    attempt_summary
                FROM transparent_proxy_logs
                ORDER BY id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

            List<TransparentProxyLogEntry> entries = [];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM transparent_proxy_logs;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<string> ExportCsvAsync(string exportDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(exportDirectory);
        var path = Path.Combine(exportDirectory, $"transparent-proxy-logs-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.csv");
        var entries = await LoadRecentAsync(5000, cancellationToken).ConfigureAwait(false);

        StringBuilder builder = new();
        builder.AppendLine("time,level,method,path,model,route,status,elapsed_ms,request_id,wire_api,message,attempt_summary");
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join(",",
                Csv(entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Csv(entry.Level),
                Csv(entry.Method),
                Csv(entry.Path),
                Csv(entry.ModelName),
                Csv(entry.RouteName),
                Csv(entry.StatusCode.ToString(CultureInfo.InvariantCulture)),
                Csv(entry.ElapsedMs.ToString(CultureInfo.InvariantCulture)),
                Csv(entry.RequestId),
                Csv(entry.WireApi),
                Csv(entry.Message),
                Csv(entry.AttemptSummary)));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS transparent_proxy_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                level TEXT NOT NULL,
                method TEXT NOT NULL,
                path TEXT NOT NULL,
                route_name TEXT NOT NULL,
                status_code INTEGER NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                message TEXT NOT NULL,
                model_name TEXT NOT NULL,
                request_id TEXT NOT NULL DEFAULT '',
                wire_api TEXT NOT NULL DEFAULT '',
                attempt_summary TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_transparent_proxy_logs_timestamp
                ON transparent_proxy_logs(timestamp_utc DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "request_id", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "wire_api", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "attempt_summary", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        var exists = false;
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(transparent_proxy_logs);";
            await using var reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE transparent_proxy_logs ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection CreateConnection()
        => new($"Data Source={_databasePath}");

    private static void BindEntry(SqliteCommand command, TransparentProxyLogEntry entry)
    {
        command.Parameters.AddWithValue("$timestamp_utc", entry.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$level", ProbeTraceRedactor.RedactText(entry.Level));
        command.Parameters.AddWithValue("$method", ProbeTraceRedactor.RedactText(entry.Method));
        command.Parameters.AddWithValue("$path", ProbeTraceRedactor.RedactUrl(entry.Path));
        command.Parameters.AddWithValue("$route_name", ProbeTraceRedactor.RedactText(entry.RouteName));
        command.Parameters.AddWithValue("$status_code", entry.StatusCode);
        command.Parameters.AddWithValue("$elapsed_ms", entry.ElapsedMs);
        command.Parameters.AddWithValue("$message", ProbeTraceRedactor.RedactText(entry.Message));
        command.Parameters.AddWithValue("$model_name", ProbeTraceRedactor.RedactText(entry.ModelName));
        command.Parameters.AddWithValue("$request_id", ProbeTraceRedactor.RedactText(entry.RequestId));
        command.Parameters.AddWithValue("$wire_api", ProbeTraceRedactor.RedactText(entry.WireApi));
        command.Parameters.AddWithValue("$attempt_summary", ProbeTraceRedactor.RedactText(entry.AttemptSummary));
    }

    private static TransparentProxyLogEntry ReadEntry(SqliteDataReader reader)
    {
        var timestampText = reader.GetString(0);
        var timestamp = DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        return new TransparentProxyLogEntry(
            timestamp,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11));
    }

    private static string Csv(string? value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
