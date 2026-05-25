namespace RelayBench.WinUI.Storage;

/// <summary>
/// Provides well-known file paths for the persistence layer.
/// The root directory is created lazily on first access.
/// </summary>
public static class StoragePaths
{
    private static readonly Lazy<string> _root = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data");

        Directory.CreateDirectory(path);
        return path;
    });

    /// <summary>
    /// {AppContext.BaseDirectory}\data\
    /// The directory is created on first access if it does not already exist.
    /// </summary>
    public static string Root => _root.Value;

    /// <summary>
    /// Full path to the SQLite history database file.
    /// </summary>
    public static string HistoryDbPath => Path.Combine(Root, "history.db");

    /// <summary>
    /// Full path to the JSON settings file.
    /// </summary>
    public static string SettingsJsonPath => Path.Combine(Root, "settings.json");
}
