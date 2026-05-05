using System.IO;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyCaptureArtifactStore
{
    private const string BackupMarker = ".relaybench-app-capture-backup-";

    private readonly string _userProfilePath;
    private readonly string _roamingAppDataPath;

    public TransparentProxyCaptureArtifactStore()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
    {
    }

    public TransparentProxyCaptureArtifactStore(string userProfilePath, string roamingAppDataPath)
    {
        _userProfilePath = string.IsNullOrWhiteSpace(userProfilePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(userProfilePath);
        _roamingAppDataPath = string.IsNullOrWhiteSpace(roamingAppDataPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.GetFullPath(roamingAppDataPath);
    }

    public IReadOnlyList<TransparentProxyCaptureArtifactSnapshot> ScanDefaultArtifacts()
        =>
        [
            Scan("codex-cli", "Codex CLI", ResolveCodexConfigPath()),
            Scan("claude-cli", "Claude CLI", ResolveClaudeSettingsPath()),
            .. ResolveVsCodeSettingsPaths().Select(path => Scan("vs-codex", "VS Codex / VS Code", path))
        ];

    private TransparentProxyCaptureArtifactSnapshot Scan(string targetId, string displayName, string path)
    {
        var backups = FindBackups(path);
        var latest = backups.FirstOrDefault();
        var latestBackupAt = latest is null
            ? (DateTimeOffset?)null
            : File.GetLastWriteTime(latest);
        return new TransparentProxyCaptureArtifactSnapshot(
            targetId,
            displayName,
            path,
            File.Exists(path),
            backups.Count,
            latest,
            latestBackupAt,
            backups.Count > 0 ? "ready" : "missing");
    }

    private string ResolveCodexConfigPath()
        => Path.Combine(_userProfilePath, ".codex", "config.toml");

    private string ResolveClaudeSettingsPath()
        => Path.Combine(_userProfilePath, ".claude", "settings.json");

    private IReadOnlyList<string> ResolveVsCodeSettingsPaths()
    {
        var stable = Path.Combine(_roamingAppDataPath, "Code", "User", "settings.json");
        var insiders = Path.Combine(_roamingAppDataPath, "Code - Insiders", "User", "settings.json");
        var insidersDirectory = Path.GetDirectoryName(insiders);
        return File.Exists(insiders) ||
               (!string.IsNullOrWhiteSpace(insidersDirectory) && Directory.Exists(insidersDirectory))
            ? [stable, insiders]
            : [stable];
    }

    private static IReadOnlyList<string> FindBackups(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(fileName) ||
            !Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .GetFiles(directory, $"{fileName}{BackupMarker}*")
            .OrderByDescending(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed record TransparentProxyCaptureArtifactSnapshot(
    string TargetId,
    string DisplayName,
    string Path,
    bool TargetExists,
    int BackupCount,
    string? LatestBackupPath,
    DateTimeOffset? LatestBackupAt,
    string Status);
