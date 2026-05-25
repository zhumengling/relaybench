namespace RelayBench.WinUI.Storage;

/// <summary>
/// Provides CRUD operations for routing strategies stored in SQLite.
/// </summary>
public interface IStrategyRepository
{
    /// <summary>
    /// Retrieves all strategies ordered by priority descending, then name ascending.
    /// </summary>
    Task<IReadOnlyList<Strategy>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves a strategy. If <see cref="Strategy.Id"/> is 0, inserts a new row;
    /// otherwise updates the existing row. Throws <see cref="InvalidOperationException"/>
    /// if inserting a strategy whose name already exists.
    /// </summary>
    Task SaveAsync(Strategy strategy, CancellationToken ct = default);

    /// <summary>
    /// Deletes the strategy with the specified id.
    /// </summary>
    Task DeleteAsync(long id, CancellationToken ct = default);
}
