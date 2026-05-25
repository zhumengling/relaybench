namespace RelayBench.WinUI.Storage;

/// <summary>
/// Provides CRUD and reorder operations for proxy route definitions stored in SQLite.
/// </summary>
public interface IRouteRepository
{
    /// <summary>
    /// Returns all route definitions ordered by priority descending.
    /// API keys are decrypted (unprotected) before being returned.
    /// </summary>
    Task<IReadOnlyList<RouteDefinition>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a route definition. If <see cref="RouteDefinition.Id"/> is null or empty,
    /// a new GUID is generated. The API key is encrypted (protected) before storage.
    /// </summary>
    Task UpsertAsync(RouteDefinition route, CancellationToken ct = default);

    /// <summary>
    /// Updates the priority for every supplied (id, priority) pair inside a single transaction.
    /// </summary>
    Task ReorderAsync(IReadOnlyList<(string id, int priority)> ordering, CancellationToken ct = default);

    /// <summary>
    /// Deletes the route with the specified id.
    /// </summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}
