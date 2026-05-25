using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RelayBench.WinUI.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IStrategyRepository"/>.
/// </summary>
public sealed class StrategyRepository : IStrategyRepository
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<Strategy>> GetAllAsync(CancellationToken ct = default)
    {
        using var connection = HistoryDatabase.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, priority, model_pattern, endpoint_pattern, target_routes_json, updated_at
            FROM strategies
            ORDER BY priority DESC, name ASC
            """;

        using var reader = cmd.ExecuteReader();
        var results = new List<Strategy>();

        while (reader.Read())
        {
            results.Add(ReadStrategy(reader));
        }

        return Task.FromResult<IReadOnlyList<Strategy>>(results);
    }

    /// <inheritdoc/>
    public Task SaveAsync(Strategy strategy, CancellationToken ct = default)
    {
        using var connection = HistoryDatabase.CreateConnection();

        if (strategy.Id == 0)
        {
            // Insert — check for duplicate name first
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM strategies WHERE name = @name";
            checkCmd.Parameters.AddWithValue("@name", strategy.Name);

            var count = Convert.ToInt64(checkCmd.ExecuteScalar());
            if (count > 0)
            {
                throw new InvalidOperationException(
                    $"A strategy with the name '{strategy.Name}' already exists.");
            }

            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO strategies (name, priority, model_pattern, endpoint_pattern, target_routes_json, updated_at)
                VALUES (@name, @priority, @modelPattern, @endpointPattern, @targetRoutesJson, @updatedAt)
                """;

            insertCmd.Parameters.AddWithValue("@name", strategy.Name);
            insertCmd.Parameters.AddWithValue("@priority", strategy.Priority);
            insertCmd.Parameters.AddWithValue("@modelPattern", (object?)strategy.ModelPattern ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@endpointPattern", (object?)strategy.EndpointPattern ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@targetRoutesJson", JsonSerializer.Serialize(strategy.TargetRouteIds));
            insertCmd.Parameters.AddWithValue("@updatedAt", strategy.UpdatedAtUtc.ToString("O"));

            insertCmd.ExecuteNonQuery();
        }
        else
        {
            // Update existing row
            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE strategies
                SET name = @name,
                    priority = @priority,
                    model_pattern = @modelPattern,
                    endpoint_pattern = @endpointPattern,
                    target_routes_json = @targetRoutesJson,
                    updated_at = @updatedAt
                WHERE id = @id
                """;

            updateCmd.Parameters.AddWithValue("@id", strategy.Id);
            updateCmd.Parameters.AddWithValue("@name", strategy.Name);
            updateCmd.Parameters.AddWithValue("@priority", strategy.Priority);
            updateCmd.Parameters.AddWithValue("@modelPattern", (object?)strategy.ModelPattern ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("@endpointPattern", (object?)strategy.EndpointPattern ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("@targetRoutesJson", JsonSerializer.Serialize(strategy.TargetRouteIds));
            updateCmd.Parameters.AddWithValue("@updatedAt", strategy.UpdatedAtUtc.ToString("O"));

            updateCmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var connection = HistoryDatabase.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM strategies WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    private static Strategy ReadStrategy(SqliteDataReader reader)
    {
        var targetRoutesJson = reader.GetString(5);
        var targetRouteIds = JsonSerializer.Deserialize<List<string>>(targetRoutesJson) ?? [];

        return new Strategy(
            Id: reader.GetInt64(0),
            Name: reader.GetString(1),
            Priority: reader.GetInt32(2),
            ModelPattern: reader.IsDBNull(3) ? null : reader.GetString(3),
            EndpointPattern: reader.IsDBNull(4) ? null : reader.GetString(4),
            TargetRouteIds: targetRouteIds,
            UpdatedAtUtc: DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
