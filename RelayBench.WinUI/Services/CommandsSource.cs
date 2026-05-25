namespace RelayBench.WinUI.Services;

/// <summary>
/// Command palette source that provides application commands (e.g., Toggle Proxy, Open Settings).
/// </summary>
public sealed class CommandsSource : ICommandPaletteSource
{
    private readonly IReadOnlyList<CommandPaletteItem> _commands;

    /// <summary>
    /// Creates a new <see cref="CommandsSource"/> with the given command definitions.
    /// </summary>
    /// <param name="commands">The list of available commands.</param>
    public CommandsSource(IReadOnlyList<CommandPaletteItem> commands)
    {
        _commands = commands;
    }

    public IEnumerable<CommandPaletteItem> Query(string text)
    {
        foreach (var command in _commands)
        {
            if (string.IsNullOrEmpty(text) ||
                command.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                yield return command;
            }
        }
    }
}
