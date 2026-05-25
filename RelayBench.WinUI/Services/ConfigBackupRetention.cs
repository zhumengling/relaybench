namespace RelayBench.WinUI.Services;

/// <summary>
/// Trims timestamped backup files for a given config path, keeping only the newest N.
/// Backup files are expected to follow the pattern: {originalFileName}.bak-{timestamp}
/// where timestamp sorts lexicographically (e.g., yyyyMMdd-HHmmss).
/// </summary>
public static class ConfigBackupRetention
{
    private const string BackupPrefix = ".bak-";

    /// <summary>
    /// Scans the directory containing <paramref name="targetFilePath"/> for backup siblings
    /// matching {fileName}.bak-* and deletes all but the newest <paramref name="maxBackups"/>.
    /// </summary>
    /// <param name="targetFilePath">The original config file path whose backups should be pruned.</param>
    /// <param name="maxBackups">Maximum number of backup files to retain (default 8).</param>
    public static void EnsureRetained(string targetFilePath, int maxBackups = 8)
    {
        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(targetFilePath);
        var fileName = Path.GetFileName(targetFilePath);
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(fileName) ||
            !Directory.Exists(directory))
        {
            return;
        }

        var searchPattern = $"{fileName}{BackupPrefix}*";
        var backupsToDelete = Directory
            .GetFiles(directory, searchPattern)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .Skip(Math.Max(0, maxBackups));

        foreach (var backup in backupsToDelete)
        {
            try
            {
                File.Delete(backup);
            }
            catch
            {
                // Best-effort deletion — file may be locked or permission denied.
            }
        }
    }
}
