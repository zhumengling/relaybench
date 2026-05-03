using System.Globalization;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
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

    public bool TransparentProxyRewriteModel
    {
        get => _transparentProxyRewriteModel;
        set => SetProperty(ref _transparentProxyRewriteModel, value);
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
        var routes = ParseTransparentProxyRoutes(TransparentProxyRoutesText);
        if (routes.Count == 0)
        {
            TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
            routes = ParseTransparentProxyRoutes(TransparentProxyRoutesText);
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
            TransparentProxyRewriteModel,
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 300));

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
            Model = ProxyModel,
            ApiKey = string.Empty
        };
        AttachTransparentProxyRouteEditorItem(item);
        TransparentProxyRouteEditorItems.Add(item);
        SelectedTransparentProxyRouteEditorItem = item;
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
                var routes = ParseTransparentProxyRoutes(TransparentProxyRoutesText);
                if (routes.Count == 0)
                {
                    TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
                    routes = ParseTransparentProxyRoutes(TransparentProxyRoutesText);
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
        TransparentProxyLogs.Clear();
        return Task.CompletedTask;
    }

    private void LoadTransparentProxyState(AppStateSnapshot snapshot)
    {
        TransparentProxyPortText = string.IsNullOrWhiteSpace(snapshot.TransparentProxyPortText) ? "17880" : snapshot.TransparentProxyPortText;
        TransparentProxyRoutesText = snapshot.TransparentProxyRoutesText ?? string.Empty;
        TransparentProxyRateLimitPerMinuteText = string.IsNullOrWhiteSpace(snapshot.TransparentProxyRateLimitPerMinuteText) ? "60" : snapshot.TransparentProxyRateLimitPerMinuteText;
        TransparentProxyMaxConcurrencyText = string.IsNullOrWhiteSpace(snapshot.TransparentProxyMaxConcurrencyText) ? "8" : snapshot.TransparentProxyMaxConcurrencyText;
        TransparentProxyEnableFallback = snapshot.TransparentProxyEnableFallback;
        TransparentProxyEnableCache = snapshot.TransparentProxyEnableCache;
        TransparentProxyCacheTtlSecondsText = string.IsNullOrWhiteSpace(snapshot.TransparentProxyCacheTtlSecondsText) ? "60" : snapshot.TransparentProxyCacheTtlSecondsText;
        TransparentProxyRewriteModel = snapshot.TransparentProxyRewriteModel;

        if (string.IsNullOrWhiteSpace(TransparentProxyRoutesText))
        {
            TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromWorkspace();
        }

        RefreshTransparentProxyRouteEditorItemsFromText();
        RefreshTransparentProxyRoutePreview();
    }

    private void ApplyTransparentProxyStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.TransparentProxyPortText = TransparentProxyPortText;
        snapshot.TransparentProxyRoutesText = TransparentProxyRoutesText;
        snapshot.TransparentProxyRateLimitPerMinuteText = TransparentProxyRateLimitPerMinuteText;
        snapshot.TransparentProxyMaxConcurrencyText = TransparentProxyMaxConcurrencyText;
        snapshot.TransparentProxyEnableFallback = TransparentProxyEnableFallback;
        snapshot.TransparentProxyEnableCache = TransparentProxyEnableCache;
        snapshot.TransparentProxyCacheTtlSecondsText = TransparentProxyCacheTtlSecondsText;
        snapshot.TransparentProxyRewriteModel = TransparentProxyRewriteModel;
    }

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

        List<TransparentProxyRoute> hydratedRoutes = new(routes.Count);
        var routeIndex = 0;
        var probedModels = 0;
        var cachedModels = 0;
        var skippedRoutes = 0;
        foreach (var route in routes)
        {
            routeIndex++;
            if (IsGlobalTaskProgressVisible)
            {
                UpdateGlobalTaskProgress(
                    routeIndex,
                    routes.Count,
                    fetchCatalogModels ? $"探测 {route.Name}" : "读取协议缓存");
            }

            var modelNames = fetchCatalogModels
                ? await FetchTransparentProxyRouteModelsAsync(route, cancellationToken)
                : BuildTransparentProxyRouteProbeModels(route);

            TransparentProxyProtocolSnapshot? routeSnapshot = null;
            foreach (var model in modelNames)
            {
                var snapshot = await ResolveTransparentProxyModelProtocolAsync(
                    route,
                    model,
                    forceProbe,
                    cancellationToken);
                if (snapshot is null)
                {
                    continue;
                }

                if (snapshot.WasProbed)
                {
                    probedModels++;
                }
                else
                {
                    cachedModels++;
                }

                if (string.Equals(model, route.Model, StringComparison.OrdinalIgnoreCase) || routeSnapshot is null)
                {
                    routeSnapshot = snapshot;
                }
            }

            if (routeSnapshot is null)
            {
                skippedRoutes++;
                hydratedRoutes.Add(route);
                continue;
            }

            _transparentProxyProtocolSnapshots[route.Id] = routeSnapshot;
            hydratedRoutes.Add(route.WithProtocol(
                routeSnapshot.PreferredWireApi,
                routeSnapshot.ChatCompletionsSupported,
                routeSnapshot.ResponsesSupported,
                routeSnapshot.AnthropicMessagesSupported,
                routeSnapshot.CheckedAt));
        }

        TransparentProxyProtocolSummary =
            $"协议探测：写入/刷新 {probedModels} 个模型，命中缓存 {cachedModels} 个，跳过 {skippedRoutes} 条路由。优先级 Responses → Anthropic → OpenAI Chat。";
        return hydratedRoutes;
    }

    private async Task<IReadOnlyList<string>> FetchTransparentProxyRouteModelsAsync(
        TransparentProxyRoute route,
        CancellationToken cancellationToken)
    {
        var fallbackModels = BuildTransparentProxyRouteProbeModels(route);
        if (string.IsNullOrWhiteSpace(route.ApiKey))
        {
            return fallbackModels;
        }

        var settings = BuildTransparentProxyEndpointSettings(route, route.Model);
        var catalog = await _proxyDiagnosticsService.FetchModelsAsync(settings, cancellationToken);
        await CacheProxyModelCatalogResultAsync(settings, catalog, cancellationToken);
        if (!catalog.Success)
        {
            return fallbackModels;
        }

        var models = catalog.ModelItems is { Count: > 0 }
            ? catalog.ModelItems.Select(static item => item.Id)
            : catalog.Models;
        return models
            .Append(route.Model)
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildTransparentProxyRouteProbeModels(TransparentProxyRoute route)
        => string.IsNullOrWhiteSpace(route.Model)
            ? Array.Empty<string>()
            : [route.Model.Trim()];

    private async Task<TransparentProxyProtocolSnapshot?> ResolveTransparentProxyModelProtocolAsync(
        TransparentProxyRoute route,
        string model,
        bool forceProbe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(route.ApiKey) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        if (!forceProbe)
        {
            var cached = await _proxyEndpointModelCacheService.TryResolveAsync(
                route.BaseUrl,
                route.ApiKey,
                model,
                cancellationToken);
            if (cached is not null &&
                (cached.ChatCompletionsSupported.HasValue ||
                 cached.ResponsesSupported.HasValue ||
                 cached.AnthropicMessagesSupported.HasValue))
            {
                return new TransparentProxyProtocolSnapshot(
                    cached.PreferredWireApi,
                    cached.ChatCompletionsSupported,
                    cached.ResponsesSupported,
                    cached.AnthropicMessagesSupported,
                    cached.CheckedAt,
                    WasProbed: false);
            }
        }

        var settings = BuildTransparentProxyEndpointSettings(route, model);
        var result = await _proxyDiagnosticsService.ProbeProtocolAsync(settings, cancellationToken);
        await _proxyEndpointModelCacheService.SaveProtocolProbeAsync(settings, result, cancellationToken);
        return new TransparentProxyProtocolSnapshot(
            result.PreferredWireApi,
            result.ChatCompletionsSupported,
            result.ResponsesSupported,
            result.AnthropicMessagesSupported,
            result.CheckedAt,
            WasProbed: true);
    }

    private ProxyEndpointSettings BuildTransparentProxyEndpointSettings(TransparentProxyRoute route, string model)
        => new(
            route.BaseUrl,
            route.ApiKey,
            model,
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 300));

    private string BuildTransparentProxyRoutesTextFromWorkspace()
    {
        List<TransparentProxyRouteSeed> seeds = [];

        if (!string.IsNullOrWhiteSpace(ProxyBaseUrl))
        {
            seeds.Add(new TransparentProxyRouteSeed("当前接口", ProxyBaseUrl, ProxyModel, ProxyApiKey));
        }

        foreach (var row in ProxyBatchRankingRows
                     .Where(static item => item.IsSelected || item.Rank is > 0 and <= 3)
                     .OrderBy(static item => item.Rank <= 0 ? int.MaxValue : item.Rank))
        {
            seeds.Add(new TransparentProxyRouteSeed(
                string.IsNullOrWhiteSpace(row.EntryName) ? $"候选 #{row.Rank}" : row.EntryName,
                row.BaseUrl,
                row.Model,
                row.ApiKey));
        }

        foreach (var item in ProxyBatchEditorItems.Where(static item => item.IncludeInBatchTest))
        {
            var apiKey = string.IsNullOrWhiteSpace(item.EntryApiKey) ? item.SiteGroupApiKey : item.EntryApiKey;
            var model = string.IsNullOrWhiteSpace(item.EntryModel) ? item.SiteGroupModel : item.EntryModel;
            seeds.Add(new TransparentProxyRouteSeed(
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

        return string.Join(
            Environment.NewLine,
            distinct.Select(static item => $"{EscapeRouteField(item.Name)} | {EscapeRouteField(item.BaseUrl)} | {EscapeRouteField(item.Model)} | {EscapeRouteField(item.ApiKey)}"));
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
            foreach (var item in ParseTransparentProxyRouteEditorItems(TransparentProxyRoutesText))
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
            TransparentProxyRoutesText = BuildTransparentProxyRoutesTextFromEditor();
        }
        finally
        {
            _isUpdatingTransparentProxyRoutesTextFromEditor = false;
        }

        RefreshTransparentProxyRoutePreview();
    }

    private string BuildTransparentProxyRoutesTextFromEditor()
        => string.Join(
            Environment.NewLine,
            TransparentProxyRouteEditorItems.Select(static item =>
            {
                var prefix = item.IsEnabled ? string.Empty : "# ";
                return $"{prefix}{EscapeRouteField(item.Name)} | {EscapeRouteField(item.BaseUrl)} | {EscapeRouteField(item.Model)} | {EscapeRouteField(item.ApiKey)}";
            }));

    private static IReadOnlyList<TransparentProxyRouteEditorItemViewModel> ParseTransparentProxyRouteEditorItems(string text)
    {
        List<TransparentProxyRouteEditorItemViewModel> items = [];
        var lines = (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var isEnabled = true;
            if (line.StartsWith('#'))
            {
                isEnabled = false;
                line = line[1..].Trim();
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('|').Select(static item => item.Trim()).ToArray();
            string name;
            string baseUrl;
            string model;
            string apiKey;
            if (parts.Length >= 4)
            {
                name = parts[0];
                baseUrl = parts[1];
                model = parts[2];
                apiKey = parts[3];
            }
            else if (parts.Length == 3)
            {
                name = parts[0];
                baseUrl = parts[1];
                model = parts[2];
                apiKey = string.Empty;
            }
            else if (parts.Length == 2)
            {
                name = $"路由 {items.Count + 1}";
                baseUrl = parts[0];
                model = parts[1];
                apiKey = string.Empty;
            }
            else
            {
                continue;
            }

            items.Add(new TransparentProxyRouteEditorItemViewModel
            {
                IsEnabled = isEnabled,
                Name = string.IsNullOrWhiteSpace(name) ? $"路由 {items.Count + 1}" : name,
                BaseUrl = baseUrl,
                Model = model,
                ApiKey = apiKey
            });
        }

        return items;
    }

    private void RefreshTransparentProxyRoutePreview()
    {
        var routes = ParseTransparentProxyRoutes(TransparentProxyRoutesText);
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

    private IReadOnlyList<TransparentProxyRoute> ParseTransparentProxyRoutes(string text)
    {
        List<TransparentProxyRoute> routes = [];
        var lines = (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|').Select(static item => item.Trim()).ToArray();
            string name;
            string baseUrl;
            string model;
            string apiKey;
            if (parts.Length >= 4)
            {
                name = parts[0];
                baseUrl = parts[1];
                model = parts[2];
                apiKey = parts[3];
            }
            else if (parts.Length == 3)
            {
                name = parts[0];
                baseUrl = parts[1];
                model = parts[2];
                apiKey = string.Empty;
            }
            else if (parts.Length == 2)
            {
                name = $"路由 {routes.Count + 1}";
                baseUrl = parts[0];
                model = parts[1];
                apiKey = string.Empty;
            }
            else
            {
                continue;
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            {
                continue;
            }

            name = string.IsNullOrWhiteSpace(name) ? $"路由 {routes.Count + 1}" : name;
            routes.Add(new TransparentProxyRoute(
                BuildTransparentProxyRouteId(name, baseUrl, model),
                name,
                baseUrl.Trim(),
                apiKey.Trim(),
                model.Trim()));
        }

        return routes;
    }

    private void OnTransparentProxyLogEmitted(object? sender, TransparentProxyLogEntry entry)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            TransparentProxyLogs.Insert(0, new TransparentProxyLogEntryViewModel(entry));
            while (TransparentProxyLogs.Count > 200)
            {
                TransparentProxyLogs.RemoveAt(TransparentProxyLogs.Count - 1);
            }
        });
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

    private int ParseTransparentProxyPort()
        => ParseBoundedInt(TransparentProxyPortText, fallback: 17880, min: 1024, max: 65535);

    private void NotifyTransparentProxyEndpointChanged()
    {
        OnPropertyChanged(nameof(TransparentProxyLocalEndpoint));
        OnPropertyChanged(nameof(TransparentProxyHealthEndpoint));
        OnPropertyChanged(nameof(TransparentProxyStatusSummary));
    }

    private static string EscapeRouteField(string value)
        => (value ?? string.Empty)
            .Replace("|", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static string BuildTransparentProxyRouteId(string name, string baseUrl, string model)
    {
        var input = $"{name}|{baseUrl}|{model}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash[..8]);
    }

    private sealed record TransparentProxyRouteSeed(string Name, string BaseUrl, string Model, string ApiKey);

    private sealed record TransparentProxyProtocolSnapshot(
        string? PreferredWireApi,
        bool? ChatCompletionsSupported,
        bool? ResponsesSupported,
        bool? AnthropicMessagesSupported,
        DateTimeOffset CheckedAt,
        bool WasProbed);
}
