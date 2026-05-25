using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class ModelChatViewModel : ObservableObject
{
    private readonly ChatConversationService _chatService = new();
    private readonly ProxyDiagnosticsService _diagnosticsService = new();
    private readonly ProxyEndpointModelCacheService _modelCacheService;
    private readonly ProxyEndpointProtocolProbeService _protocolProbeService;
    private readonly ChatSessionSqliteStore _sessionStore = new();
    private readonly ChatPresetStore _presetStore = new();
    private readonly ChatAttachmentService _attachmentService = new();
    private readonly EndpointHistoryStore _historyStore = new();
    private readonly Func<SharedEndpointState?> _sharedEndpointLoader;
    private readonly Func<CancellationToken, Task<IReadOnlyList<EndpointHistoryItem>>> _endpointHistoryLoader;
    private CancellationTokenSource? _cts;
    private ProxyEndpointProtocolProbeResult? _lastProtocolProbeResult;
    private ChatEditSnapshot? _chatEditSnapshot;

    [ObservableProperty] public partial string BaseUrl { get; set; } = "";
    [ObservableProperty] public partial string ApiKey { get; set; } = "";
    [ObservableProperty] public partial string Model { get; set; } = "";
    [ObservableProperty] public partial string SystemPrompt { get; set; } = "You are a helpful assistant.";
    [ObservableProperty] public partial bool JsonMode { get; set; }
    [ObservableProperty] public partial double Temperature { get; set; } = 0.7;
    [ObservableProperty] public partial int MaxTokens { get; set; } = 4096;
    [ObservableProperty] public partial string InputText { get; set; } = "";
    [ObservableProperty] public partial bool IsStreaming { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial string LastMetrics { get; set; } = "";
    [ObservableProperty] public partial int InputTokens { get; set; }
    [ObservableProperty] public partial int OutputTokens { get; set; }
    [ObservableProperty] public partial int CachedTokens { get; set; }
    [ObservableProperty] public partial string CacheHitRate { get; set; } = "0.0%";
    [ObservableProperty] public partial string InputTokenTrend { get; set; } = "";
    [ObservableProperty] public partial string OutputTokenTrend { get; set; } = "";
    [ObservableProperty] public partial string CachedTokenTrend { get; set; } = "";
    [ObservableProperty] public partial string CacheHitTrend { get; set; } = "";
    [ObservableProperty] public partial string ResponseTime { get; set; } = "--";
    [ObservableProperty] public partial int MessageCount { get; set; }
    [ObservableProperty] public partial bool MultiModelEnabled { get; set; }
    [ObservableProperty] public partial string EndpointName { get; set; } = "--";
    [ObservableProperty] public partial string EndpointAddress { get; set; } = "--";
    [ObservableProperty] public partial string EndpointProtocol { get; set; } = "--";
    [ObservableProperty] public partial string ActiveConnections { get; set; } = "--";
    [ObservableProperty] public partial string EndpointTimeout { get; set; } = "--";
    [ObservableProperty] public partial string EndpointHealth { get; set; } = "--";
    [ObservableProperty] public partial string RouteSummary { get; set; } = "--";
    [ObservableProperty] public partial bool IsTransparentProxyEndpoint { get; set; }
    [ObservableProperty] public partial string TransparentProxyContextText { get; set; } = "未接入透明代理模型池";
    [ObservableProperty] public partial string TransparentProxyRouteCount { get; set; } = "0";
    [ObservableProperty] public partial string TransparentProxyModelCount { get; set; } = "0";
    [ObservableProperty] public partial string TransparentProxyCacheRate { get; set; } = "0.0%";
    [ObservableProperty] public partial string TransparentProxyProtocolSummary { get; set; } = "--";
    [ObservableProperty] public partial string TransparentProxyProviderSummary { get; set; } = "OpenAI 账号 0 · 可用 0 · API Key 路由 0";

    public ObservableCollection<ModelCompareEntry> ModelCompareEntries { get; } = new();
    [ObservableProperty] public partial string TokensPerSecond { get; set; } = "--";
    public ObservableCollection<string> AvailableModels { get; } = new();

    // ─── Phase 9: Presets ─────────────────────────────────────────────────

    public ObservableCollection<ChatPreset> ChatPresets { get; } = new();
    [ObservableProperty] public partial ChatPreset? SelectedPreset { get; set; }

    // ─── Phase 10: Attachments ────────────────────────────────────────────

    public ObservableCollection<ChatAttachmentItem> Attachments { get; } = new();
    public ObservableCollection<ChatAttachmentItem> PendingAttachments { get; } = new();
    [ObservableProperty] public partial string AttachmentError { get; set; } = "";
    [ObservableProperty] public partial bool IsChatSettingsPanelOpen { get; set; }

    public bool HasPendingAttachments => PendingAttachments.Count > 0;
    public bool IsEditingChatMessage => _chatEditSnapshot is not null;
    public string ChatSendButtonText => IsEditingChatMessage ? "重发" : "发送";
    public string ChatEditStatusText => IsEditingChatMessage
        ? "正在编辑上一条用户消息，发送后会从这里重新生成。"
        : string.Empty;
    public GridLength ChatSettingsPanelGridWidth => IsChatSettingsPanelOpen
        ? new GridLength(320)
        : new GridLength(0);

    // ─── Phase 12: Reasoning Effort & Multi-Model ─────────────────────────

    [ObservableProperty] public partial string SelectedReasoningEffort { get; set; } = "Medium";
    public ObservableCollection<string> SelectedCompareModels { get; } = new();

    public int TotalTokens => InputTokens + OutputTokens;
    public string ModelCompareTitle => $"模型对比（已选 {SelectedCompareModels.Count} 个）";
    public string InputTokensDisplay => InputTokens.ToString("N0", CultureInfo.InvariantCulture);
    public string OutputTokensDisplay => OutputTokens.ToString("N0", CultureInfo.InvariantCulture);
    public string CachedTokensDisplay => CachedTokens.ToString("N0", CultureInfo.InvariantCulture);

    public bool IsReasoningLowSelected => SelectedReasoningEffort == "Low";
    public bool IsReasoningMediumSelected => SelectedReasoningEffort == "Medium";
    public bool IsReasoningHighSelected => SelectedReasoningEffort == "High";

    public ModelChatViewModel()
        : this(
            SharedEndpointStore.Load,
            async ct => await new EndpointHistoryStore().LoadAsync(ct))
    {
    }

    public ModelChatViewModel(
        Func<SharedEndpointState?> sharedEndpointLoader,
        Func<CancellationToken, Task<IReadOnlyList<EndpointHistoryItem>>> endpointHistoryLoader)
    {
        _sharedEndpointLoader = sharedEndpointLoader;
        _endpointHistoryLoader = endpointHistoryLoader;
        _modelCacheService = new ProxyEndpointModelCacheService();
        _protocolProbeService = new ProxyEndpointProtocolProbeService(_diagnosticsService, _modelCacheService);
        LoadPersistedEndpoint();
        PendingAttachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPendingAttachments));
            SendChatMessageCommand.NotifyCanExecuteChanged();
            RemoveChatAttachmentCommand.NotifyCanExecuteChanged();
        };
        SelectedCompareModels.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ModelCompareTitle));
            ClearChatSelectedModelsCommand.NotifyCanExecuteChanged();
            RemoveChatSelectedModelCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>
    /// Loads persisted endpoint values from the shared endpoint store.
    /// </summary>
    private bool LoadPersistedEndpoint()
    {
        try
        {
            var shared = _sharedEndpointLoader();
            if (shared is not null && !string.IsNullOrWhiteSpace(shared.BaseUrl))
            {
                ApplyPersistedEndpoint(shared.BaseUrl, shared.ApiKey, shared.Model, [shared.Model]);
                return true;
            }

            var items = _endpointHistoryLoader(CancellationToken.None).GetAwaiter().GetResult();
            if (items is { Count: > 0 })
            {
                var latest = items[0];
                ApplyPersistedEndpoint(
                    latest.BaseUrl ?? string.Empty,
                    latest.ApiKey ?? string.Empty,
                    latest.Model ?? string.Empty,
                    latest.Models ?? []);
                return true;
            }
        }
        catch
        {
            // Best-effort
        }

        return false;
    }

    private void ApplyPersistedEndpoint(
        string baseUrl,
        string apiKey,
        string model,
        IEnumerable<string> models)
    {
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        Model = model;
        AvailableModels.Clear();
        AddAvailableModels(models);
        AddAvailableModel(Model);
        EndpointName = string.IsNullOrWhiteSpace(Model) ? "--" : Model.Trim();
        EndpointAddress = string.IsNullOrWhiteSpace(BaseUrl) ? "--" : BaseUrl.Trim();
        EndpointProtocol = "--";
        EndpointHealth = "待探测";
        EndpointTimeout = "--";
        ActiveConnections = "--";
        RouteSummary = string.IsNullOrWhiteSpace(BaseUrl) ? "--" : "当前入口已同步";
        IsTransparentProxyEndpoint = IsLocalTransparentProxyBaseUrl(BaseUrl);
        TransparentProxyContextText = IsTransparentProxyEndpoint ? $"已接入 {BaseUrl.Trim()}" : "未接入透明代理模型池";
        if (!IsTransparentProxyEndpoint)
        {
            TransparentProxyRouteCount = "0";
            TransparentProxyModelCount = "0";
            TransparentProxyCacheRate = "0.0%";
            TransparentProxyProtocolSummary = "--";
            TransparentProxyProviderSummary = "OpenAI 账号 0 · 可用 0 · API Key 路由 0";
        }
    }

    public bool TryApplyTransparentProxyEndpoint(TransparentProxyViewModel proxy, bool overwrite)
    {
        var alreadyOnProxy = IsSameEndpoint(BaseUrl, proxy.LocalEndpoint);
        if (!overwrite && !string.IsNullOrWhiteSpace(BaseUrl) && !alreadyOnProxy && !IsTransparentProxyEndpoint)
        {
            return false;
        }

        var models = BuildTransparentProxyModelList(proxy);
        var selected = alreadyOnProxy && !string.IsNullOrWhiteSpace(Model) &&
                       models.Contains(Model.Trim(), StringComparer.OrdinalIgnoreCase)
            ? Model.Trim()
            : models.FirstOrDefault() ?? string.Empty;
        BaseUrl = proxy.LocalEndpoint;
        ApiKey = "relaybench-local";
        Model = selected;
        AvailableModels.Clear();
        AddAvailableModels(models);
        EndpointName = string.IsNullOrWhiteSpace(selected) ? "--" : selected;
        EndpointAddress = proxy.LocalEndpoint;
        EndpointProtocol = "透明代理";
        EndpointHealth = proxy.IsRunning ? "运行中" : "已配置";
        EndpointTimeout = $"{proxy.P50Latency} / {proxy.P95Latency}";
        ActiveConnections = proxy.ActiveConnections.ToString(CultureInfo.InvariantCulture);
        RouteSummary = BuildTransparentProxyContext(proxy, models.Count);
        TransparentProxyContextText = RouteSummary;
        TransparentProxyRouteCount = proxy.Routes.Count(static route => route.Enabled).ToString(CultureInfo.InvariantCulture);
        TransparentProxyModelCount = models.Count.ToString(CultureInfo.InvariantCulture);
        TransparentProxyCacheRate = proxy.CacheHitRate;
        TransparentProxyProtocolSummary = BuildTransparentProxyProtocolSummary(proxy);
        TransparentProxyProviderSummary = proxy.ProviderAccountSummary;
        IsTransparentProxyEndpoint = true;
        StatusText = string.IsNullOrWhiteSpace(selected)
            ? "大模型对话已接入透明代理，等待真实模型池"
            : $"大模型对话已接入透明代理：{selected}";
        return true;
    }

    public void RefreshTransparentProxyContext(TransparentProxyViewModel proxy)
    {
        if (!IsTransparentProxyEndpoint && !IsSameEndpoint(BaseUrl, proxy.LocalEndpoint))
        {
            return;
        }

        TryApplyTransparentProxyEndpoint(proxy, overwrite: false);
    }

    private static IReadOnlyList<string> BuildTransparentProxyModelList(TransparentProxyViewModel proxy)
    {
        List<string> models = [];
        foreach (var item in proxy.ModelPool)
        {
            AddModel(models, item.Name);
        }

        foreach (var route in proxy.Routes.Where(static route => route.Enabled))
        {
            foreach (var model in SplitModelFilter(route.ModelFilter))
            {
                AddModel(models, model);
            }
        }

        foreach (var rule in proxy.ModelRewriteRules)
        {
            AddModel(models, rule.SourceModel);
            AddModel(models, rule.TargetModel);
        }

        return models;
    }

    private static void AddModel(List<string> models, string? value)
    {
        var model = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model) ||
            model == "*" ||
            models.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        models.Add(model);
    }

    private static IEnumerable<string> SplitModelFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            yield break;
        }

        foreach (var item in filter.Split([',', ';', '|', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                yield return item.Trim();
            }
        }
    }

    private static string BuildTransparentProxyContext(TransparentProxyViewModel proxy, int modelCount)
    {
        var enabledRoutes = proxy.Routes.Count(static route => route.Enabled);
        var codexOAuthRoutes = proxy.ProviderAccounts.Count(static item => item.IsOAuthCredential);
        return $"透明代理：{proxy.LocalEndpoint} · {enabledRoutes} 条启用路由 · {modelCount} 个候选模型 · Codex OAuth {codexOAuthRoutes}";
    }

    private static string BuildTransparentProxyProtocolSummary(TransparentProxyViewModel proxy)
    {
        var protocols = proxy.RouteQueue
            .Select(static item => item.Protocol?.Trim() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item) && !item.Equals("未知", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        if (protocols.Length > 0)
        {
            return string.Join(" / ", protocols);
        }

        var routeProtocols = proxy.Routes
            .Where(static route => route.Enabled)
            .Select(static route => string.IsNullOrWhiteSpace(route.Prefix) ? "自动" : route.Prefix.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        return routeProtocols.Length == 0 ? "--" : string.Join(" / ", routeProtocols);
    }

    private static bool IsLocalTransparentProxyBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameEndpoint(string? left, string? right)
    {
        if (!Uri.TryCreate(left?.Trim(), UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right?.Trim(), UriKind.Absolute, out var rightUri))
        {
            return false;
        }

        return leftUri.Port == rightUri.Port &&
               (leftUri.Host.Equals(rightUri.Host, StringComparison.OrdinalIgnoreCase) ||
                IsLoopbackHost(leftUri.Host) && IsLoopbackHost(rightUri.Host));
    }

    private static bool IsLoopbackHost(string host)
        => host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads cached model names and protocol metadata for the current endpoint.
    /// </summary>
    public async Task LoadCachedModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey))
        {
            return;
        }

        try
        {
            var cachedModels = await _modelCacheService.ListModelsAsync(BaseUrl, ApiKey);
            foreach (var model in cachedModels.Select(static item => item.Model))
            {
                AddAvailableModel(model);
            }

            if (string.IsNullOrWhiteSpace(Model) && AvailableModels.Count > 0)
            {
                Model = AvailableModels[0];
            }

            AddAvailableModel(Model);

            var cachedEndpoint = await _modelCacheService.TryResolveAsync(BaseUrl, ApiKey, Model);
            if (cachedEndpoint is not null)
            {
                ApplyCachedEndpointInfo(cachedEndpoint);
            }
        }
        catch
        {
            // Best-effort cache warm-up only.
        }
    }

    private void AddAvailableModel(string? model)
    {
        var normalized = model?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) ||
            AvailableModels.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        AvailableModels.Add(normalized);
    }

    private void AddAvailableModels(IEnumerable<string>? models)
    {
        if (models is null)
        {
            return;
        }

        foreach (var model in models)
        {
            AddAvailableModel(model);
        }
    }

    private void ApplyCachedEndpointInfo(CachedProxyEndpointModelInfo info)
    {
        EndpointName = string.IsNullOrWhiteSpace(info.Model) ? EndpointName : info.Model.Trim();
        EndpointAddress = string.IsNullOrWhiteSpace(info.BaseUrl) ? EndpointAddress : info.BaseUrl.Trim();
        EndpointProtocol = FormatWireApiDisplay(info.PreferredWireApi);
        EndpointHealth = BuildCachedHealthText(info);
        RouteSummary = BuildRouteSummary(info);
        EndpointTimeout = info.ProtocolProbeVersion is null
            ? "--"
            : $"协议缓存 v{info.ProtocolProbeVersion}";
        ActiveConnections = info.CheckedAt > DateTimeOffset.MinValue
            ? $"缓存于 {info.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "--";
    }

    private static string BuildCachedHealthText(CachedProxyEndpointModelInfo info)
    {
        var parts = new List<string>(3);
        parts.Add(info.ChatCompletionsSupported == true ? "Chat 支持" : "Chat 未确认");
        parts.Add(info.ResponsesSupported == true ? "Responses 支持" : "Responses 未确认");
        parts.Add(info.AnthropicMessagesSupported == true ? "Anthropic 支持" : "Anthropic 未确认");
        return string.Join(" · ", parts);
    }

    private static string BuildRouteSummary(CachedProxyEndpointModelInfo info)
    {
        var preferred = FormatWireApiDisplay(info.PreferredWireApi);
        var supportBits = new[]
        {
            info.ChatCompletionsSupported == true ? "Chat" : null,
            info.ResponsesSupported == true ? "Responses" : null,
            info.AnthropicMessagesSupported == true ? "Anthropic" : null
        }
        .Where(static item => !string.IsNullOrWhiteSpace(item));
        var supportText = string.Join("/", supportBits);
        return string.IsNullOrWhiteSpace(supportText)
            ? preferred
            : $"{preferred} · {supportText}";
    }

    private static string FormatWireApiDisplay(string? wireApi)
        => ProxyWireApiProbeService.NormalizeWireApi(wireApi) switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses 接口",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic 消息",
            ProxyWireApiProbeService.ChatCompletionsWireApi => "Chat 完整接口",
            _ => "--"
        };

    private static string FormatCompactNumber(double value)
    {
        if (value <= 0)
        {
            return "0";
        }

        return value >= 1000
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : Math.Round(value, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture);
    }

    partial void OnInputTokensChanged(int value)
    {
        OnPropertyChanged(nameof(TotalTokens));
        OnPropertyChanged(nameof(InputTokensDisplay));
    }

    partial void OnOutputTokensChanged(int value)
    {
        OnPropertyChanged(nameof(TotalTokens));
        OnPropertyChanged(nameof(OutputTokensDisplay));
    }

    partial void OnCachedTokensChanged(int value)
    {
        OnPropertyChanged(nameof(CachedTokensDisplay));
    }

    partial void OnSelectedReasoningEffortChanged(string value)
    {
        OnPropertyChanged(nameof(IsReasoningLowSelected));
        OnPropertyChanged(nameof(IsReasoningMediumSelected));
        OnPropertyChanged(nameof(IsReasoningHighSelected));
    }

    partial void OnSelectedPresetChanged(ChatPreset? value)
    {
        if (value is not null)
        {
            SystemPrompt = value.SystemPrompt;
        }
    }

    partial void OnIsChatSettingsPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ChatSettingsPanelGridWidth));
    }

    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    // ─── Session Management ───────────────────────────────────────────────

    /// <summary>
    /// All persisted sessions, ordered by last message time descending.
    /// </summary>
    public ObservableCollection<ChatSession> Sessions { get; } = new();

    /// <summary>
    /// The currently active session.
    /// </summary>
    [ObservableProperty] public partial ChatSession? CurrentSession { get; set; }

    /// <summary>
    /// Loads sessions from the store. Called on page initialization.
    /// </summary>
    public void LoadSessions()
    {
        Sessions.Clear();
        var sessions = _sessionStore.GetAllSessions();
        foreach (var s in sessions)
            Sessions.Add(s);

        if (Sessions.Count > 0)
        {
            SwitchToSession(Sessions[0]);
        }
        else
        {
            CreateNewSession();
        }
    }

}
