namespace RelayBench.WinUI.Services;

/// <summary>
/// Aggregates results from multiple <see cref="ICommandPaletteSource"/> instances,
/// ranks them using substring + starts-with weighting, and returns results suitable
/// for display in the command palette AutoSuggestBox.
/// </summary>
public sealed class CommandPaletteAggregator
{
    private readonly IReadOnlyList<ICommandPaletteSource> _sources;

    /// <summary>Score assigned to items whose title starts with the query text.</summary>
    private const int StartsWithScore = 100;

    /// <summary>Score assigned to items whose title contains (but does not start with) the query text.</summary>
    private const int ContainsScore = 50;

    /// <summary>
    /// Creates a new aggregator with the given sources.
    /// </summary>
    /// <param name="sources">The palette sources to query.</param>
    public CommandPaletteAggregator(IReadOnlyList<ICommandPaletteSource> sources)
    {
        _sources = sources;
    }

    /// <summary>
    /// Queries all registered sources, ranks results, and returns them ordered by score descending.
    /// Empty or null <paramref name="text"/> returns all items from all sources.
    /// </summary>
    /// <param name="text">The search text entered by the user.</param>
    /// <returns>Ranked list of palette items.</returns>
    public IReadOnlyList<CommandPaletteItem> Query(string? text)
    {
        var query = text?.Trim() ?? string.Empty;
        var scored = new List<(CommandPaletteItem Item, int Score)>();

        foreach (var source in _sources)
        {
            foreach (var item in source.Query(query))
            {
                var score = ComputeScore(item.Title, query);
                scored.Add((item, score));
            }
        }

        scored.Sort((a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0) return cmp;
            return string.Compare(a.Item.Title, b.Item.Title, StringComparison.OrdinalIgnoreCase);
        });

        var results = new List<CommandPaletteItem>(scored.Count);
        foreach (var (item, _) in scored)
        {
            results.Add(item);
        }

        return results;
    }

    /// <summary>
    /// Computes a relevance score for an item title against the query.
    /// </summary>
    private static int ComputeScore(string title, string query)
    {
        if (string.IsNullOrEmpty(query))
            return ContainsScore; // All items get a neutral score when no query

        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return StartsWithScore;

        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return ContainsScore;

        return 0;
    }
}
