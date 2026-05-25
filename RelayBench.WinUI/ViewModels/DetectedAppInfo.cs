namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a detected application for transparent proxy capture configuration.
/// </summary>
public sealed class DetectedAppInfo
{
    public DetectedAppInfo(
        string id,
        string name,
        string path,
        bool isConfigured,
        string protocol,
        string status,
        bool isTakeoverEnabled = false,
        bool hasLiveBackup = false,
        string backupDisplay = "--")
    {
        Id = id;
        Name = name;
        Path = path;
        IsConfigured = isConfigured;
        Protocol = protocol;
        Status = status;
        IsTakeoverEnabled = isTakeoverEnabled;
        HasLiveBackup = hasLiveBackup;
        BackupDisplay = string.IsNullOrWhiteSpace(backupDisplay) ? "--" : backupDisplay;
    }

    public string Id { get; }
    public string Name { get; }
    public string Path { get; }
    public bool IsConfigured { get; }
    public string Protocol { get; }
    public string Status { get; }
    public bool IsTakeoverEnabled { get; }
    public bool HasLiveBackup { get; }
    public string BackupDisplay { get; }

    /// <summary>Badge text for XAML display.</summary>
    public string StatusBadge => IsTakeoverEnabled ? "接管" : IsConfigured ? "Ready" : "待接入";

    public string TakeoverDisplay => IsTakeoverEnabled ? "已接管" : "未接管";

    public string BackupStatusDisplay => HasLiveBackup ? BackupDisplay : "无备份";
}
