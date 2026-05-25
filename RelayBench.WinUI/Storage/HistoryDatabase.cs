using Microsoft.Data.Sqlite;

namespace RelayBench.WinUI.Storage;

/// <summary>
/// Manages the SQLite database lifecycle for history reports, strategies, and routes.
/// Schema is initialized once on first connection creation.
/// </summary>
public static class HistoryDatabase
{
    private static readonly object _initLock = new();
    private static bool _schemaInitialized;

    private const string SchemaScript = """
        CREATE TABLE IF NOT EXISTS history_reports (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id          TEXT    NOT NULL UNIQUE,
            created_at      TEXT    NOT NULL,
            test_type       TEXT    NOT NULL,
            endpoint        TEXT    NOT NULL,
            summary         TEXT    NOT NULL,
            score           REAL,
            duration_ms     INTEGER,
            payload_json    TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_history_created ON history_reports(created_at DESC);
        CREATE INDEX IF NOT EXISTS idx_history_type    ON history_reports(test_type);

        CREATE TABLE IF NOT EXISTS strategies (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            name            TEXT    NOT NULL UNIQUE,
            priority        INTEGER NOT NULL DEFAULT 0,
            model_pattern   TEXT,
            endpoint_pattern TEXT,
            target_routes_json TEXT NOT NULL,
            updated_at      TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_strategies_priority ON strategies(priority DESC);

        CREATE TABLE IF NOT EXISTS routes (
            id              TEXT    PRIMARY KEY,
            name            TEXT    NOT NULL,
            upstream_url    TEXT    NOT NULL,
            api_key         TEXT,
            priority        INTEGER NOT NULL DEFAULT 0,
            model_filter    TEXT,
            enabled         INTEGER NOT NULL DEFAULT 1,
            updated_at      TEXT    NOT NULL,
            prefix          TEXT,
            outbound_proxy  TEXT,
            request_retry   INTEGER,
            max_retry_interval_seconds INTEGER,
            model_cooldown_seconds     INTEGER,
            excluded_model_patterns    TEXT,
            payload_rules_text         TEXT,
            preferred_wire_api         TEXT,
            headers_text               TEXT,
            auth_mode                  TEXT,
            oauth_provider             TEXT,
            oauth_credential_id        TEXT,
            codex_backend_base_url     TEXT,
            codex_oauth_fast_mode      INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_routes_priority ON routes(priority DESC);
        """;

    /// <summary>
    /// Creates and opens a new <see cref="SqliteConnection"/> to the history database.
    /// On the first call, the schema is applied (tables and indexes are created if they
    /// do not already exist). Subsequent calls skip schema initialization.
    /// </summary>
    /// <returns>An open <see cref="SqliteConnection"/> ready for use.</returns>
    public static SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = StoragePaths.HistoryDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        EnsureSchema(connection);

        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        if (_schemaInitialized)
            return;

        lock (_initLock)
        {
            if (_schemaInitialized)
                return;

            using var command = connection.CreateCommand();
            command.CommandText = SchemaScript;
            command.ExecuteNonQuery();

            _schemaInitialized = true;
        }
    }

    /// <summary>
    /// Resets the schema-initialized flag. Intended for testing scenarios only.
    /// </summary>
    internal static void ResetInitialization()
    {
        lock (_initLock)
        {
            _schemaInitialized = false;
        }
    }
}
