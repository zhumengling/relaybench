using System.IO;
using System.Text;
using System.Text.Json;

namespace NetTest.App.Infrastructure;

public sealed class AppStateStore
{
    private const string LegacyDefaultProxyModel = "gpt-4o-mini";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly string _proxyRelayConfigPath;
    private readonly string _dataDirectory;
    private readonly string _configDirectory;

    public AppStateStore()
    {
        _dataDirectory = NetTestPaths.DataDirectory;
        Directory.CreateDirectory(_dataDirectory);
        _filePath = NetTestPaths.AppStatePath;

        _configDirectory = NetTestPaths.ConfigDirectory;
        Directory.CreateDirectory(_configDirectory);
        _proxyRelayConfigPath = NetTestPaths.ProxyRelayConfigPath;
    }

    public AppStateSnapshot Load()
    {
        AppStateSnapshot snapshot;

        try
        {
            if (!File.Exists(_filePath))
            {
                snapshot = new AppStateSnapshot();
            }
            else
            {
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                snapshot = JsonSerializer.Deserialize<AppStateSnapshot>(json, SerializerOptions) ?? new AppStateSnapshot();
                ApplyLegacyPortScanState(snapshot, json);
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("AppStateStore.Load", ex);
            snapshot = new AppStateSnapshot();
        }

        ApplyProxyRelayDirectoryConfig(snapshot);
        NormalizeProxyModelSelection(snapshot);
        return snapshot;
    }

    public void Save(AppStateSnapshot snapshot)
    {
        EnsureDirectories();
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        File.WriteAllText(_filePath, json, Encoding.UTF8);
        SaveProxyRelayDirectoryConfig(snapshot);
    }

    private void ApplyProxyRelayDirectoryConfig(AppStateSnapshot snapshot)
    {
        try
        {
            if (!File.Exists(_proxyRelayConfigPath))
            {
                return;
            }

            var json = File.ReadAllText(_proxyRelayConfigPath, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<ProxyRelayDirectoryConfig>(json, SerializerOptions);
            if (config is null)
            {
                return;
            }

            snapshot.ProxyBaseUrl = string.IsNullOrWhiteSpace(config.ProxyBaseUrl) ? snapshot.ProxyBaseUrl : config.ProxyBaseUrl;
            snapshot.ProxyApiKey = string.IsNullOrWhiteSpace(config.ProxyApiKey) ? snapshot.ProxyApiKey : config.ProxyApiKey;
            snapshot.ProxyModel = config.ProxyModel ?? snapshot.ProxyModel;
            snapshot.ProxyModelWasExplicitlySet = config.ProxyModelWasExplicitlySet || snapshot.ProxyModelWasExplicitlySet;
            snapshot.ProxyDiagnosticPresetKey = string.IsNullOrWhiteSpace(config.ProxyDiagnosticPresetKey)
                ? snapshot.ProxyDiagnosticPresetKey
                : config.ProxyDiagnosticPresetKey;
            snapshot.ProxyEnableLongStreamingTest = config.ProxyEnableLongStreamingTest;
            snapshot.ProxyEnableProtocolCompatibilityTest = config.ProxyEnableProtocolCompatibilityTest;
            snapshot.ProxyEnableErrorTransparencyTest = config.ProxyEnableErrorTransparencyTest;
            snapshot.ProxyEnableStreamingIntegrityTest = config.ProxyEnableStreamingIntegrityTest;
            snapshot.ProxyEnableMultiModalTest = config.ProxyEnableMultiModalTest;
            snapshot.ProxyEnableCacheMechanismTest = config.ProxyEnableCacheMechanismTest;
            snapshot.ProxyEnableCacheIsolationTest = config.ProxyEnableCacheIsolationTest;
            snapshot.ProxyCacheIsolationAlternateApiKey = string.IsNullOrWhiteSpace(config.ProxyCacheIsolationAlternateApiKey)
                ? snapshot.ProxyCacheIsolationAlternateApiKey
                : config.ProxyCacheIsolationAlternateApiKey;
            snapshot.ProxyEnableOfficialReferenceIntegrityTest = config.ProxyEnableOfficialReferenceIntegrityTest;
            snapshot.ProxyOfficialReferenceBaseUrl = string.IsNullOrWhiteSpace(config.ProxyOfficialReferenceBaseUrl)
                ? snapshot.ProxyOfficialReferenceBaseUrl
                : config.ProxyOfficialReferenceBaseUrl;
            snapshot.ProxyOfficialReferenceApiKey = string.IsNullOrWhiteSpace(config.ProxyOfficialReferenceApiKey)
                ? snapshot.ProxyOfficialReferenceApiKey
                : config.ProxyOfficialReferenceApiKey;
            snapshot.ProxyOfficialReferenceModel = string.IsNullOrWhiteSpace(config.ProxyOfficialReferenceModel)
                ? snapshot.ProxyOfficialReferenceModel
                : config.ProxyOfficialReferenceModel;
            snapshot.ProxyEmbeddingsModel = string.IsNullOrWhiteSpace(config.ProxyEmbeddingsModel)
                ? snapshot.ProxyEmbeddingsModel
                : config.ProxyEmbeddingsModel;
            snapshot.ProxyImagesModel = string.IsNullOrWhiteSpace(config.ProxyImagesModel)
                ? snapshot.ProxyImagesModel
                : config.ProxyImagesModel;
            snapshot.ProxyAudioTranscriptionModel = string.IsNullOrWhiteSpace(config.ProxyAudioTranscriptionModel)
                ? snapshot.ProxyAudioTranscriptionModel
                : config.ProxyAudioTranscriptionModel;
            snapshot.ProxyAudioSpeechModel = string.IsNullOrWhiteSpace(config.ProxyAudioSpeechModel)
                ? snapshot.ProxyAudioSpeechModel
                : config.ProxyAudioSpeechModel;
            snapshot.ProxyModerationModel = string.IsNullOrWhiteSpace(config.ProxyModerationModel)
                ? snapshot.ProxyModerationModel
                : config.ProxyModerationModel;
            snapshot.ProxyBatchEnableLongStreamingTest = config.ProxyBatchEnableLongStreamingTest;
            snapshot.ProxyLongStreamSegmentsText = string.IsNullOrWhiteSpace(config.ProxyLongStreamSegmentsText)
                ? snapshot.ProxyLongStreamSegmentsText
                : config.ProxyLongStreamSegmentsText;
            snapshot.ProxyBatchTargetsText = string.IsNullOrWhiteSpace(config.ProxyBatchTargetsText)
                ? snapshot.ProxyBatchTargetsText
                : config.ProxyBatchTargetsText;
            snapshot.ProxyBatchItems = config.ProxyBatchItems?
                .Select(CreateProxyBatchConfigItemSnapshot)
                .ToList() ?? snapshot.ProxyBatchItems;
            snapshot.ProxyBatchDraft = config.ProxyBatchDraft is null
                ? snapshot.ProxyBatchDraft
                : CreateProxyBatchDraftSnapshot(config.ProxyBatchDraft);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("AppStateStore.ApplyProxyRelayDirectoryConfig", ex);
        }
    }

    private void SaveProxyRelayDirectoryConfig(AppStateSnapshot snapshot)
    {
        try
        {
            EnsureDirectories();
            ProxyRelayDirectoryConfig config = new()
            {
                ProxyBaseUrl = snapshot.ProxyBaseUrl,
                ProxyApiKey = snapshot.ProxyApiKey,
                ProxyModel = snapshot.ProxyModel,
                ProxyModelWasExplicitlySet = !string.IsNullOrWhiteSpace(snapshot.ProxyModel),
                ProxyDiagnosticPresetKey = snapshot.ProxyDiagnosticPresetKey,
                ProxyEnableLongStreamingTest = snapshot.ProxyEnableLongStreamingTest,
                ProxyEnableProtocolCompatibilityTest = snapshot.ProxyEnableProtocolCompatibilityTest,
                ProxyEnableErrorTransparencyTest = snapshot.ProxyEnableErrorTransparencyTest,
                ProxyEnableStreamingIntegrityTest = snapshot.ProxyEnableStreamingIntegrityTest,
                ProxyEnableMultiModalTest = snapshot.ProxyEnableMultiModalTest,
                ProxyEnableCacheMechanismTest = snapshot.ProxyEnableCacheMechanismTest,
                ProxyEnableCacheIsolationTest = snapshot.ProxyEnableCacheIsolationTest,
                ProxyCacheIsolationAlternateApiKey = snapshot.ProxyCacheIsolationAlternateApiKey,
                ProxyEnableOfficialReferenceIntegrityTest = snapshot.ProxyEnableOfficialReferenceIntegrityTest,
                ProxyOfficialReferenceBaseUrl = snapshot.ProxyOfficialReferenceBaseUrl,
                ProxyOfficialReferenceApiKey = snapshot.ProxyOfficialReferenceApiKey,
                ProxyOfficialReferenceModel = snapshot.ProxyOfficialReferenceModel,
                ProxyEmbeddingsModel = snapshot.ProxyEmbeddingsModel,
                ProxyImagesModel = snapshot.ProxyImagesModel,
                ProxyAudioTranscriptionModel = snapshot.ProxyAudioTranscriptionModel,
                ProxyAudioSpeechModel = snapshot.ProxyAudioSpeechModel,
                ProxyModerationModel = snapshot.ProxyModerationModel,
                ProxyBatchEnableLongStreamingTest = snapshot.ProxyBatchEnableLongStreamingTest,
                ProxyLongStreamSegmentsText = snapshot.ProxyLongStreamSegmentsText,
                ProxyBatchTargetsText = snapshot.ProxyBatchTargetsText,
                ProxyBatchItems = snapshot.ProxyBatchItems
                    .Select(CreateProxyBatchConfigItemSnapshot)
                    .ToList(),
                ProxyBatchDraft = CreateProxyBatchDraftSnapshot(snapshot.ProxyBatchDraft)
            };

            var json = JsonSerializer.Serialize(config, SerializerOptions);
            File.WriteAllText(_proxyRelayConfigPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("AppStateStore.SaveProxyRelayDirectoryConfig", ex);
        }
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_configDirectory);
    }

    private static void NormalizeProxyModelSelection(AppStateSnapshot snapshot)
    {
        if (snapshot.ProxyModelWasExplicitlySet)
        {
            return;
        }

        if (string.Equals(snapshot.ProxyModel, LegacyDefaultProxyModel, StringComparison.OrdinalIgnoreCase))
        {
            snapshot.ProxyModel = string.Empty;
        }
    }

    private static void ApplyLegacyPortScanState(AppStateSnapshot snapshot, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (string.IsNullOrWhiteSpace(snapshot.PortScanTarget) &&
                root.TryGetProperty("NmapTarget", out var legacyTarget) &&
                legacyTarget.ValueKind == JsonValueKind.String)
            {
                snapshot.PortScanTarget = legacyTarget.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(snapshot.PortScanProfileKey) &&
                root.TryGetProperty("NmapProfileKey", out var legacyProfileKey) &&
                legacyProfileKey.ValueKind == JsonValueKind.String)
            {
                snapshot.PortScanProfileKey = legacyProfileKey.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(snapshot.PortScanCustomPortsText) &&
                root.TryGetProperty("NmapCustomPortsText", out var legacyCustomPorts) &&
                legacyCustomPorts.ValueKind == JsonValueKind.String)
            {
                snapshot.PortScanCustomPortsText = legacyCustomPorts.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("AppStateStore.ApplyLegacyPortScanState", ex);
        }
    }

    private sealed class ProxyRelayDirectoryConfig
    {
        public string? ProxyBaseUrl { get; set; }

        public string? ProxyApiKey { get; set; }

        public string? ProxyModel { get; set; }

        public bool ProxyModelWasExplicitlySet { get; set; }

        public string? ProxyDiagnosticPresetKey { get; set; }

        public bool ProxyEnableLongStreamingTest { get; set; }

        public bool ProxyEnableProtocolCompatibilityTest { get; set; }

        public bool ProxyEnableErrorTransparencyTest { get; set; }

        public bool ProxyEnableStreamingIntegrityTest { get; set; }

        public bool ProxyEnableMultiModalTest { get; set; }

        public bool ProxyEnableCacheMechanismTest { get; set; }

        public bool ProxyEnableCacheIsolationTest { get; set; }

        public string? ProxyCacheIsolationAlternateApiKey { get; set; }

        public bool ProxyEnableOfficialReferenceIntegrityTest { get; set; }

        public string? ProxyOfficialReferenceBaseUrl { get; set; }

        public string? ProxyOfficialReferenceApiKey { get; set; }

        public string? ProxyOfficialReferenceModel { get; set; }

        public string? ProxyEmbeddingsModel { get; set; }

        public string? ProxyImagesModel { get; set; }

        public string? ProxyAudioTranscriptionModel { get; set; }

        public string? ProxyAudioSpeechModel { get; set; }

        public string? ProxyModerationModel { get; set; }

        public bool ProxyBatchEnableLongStreamingTest { get; set; }

        public string? ProxyLongStreamSegmentsText { get; set; }

        public string? ProxyBatchTargetsText { get; set; }

        public List<ProxyBatchConfigItemSnapshot>? ProxyBatchItems { get; set; }

        public ProxyBatchDraftSnapshot? ProxyBatchDraft { get; set; }
    }

    private static ProxyBatchConfigItemSnapshot CreateProxyBatchConfigItemSnapshot(ProxyBatchConfigItemSnapshot? source)
        => new()
        {
            EntryName = source?.EntryName ?? string.Empty,
            BaseUrl = source?.BaseUrl ?? string.Empty,
            EntryApiKey = source?.EntryApiKey ?? string.Empty,
            EntryModel = source?.EntryModel ?? string.Empty,
            SiteGroupName = source?.SiteGroupName ?? string.Empty,
            SiteGroupApiKey = source?.SiteGroupApiKey ?? string.Empty,
            SiteGroupModel = source?.SiteGroupModel ?? string.Empty,
            IncludeInBatchTest = source?.IncludeInBatchTest ?? true
        };

    private static ProxyBatchDraftSnapshot CreateProxyBatchDraftSnapshot(ProxyBatchDraftSnapshot? source)
        => new()
        {
            EditorModeIndex = Math.Clamp(source?.EditorModeIndex ?? 0, 0, 1),
            SiteGroupName = source?.SiteGroupName ?? string.Empty,
            SiteGroupApiKey = source?.SiteGroupApiKey ?? string.Empty,
            SiteGroupModel = source?.SiteGroupModel ?? string.Empty,
            EntryName = source?.EntryName ?? string.Empty,
            BaseUrl = source?.BaseUrl ?? string.Empty,
            EntryApiKey = source?.EntryApiKey ?? string.Empty,
            EntryModel = source?.EntryModel ?? string.Empty
        };
}
