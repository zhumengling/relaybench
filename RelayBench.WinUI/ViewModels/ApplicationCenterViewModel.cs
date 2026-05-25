using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class ApplicationCenterViewModel : ObservableObject
{
    private const string ToneAccent = ApplicationAccessTones.Accent;
    private const string ToneHealthy = ApplicationAccessTones.Healthy;
    private const string ToneWarning = ApplicationAccessTones.Warning;
    private const string ToneDanger = ApplicationAccessTones.Danger;

    private readonly ProxyDiagnosticsService _diagnosticsService;
    private readonly ProxyEndpointModelCacheService _modelCacheService;
    private readonly ProxyEndpointProtocolProbeService _probeService;
    private readonly ClientAppApplyPlanner _applyPlanner = new();
    private readonly ClientAppConfigApplyService _clientAppConfigApplyService;
    private readonly ClientApiConfigRestoreService _restoreService = new();
    private readonly ClientChatHistoryArchiveService _chatHistoryArchiveService = new();
    private readonly EndpointHistoryStore _historyStore = new();
    private readonly TransparentProxyAppDetectorService _appDetector = new();
    private readonly TransparentProxyVsCodeSettingsService _vsCodeSettingsService = new();
    private readonly CodexHistorySyncService _codexHistorySyncService;
    private readonly CodexChatMergeService _codexChatMergeService;
    private readonly Func<SharedEndpointState?> _sharedEndpointLoader;
    private readonly Func<CancellationToken, Task<IReadOnlyList<EndpointHistoryItem>>> _endpointHistoryLoader;
    private Func<ProxyEndpointSettings, ProxyEndpointProtocolProbeResult, string, Task<ClientApplyEndpoint?>>? _claudeRelayEndpointResolver;

    private ProxyEndpointProtocolProbeResult? _lastProbeResult;
    private CodexConfigTemplate? _customCodexTemplate;

    [ObservableProperty] public partial string BaseUrl { get; set; } = "http://127.0.0.1:8080";
    [ObservableProperty] public partial string ApiKey { get; set; } = "";
    [ObservableProperty] public partial string Model { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "就绪";
    [ObservableProperty] public partial bool IsProbing { get; set; }
    [ObservableProperty] public partial bool IsFetchingModels { get; set; }
    [ObservableProperty] public partial string ProbeResult { get; set; } = "尚未复核；写入前会重新检查协议";
    [ObservableProperty] public partial bool ResponsesSupported { get; set; }
    [ObservableProperty] public partial bool AnthropicSupported { get; set; }
    [ObservableProperty] public partial bool ChatSupported { get; set; }
    [ObservableProperty] public partial string PreferredProtocol { get; set; } = "--";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "";
    [ObservableProperty] public partial bool IsApplying { get; set; }
    [ObservableProperty] public partial string LastWriteTime { get; set; } = "--";
    [ObservableProperty] public partial string SelectedTargetName { get; set; } = "未选择";
    [ObservableProperty] public partial string TargetOverviewText { get; set; } = "0 个目标 | 0 个可写";
    [ObservableProperty] public partial string InstalledTargetOverviewText { get; set; } = "0 个已检测";
    [ObservableProperty] public partial string SelectedTargetOverviewText { get; set; } = "0 个已选 | 0 个可写";
    [ObservableProperty] public partial string BackupOverviewText { get; set; } = "0 个近期备份 | 0 个文件变更";
    [ObservableProperty] public partial string EndpointTakeoverText { get; set; } = "--";
    [ObservableProperty] public partial string ProtocolCoverageText { get; set; } = "--";
    [ObservableProperty] public partial int LastChangedFileCount { get; set; }
    [ObservableProperty] public partial int LastBackupFileCount { get; set; }
    [ObservableProperty] public partial bool IsCodexHistorySyncing { get; set; }
    [ObservableProperty] public partial string CodexHistoryStatusText { get; set; } = "尚未检查 Codex 历史";
    [ObservableProperty] public partial string CodexHistoryDetailText { get; set; } = "Codex 历史同步只会在用户确认后修改本地记录。";

    public bool IsEndpointBusy => IsProbing || IsFetchingModels || IsApplying || IsClientApiDiagnosing || IsCodexHistorySyncing;

    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<AppTargetItem> Targets { get; } = new();
    public ObservableCollection<ProtocolProbeItem> ProtocolProbeRows { get; } = new();
    public ObservableCollection<WriteTraceItem> WriteTraceSteps { get; } = new();
    public ObservableCollection<ConfigTemplateRow> TemplateRows { get; } = new();

    public event EventHandler? CodexConfigTemplateDialogOpenRequested;
    public event EventHandler? CodexHistoryMergeReviewRequested;

    public int SelectedTargetCount => Targets.Count(static target => target.IsSelected && target.IsSelectable);
    public int SelectedRestoreTargetCount => Targets.Count(static target => target.IsSelected);
    public string ApplySelectedButtonText => $"\u5e94\u7528\u5230\u6240\u9009 ({SelectedTargetCount})";
    public string RestoreSelectedButtonText => $"\u8fd8\u539f\u6240\u9009 ({SelectedRestoreTargetCount})";

    public ApplicationCenterViewModel()
        : this(
            SharedEndpointStore.Load,
            async ct => await new EndpointHistoryStore().LoadAsync(ct))
    {
    }

    public ApplicationCenterViewModel(
        Func<SharedEndpointState?> sharedEndpointLoader,
        Func<CancellationToken, Task<IReadOnlyList<EndpointHistoryItem>>> endpointHistoryLoader,
        CodexHistorySyncService? codexHistorySyncService = null,
        CodexChatMergeService? codexChatMergeService = null)
    {
        _sharedEndpointLoader = sharedEndpointLoader;
        _endpointHistoryLoader = endpointHistoryLoader;
        _codexHistorySyncService = codexHistorySyncService ?? new CodexHistorySyncService();
        _codexChatMergeService = codexChatMergeService ?? new CodexChatMergeService();
        _diagnosticsService = new ProxyDiagnosticsService();
        _modelCacheService = new ProxyEndpointModelCacheService();
        _probeService = new ProxyEndpointProtocolProbeService(_diagnosticsService, _modelCacheService);
        _clientAppConfigApplyService = new(vsCodeAdapter: new VsCodeClientConfigApplyAdapter(_vsCodeSettingsService));

        LoadPersistedEndpoint();
        ResetProbeRows();
        RefreshTargets();
        RefreshTemplateRows();
        ResetTraceSteps();
    }

    public CodexConfigTemplate CreateCodexTemplateSnapshot()
        => GetCurrentCodexTemplate();

    public CodexConfigTemplate CreateDefaultCodexTemplateSnapshot()
        => BuildDefaultCodexTemplate();

    public void ApplyCodexTemplate(CodexConfigTemplate template)
    {
        _customCodexTemplate = template;
        SelectedTargetName = "Codex";
        StatusText = "Codex 配置模板已更新";
        StatusMessage = "高级模板设置会在下次写入 Codex 配置时生效。";
        RefreshTargets();
        RefreshTemplateRows();
    }

    [RelayCommand]
    private void OpenCodexConfigTemplateDialog()
    {
        SelectedTargetName = "Codex";
        RefreshTemplateRows();
        CodexConfigTemplateDialogOpenRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SaveCodexConfigTemplate(CodexConfigTemplate? template)
    {
        if (template is null)
        {
            StatusText = "Codex 配置模板未发生变化。";
            return;
        }

        ApplyCodexTemplate(template);
    }

    [RelayCommand]
    private void ResetCodexConfigTemplate()
    {
        _customCodexTemplate = null;
        SelectedTargetName = "Codex";
        StatusText = "Codex 配置模板已重置为当前入口默认值。";
        StatusMessage = "下次写入 Codex 时会使用当前基础 URL、API 密钥、模型以及默认重试/上下文设置。";
        RefreshTargets();
        RefreshTemplateRows();
    }

    [RelayCommand]
    private void CloseCodexConfigTemplateDialog()
    {
        StatusText = "Codex 配置模板对话框已关闭。";
    }

    public void ConfigureClaudeRelayEndpointResolver(
        Func<ProxyEndpointSettings, ProxyEndpointProtocolProbeResult, string, Task<ClientApplyEndpoint?>>? resolver)
        => _claudeRelayEndpointResolver = resolver;

    [RelayCommand]
    private async Task RefreshCodexHistoryStatusAsync()
    {
        if (IsCodexHistorySyncing)
        {
            return;
        }

        IsCodexHistorySyncing = true;
        try
        {
            var status = await _codexHistorySyncService.GetStatusAsync();
            CodexHistoryStatusText = BuildCodexHistoryStatusSummary(status);
            CodexHistoryDetailText = BuildCodexHistoryStatusDetail(status);
            StatusText = CodexHistoryStatusText;
        }
        catch (Exception ex)
        {
            CodexHistoryStatusText = $"Codex 历史检查失败：{ex.Message}";
            CodexHistoryDetailText = ex.ToString();
            StatusText = CodexHistoryStatusText;
        }
        finally
        {
            IsCodexHistorySyncing = false;
        }
    }

    [RelayCommand]
    private async Task OpenCodexHistoryMergeReviewAsync()
    {
        await RefreshCodexHistoryStatusAsync();
        StatusMessage = "变更本地对话记录前，请先复核 Codex 历史合并。";
        CodexHistoryMergeReviewRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task<CodexChatMergeResult> MergeCodexHistoryAfterConfirmationAsync(
        CodexChatMergeTarget target,
        string? targetModel = null,
        CancellationToken cancellationToken = default)
    {
        if (IsCodexHistorySyncing)
        {
            throw new InvalidOperationException("Codex 历史同步已在运行。");
        }

        IsCodexHistorySyncing = true;
        try
        {
            var result = await _codexChatMergeService.MergeAsync(target, targetModel, cancellationToken);
            CodexHistoryStatusText = result.Succeeded
                ? result.Summary
                : $"Codex 历史同步失败：{result.Error ?? result.Summary}";
            CodexHistoryDetailText = BuildCodexChatMergeDetail(result);
            StatusText = CodexHistoryStatusText;
            StatusMessage = CodexHistoryDetailText;
            return result;
        }
        finally
        {
            IsCodexHistorySyncing = false;
        }
    }

    public void ApplyEndpointHistoryItem(EndpointHistoryItem? item)
    {
        if (item is null)
        {
            return;
        }

        ApplyExternalEndpoint(item.BaseUrl, item.ApiKey, item.Model, item.Models, $"历史入口：{item.BaseUrl}");
    }

    public void ApplyExternalEndpoint(
        string baseUrl,
        string apiKey,
        string model,
        IEnumerable<string>? models,
        string? sourceLabel = null)
    {
        _lastProbeResult = null;
        BaseUrl = baseUrl.Trim();
        ApiKey = apiKey.Trim();
        Model = model.Trim();
        AvailableModels.Clear();
        if (models is not null)
        {
            foreach (var availableModel in models.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                AvailableModels.Add(availableModel.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(Model) &&
            !AvailableModels.Contains(Model, StringComparer.OrdinalIgnoreCase))
        {
            AvailableModels.Insert(0, Model.Trim());
        }

        ResponsesSupported = false;
        AnthropicSupported = false;
        ChatSupported = false;
        PreferredProtocol = "--";
        ProbeResult = "尚未探测；写入前会重新检查协议";
        ResetProbeRows();
        StatusText = string.IsNullOrWhiteSpace(sourceLabel)
            ? $"已应用入口：{BaseUrl}"
            : $"已应用{sourceLabel}";
        RefreshTargets();
        RefreshTemplateRows();
        ResetTraceSteps();
    }

    partial void OnBaseUrlChanged(string value)
    {
        SyncCustomCodexTemplateCoreFields();
        RefreshTargets();
        RefreshTemplateRows();
    }

    partial void OnApiKeyChanged(string value)
    {
        SyncCustomCodexTemplateCoreFields();
        RefreshTargets();
        RefreshTemplateRows();
    }

    partial void OnModelChanged(string value)
    {
        SyncCustomCodexTemplateCoreFields();
        RefreshTargets();
        RefreshTemplateRows();
    }

    partial void OnIsProbingChanged(bool value)
        => OnPropertyChanged(nameof(IsEndpointBusy));

    partial void OnIsFetchingModelsChanged(bool value)
        => OnPropertyChanged(nameof(IsEndpointBusy));

    partial void OnIsApplyingChanged(bool value)
        => OnPropertyChanged(nameof(IsEndpointBusy));

    partial void OnIsCodexHistorySyncingChanged(bool value)
        => OnPropertyChanged(nameof(IsEndpointBusy));

}
