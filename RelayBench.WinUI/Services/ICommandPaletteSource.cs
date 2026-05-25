namespace RelayBench.WinUI.Services;

/// <summary>
/// Provides queryable items for the command palette.
/// Implementations return items matching the given search text.
/// </summary>
public interface ICommandPaletteSource
{
    /// <summary>
    /// Returns items matching <paramref name="text"/>. An empty or null text returns all items.
    /// </summary>
    IEnumerable<CommandPaletteItem> Query(string text);
}
