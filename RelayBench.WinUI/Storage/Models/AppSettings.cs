namespace RelayBench.WinUI.Storage;

/// <summary>
/// Immutable record representing all user-configurable application settings.
/// Serialized to/from settings.json.
/// </summary>
public sealed record AppSettings(
    int SchemaVersion,
    string Theme,
    string ProxyListenAddress,
    int ProxyListenPort,
    bool AutoStartProxy,
    bool RegisterSystemProxy,
    int MaxConcurrency,
    int RequestTimeoutSeconds,
    int CacheTtlSeconds,
    bool AutoBackup,
    int RetentionDays,
    bool NotificationsEnabled,
    // Phase 21: Navigation state persistence
    string LastVisitedPage = "Dashboard",
    // Phase 21: Speed test configuration
    string SpeedTestProfile = "quick",
    // Phase 21: Proxy route strategy
    string ProxyRouteStrategy = "smart",
    // Phase 21: Split routing host list
    string SplitRoutingHosts = "",
    // Phase 21: Network review selected problem types
    string NetworkReviewProblemTypes = "",
    // Phase 21: Capability matrix model lists
    string CapabilityEmbeddingsModel = "",
    string CapabilityImagesModel = "",
    string CapabilityAudioModel = "",
    string CapabilityModerationModel = "")
{
    /// <summary>
    /// Returns the default settings used when no configuration file exists
    /// or when the existing file is corrupt.
    /// </summary>
    public static AppSettings Defaults => new(
        SchemaVersion: 1,
        Theme: "System",
        ProxyListenAddress: "127.0.0.1",
        ProxyListenPort: 8080,
        AutoStartProxy: true,
        RegisterSystemProxy: true,
        MaxConcurrency: 32,
        RequestTimeoutSeconds: 30,
        CacheTtlSeconds: 600,
        AutoBackup: true,
        RetentionDays: 30,
        NotificationsEnabled: true);
}
