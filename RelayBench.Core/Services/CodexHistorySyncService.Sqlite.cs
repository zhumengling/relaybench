using Microsoft.Data.Sqlite;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class CodexHistorySyncService
{
    private static string StateDbPath(string codexHome)
        => Path.Combine(codexHome, DbFileBasename);

    private static async Task<CodexProviderCounts?> ReadSqliteProviderCountsAsync(string codexHome)
    {
        var dbPath = StateDbPath(codexHome);
        if (!File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            await using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync();
            if (!await TableExistsAsync(connection, "threads"))
            {
                return new CodexProviderCounts(
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    true,
                    "state_5.sqlite 不包含 threads 表");
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                  CASE
                    WHEN model_provider IS NULL OR model_provider = '' THEN '(missing)'
                    ELSE model_provider
                  END AS model_provider,
                  archived,
                  COUNT(*) AS count
                FROM threads
                GROUP BY model_provider, archived
                ORDER BY archived, model_provider
                """;

            Dictionary<string, int> sessions = new(StringComparer.Ordinal);
            Dictionary<string, int> archivedSessions = new(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var provider = reader.GetString(0);
                var archived = reader.GetInt64(1) != 0;
                var bucket = archived ? archivedSessions : sessions;
                bucket[provider] = reader.GetInt32(2);
            }

            return new CodexProviderCounts(SortCounts(sessions), SortCounts(archivedSessions));
        }
        catch (Exception error) when (IsSqliteBusyError(error))
        {
            return new CodexProviderCounts(
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                true,
                "state_5.sqlite 正在被占用");
        }
        catch (Exception error) when (IsSqliteMalformedError(error) || error is SqliteException)
        {
            return new CodexProviderCounts(
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                true,
                $"state_5.sqlite 不可读：{error.Message}");
        }
    }

    private static async Task<CodexSqliteRepairStats?> ReadSqliteRepairStatsAsync(
        string codexHome,
        IReadOnlyCollection<string> userEventThreadIds,
        IReadOnlyDictionary<string, string> threadCwdsById)
    {
        var dbPath = StateDbPath(codexHome);
        if (!File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            await using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync();
            if (!await TableExistsAsync(connection, "threads"))
            {
                return null;
            }

            var userEventRowsNeedingRepair = 0;
            if (userEventThreadIds.Count > 0 && await TableHasColumnAsync(connection, "threads", "has_user_event"))
            {
                await using var userEventCommand = connection.CreateCommand();
                userEventCommand.CommandText = "SELECT has_user_event FROM threads WHERE id = $id";
                var idParameter = userEventCommand.Parameters.Add("$id", SqliteType.Text);
                foreach (var threadId in userEventThreadIds)
                {
                    idParameter.Value = threadId;
                    var value = await userEventCommand.ExecuteScalarAsync();
                    if (value is not null && value is not DBNull && Convert.ToInt64(value) != 1)
                    {
                        userEventRowsNeedingRepair++;
                    }
                }
            }

            var cwdRowsNeedingRepair = 0;
            if (threadCwdsById.Count > 0 && await TableHasColumnAsync(connection, "threads", "cwd"))
            {
                await using var cwdCommand = connection.CreateCommand();
                cwdCommand.CommandText = "SELECT cwd FROM threads WHERE id = $id";
                var idParameter = cwdCommand.Parameters.Add("$id", SqliteType.Text);
                foreach (var (threadId, expectedCwd) in threadCwdsById)
                {
                    if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(expectedCwd))
                    {
                        continue;
                    }

                    idParameter.Value = threadId;
                    var value = await cwdCommand.ExecuteScalarAsync();
                    if (value is not null &&
                        value is not DBNull &&
                        !string.Equals(Convert.ToString(value), expectedCwd, StringComparison.Ordinal))
                    {
                        cwdRowsNeedingRepair++;
                    }
                }
            }

            return new CodexSqliteRepairStats(userEventRowsNeedingRepair, cwdRowsNeedingRepair);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> AssertSqliteWritableAsync(string codexHome, int? busyTimeoutMs = null)
    {
        var dbPath = StateDbPath(codexHome);
        if (!File.Exists(dbPath))
        {
            return false;
        }

        await using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadWriteCreate);
        try
        {
            await connection.OpenAsync();
            await SetBusyTimeoutAsync(connection, busyTimeoutMs);
            if (!await TableExistsAsync(connection, "threads"))
            {
                throw new InvalidOperationException("state_5.sqlite 不包含 threads 表。");
            }

            await ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE");
            await ExecuteNonQueryAsync(connection, "ROLLBACK");
            return true;
        }
        catch (Exception error)
        {
            throw WrapSqliteError(error, "更新 Codex 历史索引");
        }
    }

    private static async Task<(int UpdatedRows, int ProviderRowsUpdated, int UserEventRowsUpdated, int CwdRowsUpdated, bool DatabasePresent)> UpdateSqliteProviderAsync(
        string codexHome,
        string targetProvider,
        Func<Task>? afterProviderUpdate = null,
        int? busyTimeoutMs = null,
        IReadOnlyCollection<string>? userEventThreadIds = null,
        IReadOnlyDictionary<string, string>? threadCwdsById = null)
    {
        var dbPath = StateDbPath(codexHome);
        if (!File.Exists(dbPath))
        {
            if (afterProviderUpdate is not null)
            {
                await afterProviderUpdate();
            }

            return (0, 0, 0, 0, false);
        }

        await using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadWriteCreate);
        var transactionOpen = false;
        try
        {
            await connection.OpenAsync();
            await SetBusyTimeoutAsync(connection, busyTimeoutMs);
            if (!await TableExistsAsync(connection, "threads"))
            {
                throw new InvalidOperationException("state_5.sqlite 不包含 threads 表。");
            }

            await ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE");
            transactionOpen = true;

            await using var providerCommand = connection.CreateCommand();
            providerCommand.CommandText = """
                UPDATE threads
                SET model_provider = $provider
                WHERE COALESCE(model_provider, '') <> $provider
                """;
            providerCommand.Parameters.AddWithValue("$provider", targetProvider);
            var providerRowsUpdated = await providerCommand.ExecuteNonQueryAsync();

            var userEventRowsUpdated = 0;
            if (userEventThreadIds is { Count: > 0 } && await TableHasColumnAsync(connection, "threads", "has_user_event"))
            {
                await using var userEventCommand = connection.CreateCommand();
                userEventCommand.CommandText = """
                    UPDATE threads
                    SET has_user_event = 1
                    WHERE id = $id AND COALESCE(has_user_event, 0) <> 1
                    """;
                var idParameter = userEventCommand.Parameters.Add("$id", SqliteType.Text);
                foreach (var threadId in userEventThreadIds)
                {
                    idParameter.Value = threadId;
                    userEventRowsUpdated += await userEventCommand.ExecuteNonQueryAsync();
                }
            }

            var cwdRowsUpdated = 0;
            if (threadCwdsById is { Count: > 0 } && await TableHasColumnAsync(connection, "threads", "cwd"))
            {
                await using var cwdCommand = connection.CreateCommand();
                cwdCommand.CommandText = """
                    UPDATE threads
                    SET cwd = $cwd
                    WHERE id = $id AND COALESCE(cwd, '') <> $cwd
                    """;
                var idParameter = cwdCommand.Parameters.Add("$id", SqliteType.Text);
                var cwdParameter = cwdCommand.Parameters.Add("$cwd", SqliteType.Text);
                foreach (var (threadId, cwd) in threadCwdsById)
                {
                    if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(cwd))
                    {
                        continue;
                    }

                    idParameter.Value = threadId;
                    cwdParameter.Value = cwd;
                    cwdRowsUpdated += await cwdCommand.ExecuteNonQueryAsync();
                }
            }

            if (afterProviderUpdate is not null)
            {
                await afterProviderUpdate();
            }

            await ExecuteNonQueryAsync(connection, "COMMIT");
            transactionOpen = false;
            return (
                providerRowsUpdated + userEventRowsUpdated + cwdRowsUpdated,
                providerRowsUpdated,
                userEventRowsUpdated,
                cwdRowsUpdated,
                true);
        }
        catch (Exception error)
        {
            if (transactionOpen)
            {
                try
                {
                    await ExecuteNonQueryAsync(connection, "ROLLBACK");
                }
                catch
                {
                    // Ignore rollback failures and surface the original error.
                }
            }

            throw WrapSqliteError(error, "更新 Codex 历史索引");
        }
    }

    private static SqliteConnection OpenConnection(string dbPath, SqliteOpenMode mode)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = dbPath,
            Mode = mode,
            Pooling = false
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private static async Task SetBusyTimeoutAsync(SqliteConnection connection, int? busyTimeoutMs)
    {
        var timeout = busyTimeoutMs is >= 0 ? busyTimeoutMs.Value : DefaultSqliteBusyTimeoutMs;
        await ExecuteNonQueryAsync(connection, $"PRAGMA busy_timeout = {timeout}");
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result ?? 0) > 0;
    }

    private static async Task<bool> TableHasColumnAsync(SqliteConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string QuoteIdentifier(string value)
        => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static Exception WrapSqliteError(Exception error, string action)
    {
        if (IsSqliteBusyError(error))
        {
            return new InvalidOperationException($"{action}失败：state_5.sqlite 正在被占用，请关闭 Codex / Codex Desktop 后重试。", error);
        }

        if (IsSqliteMalformedError(error))
        {
            return new InvalidOperationException($"{action}失败：state_5.sqlite 损坏或不可读。", error);
        }

        return error;
    }

    private static bool IsSqliteBusyError(Exception error)
    {
        if (error.InnerException is not null && IsSqliteBusyError(error.InnerException))
        {
            return true;
        }

        return error is SqliteException { SqliteErrorCode: 5 or 6 };
    }

    private static bool IsSqliteMalformedError(Exception error)
    {
        if (error.InnerException is not null && IsSqliteMalformedError(error.InnerException))
        {
            return true;
        }

        return error is SqliteException sqliteError &&
               (sqliteError.SqliteErrorCode == 11 ||
                sqliteError.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase) ||
                sqliteError.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase));
    }
}
