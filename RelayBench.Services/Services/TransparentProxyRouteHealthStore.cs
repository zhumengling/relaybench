using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using RelayBench.Services.Infrastructure;

namespace RelayBench.Services;

internal sealed class TransparentProxyRouteHealthStore
{
    private readonly object _syncRoot = new();
    private readonly string _databasePath;

    public TransparentProxyRouteHealthStore(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? RelayBenchPaths.TransparentProxyRouteHealthPath
            : databasePath;
    }

    public IReadOnlyDictionary<string, TransparentProxyRouteHealthSnapshot> Load(
        IEnumerable<string> routeIds)
    {
        HashSet<string> requestedIds = new(
            routeIds.Where(static id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        if (requestedIds.Count == 0 || string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return new Dictionary<string, TransparentProxyRouteHealthSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        lock (_syncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsureSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        route_id,
                        route_name,
                        sent,
                        success,
                        failed,
                        last_status_code,
                        last_latency_ms,
                        consecutive_failures,
                        consecutive_successes,
                        circuit_window_requests,
                        circuit_window_failures,
                        circuit_state,
                        circuit_open_until_utc,
                        last_seen_at_utc,
                        updated_at_utc
                    FROM transparent_proxy_route_health;
                    """;

                Dictionary<string, TransparentProxyRouteHealthSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var routeId = reader.GetString(0);
                    if (!requestedIds.Contains(routeId))
                    {
                        continue;
                    }

                    snapshots[routeId] = ReadSnapshot(reader);
                }

                return snapshots;
            }
            catch
            {
                return new Dictionary<string, TransparentProxyRouteHealthSnapshot>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void Save(TransparentProxyRouteRuntimeState state)
    {
        if (string.IsNullOrWhiteSpace(state.Id) || string.IsNullOrWhiteSpace(_databasePath))
        {
            return;
        }

        lock (_syncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsureSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO transparent_proxy_route_health (
                        route_id,
                        route_name,
                        sent,
                        success,
                        failed,
                        last_status_code,
                        last_latency_ms,
                        consecutive_failures,
                        consecutive_successes,
                        circuit_window_requests,
                        circuit_window_failures,
                        circuit_state,
                        circuit_open_until_utc,
                        last_seen_at_utc,
                        updated_at_utc
                    )
                    VALUES (
                        $route_id,
                        $route_name,
                        $sent,
                        $success,
                        $failed,
                        $last_status_code,
                        $last_latency_ms,
                        $consecutive_failures,
                        $consecutive_successes,
                        $circuit_window_requests,
                        $circuit_window_failures,
                        $circuit_state,
                        $circuit_open_until_utc,
                        $last_seen_at_utc,
                        $updated_at_utc
                    )
                    ON CONFLICT(route_id) DO UPDATE SET
                        route_name = excluded.route_name,
                        sent = excluded.sent,
                        success = excluded.success,
                        failed = excluded.failed,
                        last_status_code = excluded.last_status_code,
                        last_latency_ms = excluded.last_latency_ms,
                        consecutive_failures = excluded.consecutive_failures,
                        consecutive_successes = excluded.consecutive_successes,
                        circuit_window_requests = excluded.circuit_window_requests,
                        circuit_window_failures = excluded.circuit_window_failures,
                        circuit_state = excluded.circuit_state,
                        circuit_open_until_utc = excluded.circuit_open_until_utc,
                        last_seen_at_utc = excluded.last_seen_at_utc,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                BindSnapshot(command, state);
                command.ExecuteNonQuery();
            }
            catch
            {
            }
        }
    }

    public void SaveAll(IEnumerable<TransparentProxyRouteRuntimeState> states)
    {
        foreach (var state in states)
        {
            Save(state);
        }
    }

    public void Reset(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId) || string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return;
        }

        lock (_syncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsureSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM transparent_proxy_route_health WHERE route_id = $route_id;";
                command.Parameters.AddWithValue("$route_id", routeId);
                command.ExecuteNonQuery();
            }
            catch
            {
            }
        }
    }

    public void Clear()
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            return;
        }

        lock (_syncRoot)
        {
            try
            {
                using var connection = OpenConnection();
                EnsureSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM transparent_proxy_route_health;";
                command.ExecuteNonQuery();
            }
            catch
            {
            }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Cache = SqliteCacheMode.Shared
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS transparent_proxy_route_health (
                route_id TEXT PRIMARY KEY,
                route_name TEXT NOT NULL,
                sent INTEGER NOT NULL DEFAULT 0,
                success INTEGER NOT NULL DEFAULT 0,
                failed INTEGER NOT NULL DEFAULT 0,
                last_status_code INTEGER NOT NULL DEFAULT 0,
                last_latency_ms INTEGER NOT NULL DEFAULT 0,
                consecutive_failures INTEGER NOT NULL DEFAULT 0,
                consecutive_successes INTEGER NOT NULL DEFAULT 0,
                circuit_window_requests INTEGER NOT NULL DEFAULT 0,
                circuit_window_failures INTEGER NOT NULL DEFAULT 0,
                circuit_state TEXT NOT NULL DEFAULT 'Closed',
                circuit_open_until_utc TEXT NOT NULL DEFAULT '',
                last_seen_at_utc TEXT NOT NULL DEFAULT '',
                updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_transparent_proxy_route_health_updated
                ON transparent_proxy_route_health(updated_at_utc DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static void BindSnapshot(SqliteCommand command, TransparentProxyRouteRuntimeState state)
    {
        command.Parameters.AddWithValue("$route_id", state.Id);
        command.Parameters.AddWithValue("$route_name", state.Name);
        command.Parameters.AddWithValue("$sent", state.Sent);
        command.Parameters.AddWithValue("$success", state.Success);
        command.Parameters.AddWithValue("$failed", state.Failed);
        command.Parameters.AddWithValue("$last_status_code", state.LastStatusCode);
        command.Parameters.AddWithValue("$last_latency_ms", state.LastLatencyMs);
        command.Parameters.AddWithValue("$consecutive_failures", state.ConsecutiveFailures);
        command.Parameters.AddWithValue("$consecutive_successes", state.ConsecutiveSuccesses);
        command.Parameters.AddWithValue("$circuit_window_requests", state.CircuitWindowRequests);
        command.Parameters.AddWithValue("$circuit_window_failures", state.CircuitWindowFailures);
        command.Parameters.AddWithValue("$circuit_state", state.CircuitState.ToString());
        command.Parameters.AddWithValue("$circuit_open_until_utc", FormatDate(state.CircuitOpenUntil));
        command.Parameters.AddWithValue("$last_seen_at_utc", FormatDate(state.LastSeenAt));
        command.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    private static TransparentProxyRouteHealthSnapshot ReadSnapshot(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            Enum.TryParse<TransparentProxyCircuitState>(reader.GetString(11), ignoreCase: true, out var state)
                ? state
                : TransparentProxyCircuitState.Closed,
            ParseDate(reader.GetString(12)),
            ParseDate(reader.GetString(13)),
            ParseDate(reader.GetString(14)));

    private static string FormatDate(DateTimeOffset value)
        => value == DateTimeOffset.MinValue
            ? string.Empty
            : value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.MinValue;
}

internal sealed record TransparentProxyRouteHealthSnapshot(
    string RouteId,
    string RouteName,
    int Sent,
    int Success,
    int Failed,
    int LastStatusCode,
    long LastLatencyMs,
    int ConsecutiveFailures,
    int ConsecutiveSuccesses,
    int CircuitWindowRequests,
    int CircuitWindowFailures,
    TransparentProxyCircuitState CircuitState,
    DateTimeOffset CircuitOpenUntil,
    DateTimeOffset LastSeenAt,
    DateTimeOffset UpdatedAt);
