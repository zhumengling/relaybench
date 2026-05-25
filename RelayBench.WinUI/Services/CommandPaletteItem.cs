namespace RelayBench.WinUI.Services;

/// <summary>
/// Represents a single item returned by a command palette source.
/// </summary>
/// <param name="Title">Display text shown in the suggestion list.</param>
/// <param name="Group">Category label (e.g., "Pages", "Commands", "Routes", "Models").</param>
/// <param name="Invoke">Action to execute when the item is selected.</param>
public sealed record CommandPaletteItem(string Title, string Group, Action Invoke);
