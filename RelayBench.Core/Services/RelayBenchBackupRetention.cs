namespace RelayBench.Core.Services;

internal static class RelayBenchBackupRetention
{
    private const string BackupMarker = ".relaybench-backup-";
    private const int DefaultKeepCount = 3;

    public static void PruneForOriginalFile(
        IClientApiConfigMutationEnvironment environment,
        string originalFilePath,
        int keepCount = DefaultKeepCount)
    {
        if (string.IsNullOrWhiteSpace(originalFilePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(originalFilePath);
        var fileName = Path.GetFileName(originalFilePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var backups = environment
            .EnumerateFiles(directory, $"{fileName}{BackupMarker}*")
            .OrderByDescending(ExtractBackupSortKey, StringComparer.Ordinal)
            .Skip(Math.Max(0, keepCount))
            .ToArray();

        foreach (var backup in backups)
        {
            environment.DeleteFile(backup);
        }
    }

    public static void PruneAllUnderDirectory(
        IClientApiConfigMutationEnvironment environment,
        string directoryPath,
        int keepCount = DefaultKeepCount)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) ||
            !environment.DirectoryExists(directoryPath))
        {
            return;
        }

        var backups = environment
            .EnumerateFilesRecursive(directoryPath, $"*{BackupMarker}*")
            .GroupBy(ResolveOriginalPath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in backups)
        {
            foreach (var staleBackup in group
                         .OrderByDescending(ExtractBackupSortKey, StringComparer.Ordinal)
                         .Skip(Math.Max(0, keepCount)))
            {
                environment.DeleteFile(staleBackup);
            }
        }
    }

    private static string ExtractBackupSortKey(string path)
    {
        var fileName = Path.GetFileName(path);
        var markerIndex = fileName.LastIndexOf(BackupMarker, StringComparison.OrdinalIgnoreCase);
        return markerIndex >= 0
            ? fileName[(markerIndex + BackupMarker.Length)..]
            : fileName;
    }

    private static string ResolveOriginalPath(string backupPath)
    {
        var markerIndex = backupPath.LastIndexOf(BackupMarker, StringComparison.OrdinalIgnoreCase);
        return markerIndex >= 0
            ? backupPath[..markerIndex]
            : backupPath;
    }
}
