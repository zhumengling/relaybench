using System.Globalization;
using System.ComponentModel;
using System.Windows;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public string TransparentProxyPortText
    {
        get => _transparentProxyPortText;
        set
        {
            if (SetProperty(ref _transparentProxyPortText, value))
            {
                NotifyTransparentProxyEndpointChanged();
            }
        }
    }

    public string TransparentProxyRoutesText
    {
        get => _transparentProxyRoutesText;
        set
        {
            if (SetProperty(ref _transparentProxyRoutesText, value))
            {
                if (!_isUpdatingTransparentProxyRoutesTextFromEditor)
                {
                    RefreshTransparentProxyRouteEditorItemsFromText();
                }

                RefreshTransparentProxyRoutePreview();
            }
        }
    }

    public TransparentProxyRouteEditorItemViewModel? SelectedTransparentProxyRouteEditorItem
    {
        get => _selectedTransparentProxyRouteEditorItem;
        set
        {
            if (SetProperty(ref _selectedTransparentProxyRouteEditorItem, value))
            {
                RemoveTransparentProxyRouteEditorItemCommand.RaiseCanExecuteChanged();
                MoveTransparentProxyRouteEditorItemUpCommand.RaiseCanExecuteChanged();
                MoveTransparentProxyRouteEditorItemDownCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TransparentProxyRateLimitPerMinuteText
    {
        get => _transparentProxyRateLimitPerMinuteText;
        set => SetProperty(ref _transparentProxyRateLimitPerMinuteText, value);
    }

    public string TransparentProxyMaxConcurrencyText
    {
        get => _transparentProxyMaxConcurrencyText;
        set => SetProperty(ref _transparentProxyMaxConcurrencyText, value);
    }

    public string TransparentProxyRouteStrategyKey
    {
        get => _transparentProxyRouteStrategyKey;
        set
        {
            var normalized = TransparentProxyRouteStrategies.Normalize(value);
            if (SetProperty(ref _transparentProxyRouteStrategyKey, normalized))
            {
                RefreshTransparentProxyRoutePreview();
            }
        }
    }

    public bool TransparentProxyEnableFallback
    {
        get => _transparentProxyEnableFallback;
        set => SetProperty(ref _transparentProxyEnableFallback, value);
    }

    public bool TransparentProxyEnableCache
    {
        get => _transparentProxyEnableCache;
        set => SetProperty(ref _transparentProxyEnableCache, value);
    }

    public string TransparentProxyCacheTtlSecondsText
    {
        get => _transparentProxyCacheTtlSecondsText;
        set => SetProperty(ref _transparentProxyCacheTtlSecondsText, value);
    }

    public string TransparentProxyRequestRetryText
    {
        get => _transparentProxyRequestRetryText;
        set => SetProperty(ref _transparentProxyRequestRetryText, value);
    }

    public string TransparentProxyMaxRetryIntervalSecondsText
    {
        get => _transparentProxyMaxRetryIntervalSecondsText;
        set => SetProperty(ref _transparentProxyMaxRetryIntervalSecondsText, value);
    }

    public string TransparentProxySessionAffinityTtlSecondsText
    {
        get => _transparentProxySessionAffinityTtlSecondsText;
        set => SetProperty(ref _transparentProxySessionAffinityTtlSecondsText, value);
    }

    public string TransparentProxyModelCooldownSecondsText
    {
        get => _transparentProxyModelCooldownSecondsText;
        set => SetProperty(ref _transparentProxyModelCooldownSecondsText, value);
    }

    public bool TransparentProxyRewriteModel
    {
        get => _transparentProxyRewriteModel;
        set => SetProperty(ref _transparentProxyRewriteModel, value);
    }

    public bool IsTransparentProxySettingsDrawerOpen
    {
        get => _isTransparentProxySettingsDrawerOpen;
        private set => SetProperty(ref _isTransparentProxySettingsDrawerOpen, value);
    }

    public bool IsTransparentProxyListenSettingsOpen
    {
        get => _isTransparentProxyListenSettingsOpen;
        private set => SetProperty(ref _isTransparentProxyListenSettingsOpen, value);
    }

    public bool IsTransparentProxyProviderSettingsOpen
    {
        get => _isTransparentProxyProviderSettingsOpen;
        private set => SetProperty(ref _isTransparentProxyProviderSettingsOpen, value);
    }

    public bool IsTransparentProxyRouteSettingsOpen
    {
        get => _isTransparentProxyRouteSettingsOpen;
        private set => SetProperty(ref _isTransparentProxyRouteSettingsOpen, value);
    }

    public bool IsTransparentProxyLogExpanded
    {
        get => _isTransparentProxyLogExpanded;
        private set
        {
            if (SetProperty(ref _isTransparentProxyLogExpanded, value))
            {
                OnPropertyChanged(nameof(TransparentProxyLogExpandIcon));
                OnPropertyChanged(nameof(TransparentProxyLogExpandToolTip));
            }
        }
    }

    public string TransparentProxyLogExpandIcon
        => IsTransparentProxyLogExpanded ? "\uE73F" : "\uE740";

    public string TransparentProxyLogExpandToolTip
        => IsTransparentProxyLogExpanded ? "收起日志" : "展开日志";

    public string TransparentProxyLogFilterText
    {
        get => _transparentProxyLogFilterText;
        set
        {
            if (SetProperty(ref _transparentProxyLogFilterText, value ?? string.Empty))
            {
                RefreshTransparentProxyLogView();
            }
        }
    }

    public TransparentProxyLogEntryViewModel? SelectedTransparentProxyLog
    {
        get => _selectedTransparentProxyLog;
        set
        {
            if (SetProperty(ref _selectedTransparentProxyLog, value))
            {
                IsTransparentProxyLogDetailOpen = value is not null;
            }
        }
    }

    public bool IsTransparentProxyLogDetailOpen
    {
        get => _isTransparentProxyLogDetailOpen;
        private set => SetProperty(ref _isTransparentProxyLogDetailOpen, value);
    }

    public TransparentProxyRouteEditorItemViewModel? TransparentProxyRouteSettingsItem
    {
        get => _transparentProxyRouteSettingsItem;
        private set => SetProperty(ref _transparentProxyRouteSettingsItem, value);
    }

    public bool IsTransparentProxyRunning
    {
        get => _isTransparentProxyRunning;
        private set
        {
            if (SetProperty(ref _isTransparentProxyRunning, value))
            {
                StartTransparentProxyCommand.RaiseCanExecuteChanged();
                StopTransparentProxyCommand.RaiseCanExecuteChanged();
                ApplyTransparentProxyToAppsCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(TransparentProxyRunStateText));
                OnPropertyChanged(nameof(TransparentProxyRunStateBrush));
                NotifyTransparentProxyEndpointChanged();
            }
        }
    }

    public string TransparentProxyStatusSummary
    {
        get => _transparentProxyStatusSummary;
        private set => SetProperty(ref _transparentProxyStatusSummary, value);
    }

    public string TransparentProxyMetricsSummary
    {
        get => _transparentProxyMetricsSummary;
        private set => SetProperty(ref _transparentProxyMetricsSummary, value);
    }

    public string TransparentProxyRoutingSummary
    {
        get => _transparentProxyRoutingSummary;
        private set => SetProperty(ref _transparentProxyRoutingSummary, value);
    }

    public string TransparentProxyHealthSummary
    {
        get => _transparentProxyHealthSummary;
        private set => SetProperty(ref _transparentProxyHealthSummary, value);
    }

    public string TransparentProxyProtocolSummary
    {
        get => _transparentProxyProtocolSummary;
        private set => SetProperty(ref _transparentProxyProtocolSummary, value);
    }

    public string TransparentProxyTotalRequestsText
    {
        get => _transparentProxyTotalRequestsText;
        private set => SetProperty(ref _transparentProxyTotalRequestsText, value);
    }

    public string TransparentProxySuccessRateText
    {
        get => _transparentProxySuccessRateText;
        private set => SetProperty(ref _transparentProxySuccessRateText, value);
    }

    public string TransparentProxyActiveRequestsText
    {
        get => _transparentProxyActiveRequestsText;
        private set => SetProperty(ref _transparentProxyActiveRequestsText, value);
    }

    public string TransparentProxyFallbackRequestsText
    {
        get => _transparentProxyFallbackRequestsText;
        private set => SetProperty(ref _transparentProxyFallbackRequestsText, value);
    }

    public string TransparentProxyCacheHitsText
    {
        get => _transparentProxyCacheHitsText;
        private set => SetProperty(ref _transparentProxyCacheHitsText, value);
    }

    public string TransparentProxyP95LatencyText
    {
        get => _transparentProxyP95LatencyText;
        private set => SetProperty(ref _transparentProxyP95LatencyText, value);
    }

    public string TransparentProxyTotalTokensText
    {
        get => _transparentProxyTotalTokensText;
        private set => SetProperty(ref _transparentProxyTotalTokensText, value);
    }

    public string TransparentProxyTokensPerSecondText
    {
        get => _transparentProxyTokensPerSecondText;
        private set => SetProperty(ref _transparentProxyTokensPerSecondText, value);
    }

    public string TransparentProxyTokenMeterPrimaryText
    {
        get => _transparentProxyTokenMeterPrimaryText;
        private set => SetProperty(ref _transparentProxyTokenMeterPrimaryText, value);
    }

    public string TransparentProxyTokenMeterSecondaryText
    {
        get => _transparentProxyTokenMeterSecondaryText;
        private set => SetProperty(ref _transparentProxyTokenMeterSecondaryText, value);
    }

    public string TransparentProxyTokenMeterAccentBrush
    {
        get => _transparentProxyTokenMeterAccentBrush;
        private set => SetProperty(ref _transparentProxyTokenMeterAccentBrush, value);
    }

    public string TransparentProxyLocalEndpoint
        => $"http://127.0.0.1:{ParseTransparentProxyPort()}/v1";

    public string TransparentProxyHealthEndpoint
        => $"http://127.0.0.1:{ParseTransparentProxyPort()}/relaybench/health";

    public string TransparentProxyRunStateText
        => IsTransparentProxyRunning ? "运行中" : "已停止";

    public string TransparentProxyRunStateBrush
        => IsTransparentProxyRunning ? "#059669" : "#64748B";

    private bool CanStartTransparentProxy()
        => !IsTransparentProxyRunning && !IsBusy;

    private async Task StartTransparentProxyAsync()
    {
        var routes = TransparentProxyRouteTextCodec.ParseRoutes(TransparentProxyRoutesText);
        if (routes.Count == 0)
        {
            TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
            routes = TransparentProxyRouteTextCodec.ParseRoutes(TransparentProxyRoutesText);
        }

        if (routes.Count == 0)
        {
            throw new InvalidOperationException("没有可用上游。请先填写当前接口，或从批量候选生成路由表。");
        }

        routes = await ResolveTransparentProxyRouteProtocolsAsync(
            routes,
            forceProbe: false,
            fetchCatalogModels: false,
            CancellationToken.None);

        TransparentProxyServerConfig config = new(
            ParseTransparentProxyPort(),
            routes,
            ParseBoundedInt(TransparentProxyRateLimitPerMinuteText, fallback: 60, min: 0, max: 6000),
            ParseBoundedInt(TransparentProxyMaxConcurrencyText, fallback: 8, min: 1, max: 128),
            TransparentProxyEnableFallback,
            TransparentProxyEnableCache,
            ParseBoundedInt(TransparentProxyCacheTtlSecondsText, fallback: 60, min: 1, max: 3600),
            false,
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 300))
        {
            RouteStrategy = TransparentProxyRouteStrategyKey,
            RequestRetry = ParseBoundedInt(TransparentProxyRequestRetryText, fallback: 1, min: 0, max: 5),
            MaxRetryIntervalSeconds = ParseBoundedInt(TransparentProxyMaxRetryIntervalSecondsText, fallback: 8, min: 1, max: 60),
            SessionAffinityTtlSeconds = ParseBoundedInt(TransparentProxySessionAffinityTtlSecondsText, fallback: 1800, min: 30, max: 86400),
            ModelCooldownSeconds = ParseBoundedInt(TransparentProxyModelCooldownSecondsText, fallback: 120, min: 15, max: 1800)
        };

        await _transparentProxyService.StartAsync(config);
        IsTransparentProxyRunning = true;
        TransparentProxyStatusSummary = $"本地入口已监听：{TransparentProxyLocalEndpoint}";
        TransparentProxyHealthSummary = $"健康检查：{TransparentProxyHealthEndpoint}";
        RefreshTransparentProxyRoutePreview();
        BeginTransparentProxyProtocolAutoDiscovery(routes);
        SaveState();
    }

    private async Task StopTransparentProxyAsync()
    {
        CancelTransparentProxyProtocolAutoDiscovery();
        await _transparentProxyService.StopAsync();
        IsTransparentProxyRunning = false;
        TransparentProxyStatusSummary = "本地透明代理已停止。";
        TransparentProxyHealthSummary = "健康检查：未监听。";
    }

    public async Task StopTransparentProxyForExitAsync()
    {
        if (!IsTransparentProxyRunning)
        {
            return;
        }

        await StopTransparentProxyAsync();
    }

    private bool CanApplyTransparentProxyToApps()
        => !IsBusy;

    private async Task ApplyTransparentProxyToAppsAsync()
    {
        if (!IsTransparentProxyRunning)
        {
            await StartTransparentProxyAsync();
        }

        var routes = TransparentProxyRouteTextCodec.ParseRoutes(TransparentProxyRoutesText);
        if (routes.Count == 0)
        {
            StatusMessage = "透明代理还没有可用路由，暂时不能应用到软件。";
            return;
        }

        var model = ResolveTransparentProxyClientDefaultModel(routes);
        var apiKey = ResolveTransparentProxyClientApiKey(routes);
        var settings = BuildProxySettings(TransparentProxyLocalEndpoint, apiKey, model);
        var protocolProbeResult = BuildTransparentProxyClientApplyProbeResult(routes, settings);
        var selectedTargets = await ChooseClientApplyTargetsAsync(
            "应用本地透明代理到软件",
            settings,
            protocolProbeResult);
        if (selectedTargets.Count == 0)
        {
            StatusMessage = "已取消本地透明代理应用。";
            return;
        }

        await ExecuteBusyActionAsync(
            "正在应用本地透明代理到软件...",
            async () =>
            {
                var endpoint = new ClientApplyEndpoint(
                    settings.BaseUrl,
                    settings.ApiKey,
                    settings.Model,
                    "RelayBench Transparent Proxy",
                    null,
                    protocolProbeResult.PreferredWireApi);
                var result = await _clientAppConfigApplyService.ApplyAsync(endpoint, selectedTargets);

                CodexChatMergeResult? mergeResult = null;
                if (ShouldAskCodexChatMerge(selectedTargets, result))
                {
                    var shouldMergeChats = await ConfirmCodexChatMergeAsync(
                        CodexChatMergeTarget.ThirdPartyCustom,
                        "切到 RelayBench 本地透明代理");
                    mergeResult = await MergeCodexChatsIfRequestedAsync(
                        shouldMergeChats,
                        CodexChatMergeTarget.ThirdPartyCustom);
                }

                AppendModuleOutput(
                    "本地透明代理应用到软件",
                    BuildClientApplyResultSummary(result, mergeResult),
                    BuildClientApplyResultDetail(
                        "本地透明代理",
                        settings.BaseUrl,
                        settings.ApiKey,
                        settings.Model,
                        result,
                        mergeResult));

                StatusMessage = BuildClientApplyStatusMessage(result, mergeResult);
                if (HasSucceededTarget(result))
                {
                    SaveState();
                    await RunClientApiDiagnosticsCoreAsync();
                }
            });
    }

    private Task RefreshTransparentProxyRoutesFromWorkspaceAsync()
    {
        TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
        RefreshTransparentProxyRouteEditorItemsFromText();
        SaveState();
        return Task.CompletedTask;
    }

    private Task AddTransparentProxyRouteEditorItemAsync()
    {
        var index = TransparentProxyRouteEditorItems.Count + 1;
        var item = new TransparentProxyRouteEditorItemViewModel
        {
            IsEnabled = true,
            Name = $"路由 {index}",
            BaseUrl = string.Empty,
            ModelsText = string.Empty,
            ApiKey = string.Empty
        };
        AttachTransparentProxyRouteEditorItem(item);
        TransparentProxyRouteEditorItems.Add(item);
        SelectedTransparentProxyRouteEditorItem = item;
        UpdateTransparentProxyRoutesTextFromEditor();
        return Task.CompletedTask;
    }

    private Task ToggleTransparentProxySettingsDrawerAsync()
    {
        IsTransparentProxySettingsDrawerOpen = !IsTransparentProxySettingsDrawerOpen;
        if (!IsTransparentProxySettingsDrawerOpen)
        {
            IsTransparentProxyListenSettingsOpen = false;
            IsTransparentProxyProviderSettingsOpen = false;
        }

        return Task.CompletedTask;
    }

    private Task ToggleTransparentProxyListenSettingsAsync()
    {
        var shouldOpen = !IsTransparentProxyListenSettingsOpen;
        IsTransparentProxyListenSettingsOpen = shouldOpen;
        if (shouldOpen)
        {
            IsTransparentProxyProviderSettingsOpen = false;
        }

        return Task.CompletedTask;
    }

    private Task ToggleTransparentProxyProviderSettingsAsync()
    {
        var shouldOpen = !IsTransparentProxyProviderSettingsOpen;
        IsTransparentProxyProviderSettingsOpen = shouldOpen;
        if (shouldOpen)
        {
            IsTransparentProxyListenSettingsOpen = false;
        }

        return Task.CompletedTask;
    }

    private Task OpenTransparentProxyRouteSettingsAsync(TransparentProxyRouteEditorItemViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        SelectedTransparentProxyRouteEditorItem = item;
        TransparentProxyRouteSettingsItem = item;
        IsTransparentProxyRouteSettingsOpen = true;
        return Task.CompletedTask;
    }

    private Task OpenTransparentProxyRuntimeRouteSettingsAsync(TransparentProxyRouteViewModel? route)
    {
        if (route is null)
        {
            return Task.CompletedTask;
        }

        var item = TransparentProxyRouteEditorItems.FirstOrDefault(editor =>
            string.Equals(TransparentProxyRouteTextCodec.BuildRouteId(editor.Name, editor.BaseUrl, editor.Prefix), route.Id, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            return OpenTransparentProxyRouteSettingsAsync(item);
        }

        TransparentProxyStatusSummary = "未找到可编辑的路由节点。";
        return Task.CompletedTask;
    }

    private Task CloseTransparentProxyRouteSettingsAsync()
    {
        IsTransparentProxyRouteSettingsOpen = false;
        TransparentProxyRouteSettingsItem = null;
        UpdateTransparentProxyRoutesTextFromEditor();
        SaveState();
        return Task.CompletedTask;
    }

    private async Task FetchTransparentProxyRouteEditorItemModelsAsync(TransparentProxyRouteEditorItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.BaseUrl))
        {
            throw new InvalidOperationException("请先填写 Base URL。");
        }

        var effectiveApiKey = !string.IsNullOrWhiteSpace(item.ApiKey)
            ? item.ApiKey
            : ProxyApiKey;
        if (string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            throw new InvalidOperationException("请先填写 API Key。");
        }

        var settings = new ProxyEndpointSettings(
            item.BaseUrl,
            effectiveApiKey,
            item.Models.FirstOrDefault() ?? ProxyModel,
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 300));
        var catalog = await _proxyDiagnosticsService.FetchModelsAsync(settings);
        await CacheProxyModelCatalogResultAsync(settings, catalog, CancellationToken.None);
        if (!catalog.Success)
        {
            throw new InvalidOperationException(catalog.Error ?? catalog.Summary);
        }

        var models = catalog.ModelItems is { Count: > 0 }
            ? catalog.ModelItems.Select(static model => model.Id)
            : catalog.Models;
        item.ReplaceModelMappings(models
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
        UpdateTransparentProxyRoutesTextFromEditor();
        SaveState();
        TransparentProxyProtocolSummary = $"模型列表：{item.Name} 已拉取 {item.Models.Count} 个模型。";
    }

    private Task AddTransparentProxyRouteModelMappingAsync(TransparentProxyRouteEditorItemViewModel? item)
    {
        item ??= TransparentProxyRouteSettingsItem ?? SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        item.AddModelMapping();
        UpdateTransparentProxyRoutesTextFromEditor();
        return Task.CompletedTask;
    }

    private Task ResetTransparentProxyRouteCircuitAsync(TransparentProxyRouteEditorItemViewModel? item)
    {
        item ??= TransparentProxyRouteSettingsItem ?? SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var routeId = TransparentProxyRouteTextCodec.BuildRouteId(item.Name, item.BaseUrl, item.Prefix);
        TransparentProxyStatusSummary = _transparentProxyService.ResetRouteCircuit(routeId)
            ? $"Route circuit reset: {item.Name}"
            : $"Route circuit is not active yet: {item.Name}";
        return Task.CompletedTask;
    }

    private Task RemoveTransparentProxyRouteModelMappingAsync(TransparentProxyModelMappingViewModel? model)
    {
        if (model is null)
        {
            return Task.CompletedTask;
        }

        var owner = TransparentProxyRouteEditorItems.FirstOrDefault(item => item.ModelMappings.Contains(model));
        owner?.RemoveModelMapping(model);
        UpdateTransparentProxyRoutesTextFromEditor();
        return Task.CompletedTask;
    }

    private Task RemoveTransparentProxyRouteEditorItemAsync()
    {
        var item = SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var index = TransparentProxyRouteEditorItems.IndexOf(item);
        DetachTransparentProxyRouteEditorItem(item);
        TransparentProxyRouteEditorItems.Remove(item);
        SelectedTransparentProxyRouteEditorItem = TransparentProxyRouteEditorItems.Count == 0
            ? null
            : TransparentProxyRouteEditorItems[Math.Clamp(index, 0, TransparentProxyRouteEditorItems.Count - 1)];
        UpdateTransparentProxyRoutesTextFromEditor();
        return Task.CompletedTask;
    }

    private Task MoveTransparentProxyRouteEditorItemUpAsync()
    {
        var item = SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var index = TransparentProxyRouteEditorItems.IndexOf(item);
        if (index > 0)
        {
            TransparentProxyRouteEditorItems.Move(index, index - 1);
            SelectedTransparentProxyRouteEditorItem = item;
            UpdateTransparentProxyRoutesTextFromEditor();
        }

        return Task.CompletedTask;
    }

    private Task MoveTransparentProxyRouteEditorItemDownAsync()
    {
        var item = SelectedTransparentProxyRouteEditorItem;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var index = TransparentProxyRouteEditorItems.IndexOf(item);
        if (index >= 0 && index < TransparentProxyRouteEditorItems.Count - 1)
        {
            TransparentProxyRouteEditorItems.Move(index, index + 1);
            SelectedTransparentProxyRouteEditorItem = item;
            UpdateTransparentProxyRoutesTextFromEditor();
        }

        return Task.CompletedTask;
    }

    private Task ProbeTransparentProxyProtocolsAsync()
        => ExecuteBusyActionAsync(
            "正在拉取透明代理上游模型并探测协议...",
            async () =>
            {
                var routes = TransparentProxyRouteTextCodec.ParseRoutes(TransparentProxyRoutesText);
                if (routes.Count == 0)
                {
                    TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
                    routes = TransparentProxyRouteTextCodec.ParseRoutes(TransparentProxyRoutesText);
                }

                if (routes.Count == 0)
                {
                    TransparentProxyProtocolSummary = "协议探测：没有可用路由。";
                    return;
                }

                UpdateGlobalTaskProgress("拉取模型", 12d);
                var hydratedRoutes = await ResolveTransparentProxyRouteProtocolsAsync(
                    routes,
                    forceProbe: true,
                    fetchCatalogModels: true,
                    CancellationToken.None);
                ApplyHydratedTransparentProxyRoutesToEditor(hydratedRoutes);
                RefreshTransparentProxyRoutePreview();
                if (IsTransparentProxyRunning)
                {
                    _transparentProxyService.UpdateRouteProtocols(hydratedRoutes);
                }

                SaveState();
            },
            "透明代理协议探测",
            "探测中",
            6d);

    private Task RunTransparentProxySelfTestAsync()
        => ExecuteBusyActionAsync(
            "正在运行透明代理本地自检...",
            async () =>
            {
                UpdateGlobalTaskProgress("启动本地 fake upstream", 12d);
                var result = await _transparentProxySelfTestService.RunAsync(CancellationToken.None);
                UpdateGlobalTaskProgress("校验协议和缓存", 86d);
                TransparentProxyProtocolSummary = result.Summary;
                TransparentProxyStatusSummary = result.Summary;
                StatusMessage = result.Summary;
                var createdAt = DateTimeOffset.Now;
                foreach (var check in result.Checks)
                {
                    _allTransparentProxyLogs.Insert(0, new TransparentProxyLogEntryViewModel(new TransparentProxyLogEntry(
                        createdAt,
                        check.Passed ? "INFO" : "ERROR",
                        "SELFTEST",
                        "/relaybench/self-test",
                        "local",
                        check.Passed ? 200 : 500,
                        0,
                        check.DisplayText,
                        "self-test",
                        $"selftest-{createdAt.ToUnixTimeMilliseconds()}")));
                }

                RefreshTransparentProxyLogView();
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Summary);
                }
            },
            "透明代理自检",
            "本地自检",
            8d);

    private void BeginTransparentProxyProtocolAutoDiscovery(IReadOnlyList<TransparentProxyRoute> routes)
    {
        CancelTransparentProxyProtocolAutoDiscovery();
        if (routes.Count == 0 || routes.All(static route => string.IsNullOrWhiteSpace(route.ApiKey)))
        {
            return;
        }

        _transparentProxyAutoDiscoveryCancellationSource = new CancellationTokenSource();
        _ = RunTransparentProxyProtocolAutoDiscoveryAsync(
            routes.ToArray(),
            _transparentProxyAutoDiscoveryCancellationSource);
    }

    private void CancelTransparentProxyProtocolAutoDiscovery()
    {
        if (_transparentProxyAutoDiscoveryCancellationSource is null)
        {
            return;
        }

        _transparentProxyAutoDiscoveryCancellationSource.Cancel();
        _transparentProxyAutoDiscoveryCancellationSource = null;
    }

    private async Task RunTransparentProxyProtocolAutoDiscoveryAsync(
        IReadOnlyList<TransparentProxyRoute> routes,
        CancellationTokenSource cancellationSource)
    {
        var cancellationToken = cancellationSource.Token;
        try
        {
            TransparentProxyProtocolSummary = "自动探测：后台拉取上游模型，补齐 Responses / Anthropic / OpenAI Chat 协议缓存。";
            var hydratedRoutes = await ResolveTransparentProxyRouteProtocolsAsync(
                routes,
                forceProbe: false,
                fetchCatalogModels: true,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ApplyHydratedTransparentProxyRoutesToEditor(hydratedRoutes);
            RefreshTransparentProxyRoutePreview();
            if (IsTransparentProxyRunning)
            {
                _transparentProxyService.UpdateRouteProtocols(hydratedRoutes);
            }

            SaveState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxy.AutoDiscovery", ex);
            TransparentProxyProtocolSummary = $"自动探测：后台刷新失败，代理继续运行；{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_transparentProxyAutoDiscoveryCancellationSource, cancellationSource))
            {
                _transparentProxyAutoDiscoveryCancellationSource = null;
            }

            cancellationSource.Dispose();
        }
    }

    private Task CopyTransparentProxyEndpointAsync()
    {
        Clipboard.SetText(TransparentProxyLocalEndpoint);
        TransparentProxyStatusSummary = $"已复制本地入口：{TransparentProxyLocalEndpoint}";
        return Task.CompletedTask;
    }

    private Task ClearTransparentProxyLogsAsync()
    {
        _ = _transparentProxyLogStore.ClearAsync();
        _allTransparentProxyLogs.Clear();
        TransparentProxyLogs.Clear();
        SelectedTransparentProxyLog = null;
        IsTransparentProxyLogDetailOpen = false;
        return Task.CompletedTask;
    }

    private async Task ExportTransparentProxyLogsAsync()
    {
        var exportPath = await _transparentProxyLogStore.ExportCsvAsync(RelayBenchPaths.ExportsDirectory);
        TransparentProxyStatusSummary = $"Transparent proxy logs exported: {exportPath}";
    }

    private Task CloseTransparentProxyLogDetailAsync()
    {
        SelectedTransparentProxyLog = null;
        IsTransparentProxyLogDetailOpen = false;
        return Task.CompletedTask;
    }

    private Task ClearTransparentProxyCacheAsync()
    {
        var count = _transparentProxyService.ClearCache();
        TransparentProxyStatusSummary = count <= 0
            ? "短缓存为空，无需清理。"
            : $"已清空短缓存：{count} 条。";
        return Task.CompletedTask;
    }

    private Task ToggleTransparentProxyLogExpandedAsync()
    {
        IsTransparentProxyLogExpanded = !IsTransparentProxyLogExpanded;
        return Task.CompletedTask;
    }

    private void LoadTransparentProxyState(AppStateSnapshot snapshot)
    {
        var config = _transparentProxyConfigStore.Load(snapshot);
        TransparentProxyPortText = config.PortText;
        TransparentProxyRoutesText = config.RoutesText;
        TransparentProxyRateLimitPerMinuteText = config.RateLimitPerMinuteText;
        TransparentProxyMaxConcurrencyText = config.MaxConcurrencyText;
        TransparentProxyRouteStrategyKey = config.RouteStrategyKey;
        TransparentProxyEnableFallback = config.EnableFallback;
        TransparentProxyEnableCache = config.EnableCache;
        TransparentProxyCacheTtlSecondsText = config.CacheTtlSecondsText;
        TransparentProxyRequestRetryText = config.RequestRetryText;
        TransparentProxyMaxRetryIntervalSecondsText = config.MaxRetryIntervalSecondsText;
        TransparentProxySessionAffinityTtlSecondsText = config.SessionAffinityTtlSecondsText;
        TransparentProxyModelCooldownSecondsText = config.ModelCooldownSecondsText;
        TransparentProxyRewriteModel = config.RewriteModel;

        if (string.IsNullOrWhiteSpace(TransparentProxyRoutesText))
        {
            TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
        }

        RefreshTransparentProxyRouteEditorItemsFromText();
        RefreshTransparentProxyRoutePreview();
    }

    private void ApplyTransparentProxyStateToSnapshot(AppStateSnapshot snapshot)
    {
        var config = CreateTransparentProxyConfigSnapshot();
        TransparentProxyConfigStore.ApplyToAppState(config, snapshot);
        _transparentProxyConfigStore.Save(config);
    }

    private TransparentProxyConfigSnapshot CreateTransparentProxyConfigSnapshot()
        => new()
        {
            PortText = TransparentProxyPortText,
            RoutesText = TransparentProxyRoutesText,
            RateLimitPerMinuteText = TransparentProxyRateLimitPerMinuteText,
            MaxConcurrencyText = TransparentProxyMaxConcurrencyText,
            RouteStrategyKey = TransparentProxyRouteStrategyKey,
            EnableFallback = TransparentProxyEnableFallback,
            EnableCache = TransparentProxyEnableCache,
            CacheTtlSecondsText = TransparentProxyCacheTtlSecondsText,
            RequestRetryText = TransparentProxyRequestRetryText,
            MaxRetryIntervalSecondsText = TransparentProxyMaxRetryIntervalSecondsText,
            SessionAffinityTtlSecondsText = TransparentProxySessionAffinityTtlSecondsText,
            ModelCooldownSecondsText = TransparentProxyModelCooldownSecondsText,
            RewriteModel = TransparentProxyRewriteModel
        };

    private async Task<IReadOnlyList<TransparentProxyRoute>> ResolveTransparentProxyRouteProtocolsAsync(
        IReadOnlyList<TransparentProxyRoute> routes,
        bool forceProbe,
        bool fetchCatalogModels,
        CancellationToken cancellationToken)
    {
        if (routes.Count == 0)
        {
            TransparentProxyProtocolSummary = "协议探测：没有可用路由。";
            return routes;
        }

        var options = new TransparentProxyProtocolDiscoveryOptions(
            forceProbe,
            fetchCatalogModels,
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 300),
            ProxyModel);
        var progress = new Progress<TransparentProxyProtocolDiscoveryProgress>(item =>
        {
            if (IsGlobalTaskProgressVisible)
            {
                UpdateGlobalTaskProgress(
                    item.CurrentRoute,
                    item.TotalRoutes,
                    fetchCatalogModels && !string.IsNullOrWhiteSpace(item.RouteName)
                        ? $"探测 {item.RouteName}"
                        : "读取协议缓存");
            }
        });
        var result = await _transparentProxyProtocolDiscoveryService.DiscoverAsync(
            routes,
            options,
            progress,
            cancellationToken);

        foreach (var snapshot in result.Snapshots)
        {
            _transparentProxyProtocolSnapshots[snapshot.Key] = snapshot.Value;
        }

        TransparentProxyProtocolSummary =
            $"协议探测：写入/刷新 {result.ProbedModels} 个模型，命中缓存 {result.CachedModels} 个，跳过 {result.SkippedRoutes} 条路由。优先级 Responses → Anthropic → OpenAI Chat。";
        return result.HydratedRoutes;
    }

    private string BuildTransparentProxyRoutesTextFromWorkspace()
    {
        List<TransparentProxyRouteTextSeed> seeds = [];

        if (!string.IsNullOrWhiteSpace(ProxyBaseUrl))
        {
            seeds.Add(new TransparentProxyRouteTextSeed("当前接口", ProxyBaseUrl, ProxyModel, ProxyApiKey));
        }

        foreach (var row in ProxyBatchRankingRows
                     .Where(static item => item.IsSelected || item.Rank is > 0 and <= 3)
                     .OrderBy(static item => item.Rank <= 0 ? int.MaxValue : item.Rank))
        {
            seeds.Add(new TransparentProxyRouteTextSeed(
                string.IsNullOrWhiteSpace(row.EntryName) ? $"候选 #{row.Rank}" : row.EntryName,
                row.BaseUrl,
                row.Model,
                row.ApiKey));
        }

        foreach (var item in ProxyBatchEditorItems.Where(static item => item.IncludeInBatchTest))
        {
            var apiKey = string.IsNullOrWhiteSpace(item.EntryApiKey) ? item.SiteGroupApiKey : item.EntryApiKey;
            var model = string.IsNullOrWhiteSpace(item.EntryModel) ? item.SiteGroupModel : item.EntryModel;
            seeds.Add(new TransparentProxyRouteTextSeed(
                string.IsNullOrWhiteSpace(item.EntryName) ? item.DisplayTitle : item.EntryName,
                item.BaseUrl,
                model ?? string.Empty,
                apiKey ?? string.Empty));
        }

        var distinct = seeds
            .Where(static item => Uri.TryCreate(item.BaseUrl?.Trim(), UriKind.Absolute, out _))
            .GroupBy(static item => $"{item.BaseUrl.Trim()}|{item.Model.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(12)
            .ToArray();

        return TransparentProxyRouteTextCodec.BuildRoutesTextFromSeeds(distinct);
    }

    private void RefreshTransparentProxyRouteEditorItemsFromText()
    {
        if (_isRefreshingTransparentProxyRouteEditor)
        {
            return;
        }

        _isRefreshingTransparentProxyRouteEditor = true;
        try
        {
            foreach (var existing in TransparentProxyRouteEditorItems)
            {
                DetachTransparentProxyRouteEditorItem(existing);
            }

            TransparentProxyRouteEditorItems.Clear();
            foreach (var item in TransparentProxyRouteTextCodec.ParseEditorItems(TransparentProxyRoutesText))
            {
                AttachTransparentProxyRouteEditorItem(item);
                TransparentProxyRouteEditorItems.Add(item);
            }

            SelectedTransparentProxyRouteEditorItem = TransparentProxyRouteEditorItems.FirstOrDefault();
        }
        finally
        {
            _isRefreshingTransparentProxyRouteEditor = false;
        }
    }

    private void AttachTransparentProxyRouteEditorItem(TransparentProxyRouteEditorItemViewModel item)
        => item.PropertyChanged += TransparentProxyRouteEditorItem_OnPropertyChanged;

    private void DetachTransparentProxyRouteEditorItem(TransparentProxyRouteEditorItemViewModel item)
        => item.PropertyChanged -= TransparentProxyRouteEditorItem_OnPropertyChanged;

    private void TransparentProxyRouteEditorItem_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isRefreshingTransparentProxyRouteEditor)
        {
            return;
        }

        UpdateTransparentProxyRoutesTextFromEditor();
    }

    private void UpdateTransparentProxyRoutesTextFromEditor()
    {
        if (_isRefreshingTransparentProxyRouteEditor)
        {
            return;
        }

        _isUpdatingTransparentProxyRoutesTextFromEditor = true;
        try
        {
            TransparentProxyRoutesText = TransparentProxyRouteTextCodec.BuildRoutesTextFromEditor(TransparentProxyRouteEditorItems);
        }
        finally
        {
            _isUpdatingTransparentProxyRoutesTextFromEditor = false;
        }

        RefreshTransparentProxyRoutePreview();
    }

    private void ApplyHydratedTransparentProxyRoutesToEditor(IReadOnlyList<TransparentProxyRoute> hydratedRoutes)
    {
        if (hydratedRoutes.Count == 0 || TransparentProxyRouteEditorItems.Count == 0)
        {
            return;
        }

        var byId = hydratedRoutes
            .GroupBy(static route => route.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        _isRefreshingTransparentProxyRouteEditor = true;
        try
        {
            foreach (var item in TransparentProxyRouteEditorItems)
            {
                var id = TransparentProxyRouteTextCodec.BuildRouteId(item.Name, item.BaseUrl, item.Prefix);
                if (!byId.TryGetValue(id, out var route) || route.Models.Count == 0)
                {
                    continue;
                }

                var modelsText = string.Join(Environment.NewLine, route.Models);
                if (!string.Equals(item.ModelsText, modelsText, StringComparison.Ordinal))
                {
                    item.ModelsText = modelsText;
                    changed = true;
                }
            }
        }
        finally
        {
            _isRefreshingTransparentProxyRouteEditor = false;
        }

        if (changed)
        {
            UpdateTransparentProxyRoutesTextFromEditor();
        }
    }

    private void RefreshTransparentProxyRoutePreview()
    {
        var routes = TransparentProxyRouteTextCodec.ParseRoutes(TransparentProxyRoutesText);
        var metrics = _transparentProxyService.IsRunning
            ? null
            : new TransparentProxyMetricsSnapshot(false, ParseTransparentProxyPort(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0, 0d, null);

        TransparentProxyRoutes.Clear();
        var priority = 0;
        foreach (var route in routes)
        {
            priority++;
            var viewModel = new TransparentProxyRouteViewModel(route, priority);
            viewModel.ApplyMetrics(metrics?.Routes.FirstOrDefault(item => string.Equals(item.Id, route.Id, StringComparison.OrdinalIgnoreCase)));
            if (_transparentProxyProtocolSnapshots.TryGetValue(route.Id, out var protocol))
            {
                viewModel.ApplyProtocol(
                    protocol.PreferredWireApi,
                    protocol.ChatCompletionsSupported,
                    protocol.ResponsesSupported,
                    protocol.AnthropicMessagesSupported,
                    protocol.CheckedAt);
            }

            TransparentProxyRoutes.Add(viewModel);
        }

        TransparentProxyRoutingSummary = routes.Count == 0
            ? "尚未配置上游路由。每行格式：名称 | Base URL | 模型 | API Key。"
            : $"已配置 {routes.Count} 路上游，自动选路会综合配置顺序、熔断、错误率和延迟。";
    }

    private void OnTransparentProxyLogEmitted(object? sender, TransparentProxyLogEntry entry)
    {
        _ = _transparentProxyLogStore.AppendAsync(entry);
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _allTransparentProxyLogs.Insert(0, new TransparentProxyLogEntryViewModel(entry));
            while (_allTransparentProxyLogs.Count > 500)
            {
                _allTransparentProxyLogs.RemoveAt(_allTransparentProxyLogs.Count - 1);
            }

            RefreshTransparentProxyLogView();
        });
    }

    private async Task LoadTransparentProxyLogsAsync()
    {
        try
        {
            var entries = await _transparentProxyLogStore.LoadRecentAsync(500);
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _allTransparentProxyLogs.Clear();
                foreach (var entry in entries)
                {
                    _allTransparentProxyLogs.Add(new TransparentProxyLogEntryViewModel(entry));
                }

                RefreshTransparentProxyLogView();
            });
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxy.LoadLogs", ex);
        }
    }

    private void RefreshTransparentProxyLogView()
    {
        var filter = TransparentProxyLogFilterText.Trim();
        var selectedRequestId = SelectedTransparentProxyLog?.RequestId;
        var rows = _allTransparentProxyLogs
            .Where(row => MatchesTransparentProxyLogFilter(row, filter))
            .Take(300)
            .ToArray();

        TransparentProxyLogs.Clear();
        foreach (var row in rows)
        {
            TransparentProxyLogs.Add(row);
        }

        if (!string.IsNullOrWhiteSpace(selectedRequestId))
        {
            SelectedTransparentProxyLog = TransparentProxyLogs.FirstOrDefault(row => string.Equals(row.RequestId, selectedRequestId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool MatchesTransparentProxyLogFilter(TransparentProxyLogEntryViewModel row, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return Contains(row.Level, filter) ||
               Contains(row.Method, filter) ||
               Contains(row.Path, filter) ||
               Contains(row.ModelName, filter) ||
               Contains(row.RouteName, filter) ||
               Contains(row.Message, filter) ||
               Contains(row.RequestId, filter) ||
               Contains(row.WireApi, filter) ||
               Contains(row.AttemptSummary, filter);

        static bool Contains(string value, string keyword)
            => value?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void OnTransparentProxyMetricsChanged(object? sender, TransparentProxyMetricsSnapshot snapshot)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            IsTransparentProxyRunning = snapshot.IsRunning;
            TransparentProxyTotalRequestsText = snapshot.TotalRequests.ToString(CultureInfo.InvariantCulture);
            TransparentProxySuccessRateText = snapshot.TotalRequests <= 0
                ? "-"
                : $"{snapshot.SuccessRequests * 100d / snapshot.TotalRequests:0.#}%";
            TransparentProxyActiveRequestsText = snapshot.ActiveRequests.ToString(CultureInfo.InvariantCulture);
            TransparentProxyFallbackRequestsText = snapshot.FallbackRequests.ToString(CultureInfo.InvariantCulture);
            TransparentProxyCacheHitsText = snapshot.CacheHits.ToString(CultureInfo.InvariantCulture);
            TransparentProxyP95LatencyText = snapshot.P95LatencyMs <= 0
                ? "-"
                : $"{snapshot.P95LatencyMs.ToString(CultureInfo.InvariantCulture)} ms";
            _transparentProxyLatestTotalOutputTokens = snapshot.TotalOutputTokens;
            _transparentProxyLatestTokensPerSecond = snapshot.TokensPerSecond;
            _transparentProxyLatestTokenActivityAt = snapshot.LastTokenActivityAt;
            _transparentProxyLatestIsRunning = snapshot.IsRunning;
            TransparentProxyTotalTokensText = $"{FormatCompactTokenCount(snapshot.TotalOutputTokens)} tokens";
            TransparentProxyTokensPerSecondText = FormatTokensPerSecond(snapshot.TokensPerSecond);
            UpdateTransparentProxyTokenMeter(
                snapshot.IsRunning,
                snapshot.TotalOutputTokens,
                snapshot.TokensPerSecond,
                snapshot.LastTokenActivityAt);
            TransparentProxyMetricsSummary =
                $"请求 {snapshot.TotalRequests}，成功 {snapshot.SuccessRequests}，失败 {snapshot.FailedRequests}，fallback {snapshot.FallbackRequests}，缓存 {snapshot.CacheHits}，限速 {snapshot.RateLimitedRequests}，P50 {snapshot.P50LatencyMs} ms，P95 {snapshot.P95LatencyMs} ms，输出 {FormatCompactTokenCount(snapshot.TotalOutputTokens)} tokens。";
            TransparentProxyMetricsSummary = $"Requests {snapshot.TotalRequests}, success {snapshot.SuccessRequests}, failed {snapshot.FailedRequests}, fallback {snapshot.FallbackRequests}, local cache {snapshot.CacheHits}, cache entries {snapshot.ResponseCacheEntryCount}/{snapshot.ModelListCacheEntryCount}, prompt sessions {snapshot.PromptSessionCacheEntryCount}, evicted {snapshot.ResponseCacheEvictions}, upstream prompt cache {FormatCompactTokenCount(snapshot.PromptCacheTokens)}, rate limited {snapshot.RateLimitedRequests}, P50 {snapshot.P50LatencyMs} ms, P95 {snapshot.P95LatencyMs} ms, output {FormatCompactTokenCount(snapshot.TotalOutputTokens)} tokens.";
            TransparentProxyStatusSummary = snapshot.IsRunning
                ? $"运行中：{TransparentProxyLocalEndpoint}，活跃 {snapshot.ActiveRequests}，缓存条目 {snapshot.CacheEntryCount}。"
                : "本地透明代理未启动。";
            TransparentProxyHealthSummary = snapshot.IsRunning
                ? $"健康检查：{TransparentProxyHealthEndpoint}"
                : "健康检查：未监听。";

            var byId = snapshot.Routes.ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var route in TransparentProxyRoutes)
            {
                byId.TryGetValue(route.Id, out var metrics);
                route.ApplyMetrics(metrics);
            }
        });
    }

    public void RefreshTransparentProxyTokenMeterIdleState()
        => UpdateTransparentProxyTokenMeter(
            _transparentProxyLatestIsRunning,
            _transparentProxyLatestTotalOutputTokens,
            _transparentProxyLatestTokensPerSecond,
            _transparentProxyLatestTokenActivityAt);

    public void ResetTransparentProxyTokenMeter()
    {
        _transparentProxyService.ResetTokenTelemetry();
        _transparentProxyLatestTotalOutputTokens = 0;
        _transparentProxyLatestTokensPerSecond = 0d;
        _transparentProxyLatestTokenActivityAt = null;
        TransparentProxyTotalTokensText = "0 tokens";
        TransparentProxyTokensPerSecondText = "0 tok/s";
        UpdateTransparentProxyTokenMeter(_transparentProxyLatestIsRunning, 0, 0d, null);
        TransparentProxyStatusSummary = _transparentProxyLatestIsRunning
            ? $"Token 阶段计数已重置：{TransparentProxyLocalEndpoint}"
            : "Token 阶段计数已重置。";
    }

    private void UpdateTransparentProxyTokenMeter(
        bool isRunning,
        long totalOutputTokens,
        double tokensPerSecond,
        DateTimeOffset? lastTokenActivityAt)
    {
        var now = DateTimeOffset.UtcNow;
        var isStreaming = tokensPerSecond >= 0.5d &&
                          lastTokenActivityAt is not null &&
                          (now - lastTokenActivityAt.Value).TotalSeconds <= 5d;
        if (isStreaming)
        {
            TransparentProxyTokenMeterPrimaryText = FormatTokensPerSecond(tokensPerSecond);
            TransparentProxyTokenMeterSecondaryText = ResolveTransparentProxyTokenMeterSecondaryText(
                now,
                isRunning,
                totalOutputTokens,
                tokensPerSecond,
                isStreaming);
            TransparentProxyTokenMeterAccentBrush = "#0E9F6E";
            return;
        }

        TransparentProxyTokenMeterPrimaryText = $"{FormatCompactTokenCount(totalOutputTokens)} tokens";
        TransparentProxyTokenMeterSecondaryText = ResolveTransparentProxyTokenMeterSecondaryText(
            now,
            isRunning,
            totalOutputTokens,
            tokensPerSecond,
            isStreaming: false);
        TransparentProxyTokenMeterAccentBrush = isRunning ? "#64748B" : "#94A3B8";
    }

    private static string ResolveTransparentProxyTokenMeterSecondaryText(
        DateTimeOffset now,
        bool isRunning,
        long totalOutputTokens,
        double tokensPerSecond,
        bool isStreaming)
    {
        var phase = (now.ToUnixTimeSeconds() / 4) % 3;
        if (isStreaming)
        {
            return phase switch
            {
                1 => "实时输出中",
                2 => $"累计 {FormatCompactTokenCount(totalOutputTokens)}",
                _ => "tokens / sec"
            };
        }

        if (!isRunning)
        {
            return "等待启动";
        }

        return phase switch
        {
            1 => FormatTokensPerSecond(tokensPerSecond),
            2 => "等待新请求",
            _ => "本阶段累计"
        };
    }

    private static string FormatTokensPerSecond(double value)
        => value <= 0.05d
            ? "0 tok/s"
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} tok/s";

    private static string FormatCompactTokenCount(long value)
    {
        if (value >= 1_000_000)
        {
            return $"{(value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)}m";
        }

        if (value >= 10_000)
        {
            return $"{(value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture)}k";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static ProxyEndpointProtocolProbeResult BuildTransparentProxyClientApplyProbeResult(
        IReadOnlyList<TransparentProxyRoute> routes,
        ProxyEndpointSettings settings)
    {
        var responsesSupported = routes.Any(static route => route.ResponsesSupported != false);
        var anthropicSupported = routes.Any(static route => route.AnthropicMessagesSupported != false);
        var chatSupported = routes.Any(static route => route.ChatCompletionsSupported != false);
        var preferred = responsesSupported
            ? ProxyWireApiProbeService.ResponsesWireApi
            : anthropicSupported
                ? ProxyWireApiProbeService.AnthropicMessagesWireApi
                : ProxyWireApiProbeService.ChatCompletionsWireApi;

        return new ProxyEndpointProtocolProbeResult(
            DateTimeOffset.Now,
            settings.BaseUrl,
            settings.Model,
            chatSupported,
            responsesSupported,
            anthropicSupported,
            preferred,
            "RelayBench 本地透明代理会在本机接管请求，并继续由路由队列负责自动选路、fallback、缓存、限速和脱敏日志。",
            null);
    }

    private static string ResolveTransparentProxyClientDefaultModel(IReadOnlyList<TransparentProxyRoute> routes)
    {
        foreach (var route in routes
                     .OrderBy(static item => item.Priority > 0 ? item.Priority : int.MaxValue)
                     .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var mapping in route.ModelMappings)
            {
                var model = mapping.EffectiveAlias;
                if (string.IsNullOrWhiteSpace(model))
                {
                    continue;
                }

                var prefix = route.Prefix.Trim().Trim('/');
                return string.IsNullOrWhiteSpace(prefix)
                    ? model.Trim()
                    : $"{prefix}/{model.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(route.Model))
            {
                return route.Model.Trim();
            }
        }

        return "relaybench-auto";
    }

    private string ResolveTransparentProxyClientApiKey(IReadOnlyList<TransparentProxyRoute> routes)
    {
        var routeKeys = routes
            .Select(static route => route.ApiKey?.Trim() ?? string.Empty)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (routeKeys.Length > 0)
        {
            return "relaybench-local-proxy";
        }

        if (!string.IsNullOrWhiteSpace(ProxyApiKey))
        {
            return ProxyApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ApplicationCenterApiKey))
        {
            return ApplicationCenterApiKey.Trim();
        }

        return "relaybench-local-proxy";
    }

    private int ParseTransparentProxyPort()
        => ParseBoundedInt(TransparentProxyPortText, fallback: 17880, min: 1024, max: 65535);

    private void NotifyTransparentProxyEndpointChanged()
    {
        OnPropertyChanged(nameof(TransparentProxyLocalEndpoint));
        OnPropertyChanged(nameof(TransparentProxyHealthEndpoint));
        OnPropertyChanged(nameof(TransparentProxyStatusSummary));
    }
}
