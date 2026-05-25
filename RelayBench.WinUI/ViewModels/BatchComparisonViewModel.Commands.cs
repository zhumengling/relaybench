using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Security.Cryptography;
using System.Text;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class BatchComparisonViewModel
{
    private void OnSitePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchSiteEntry.IsIncluded))
            RefreshSelectionSummary();
    }

    /// <summary>
    /// Notifies the UI that the selection summary has changed.
    /// </summary>
    private void RefreshSelectionSummary()
    {
        OnPropertyChanged(nameof(SelectionSummary));
    }

    [RelayCommand]
    private void UseQuickRunMode()
    {
        if (!IsRunning)
        {
            SelectedRunMode = BatchRunMode.Quick;
            NotifyRunModeProperties();
        }
    }

    [RelayCommand]
    private void UseDeepRunMode()
    {
        if (!IsRunning)
        {
            SelectedRunMode = BatchRunMode.Deep;
            NotifyRunModeProperties();
        }
    }

    [RelayCommand]
    private void OpenProxyBatchEditor()
    {
        StatusText = SiteEditor.Sites.Count == 0
            ? "已打开入口组维护，可先带入当前入口或粘贴接口。"
            : "已打开入口组维护，可继续编辑站点组。";
        ProxyBatchEditorOpenRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CloseProxyBatchEditor()
    {
        StatusText = "已关闭入口组维护。";
        ProxyBatchEditorCloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddCurrentProxyBaseUrlToBatch()
        => AddCurrentEndpoint();

    [RelayCommand]
    private async Task RunProxyBatchAsync()
        => await StartBatchAsync();

    [RelayCommand]
    private async Task RunSelectedBatchDeepTestsAsync()
    {
        if (!IsRunning)
        {
            SelectedRunMode = BatchRunMode.Deep;
        }

        await StartBatchAsync();
    }

    [RelayCommand]
    private void ToggleBatchDeepSelection()
    {
        if (SiteEditor.Sites.Count == 0)
        {
            StatusText = "入口组为空，暂无可切换的深度测试候选。";
            return;
        }

        var includeAll = SiteEditor.Sites.Any(static site => !site.IsIncluded);
        foreach (var site in SiteEditor.Sites)
        {
            site.IsIncluded = includeAll;
        }

        RefreshSelectionSummary();
        StatusText = includeAll
            ? $"已将 {SiteEditor.Sites.Count} 个入口全部加入深度测试候选。"
            : $"已将 {SiteEditor.Sites.Count} 个入口全部从深度测试候选中移除。";
    }

    [RelayCommand]
    private async Task FetchProxyBatchSharedModelsAsync()
    {
        if (SiteEditor.SelectedSite is { } selectedSite)
        {
            await FetchModelsForSiteAsync(selectedSite);
            return;
        }

        if (SiteEditor.SelectedDraftRow is { } selectedDraftRow)
        {
            await FetchModelsForDraftRowAsync(selectedDraftRow);
            return;
        }

        if (SiteEditor.Sites.FirstOrDefault(static site => !string.IsNullOrWhiteSpace(site.BaseUrl)) is { } firstSite)
        {
            await FetchModelsForSiteAsync(firstSite);
            return;
        }

        StatusText = "请先在入口组中选择或填写一个Site，再拉取共享模型。";
    }

    [RelayCommand]
    private async Task FetchProxyBatchEntryModelsAsync()
        => await FetchModelsForSiteAsync(SiteEditor.SelectedSite);

    [RelayCommand]
    private async Task FetchProxyBatchTemplateRowModelsAsync(BatchSiteDraftRow? row)
        => await FetchModelsForDraftRowAsync(row ?? SiteEditor.SelectedDraftRow);

    [RelayCommand]
    private async Task ApplyRankingRowToCodexAppsAsync(SiteRankEntry? entry)
        => await ApplyRankingEntryAsync(entry);

    [RelayCommand]
    private void OpenBatchComparisonChart()
    {
        StatusText = HasLatencyChart || HasThroughputChart
            ? "批量对比图表已在当前页面实时展示。"
            : "当前还没有批量对比图表，请先运行批量评测。";
    }

    [RelayCommand]
    private void OpenBatchDeepComparisonChart()
    {
        StatusText = DeepTestQueue.Count > 0
            ? "深度对比结果已在当前页面的执行队列中展示。"
            : "当前还没有深度对比结果，请先运行深度测试。";
    }

    /// <summary>
    /// Loads persisted endpoint values from the shared endpoint store.
    /// </summary>
    private void LoadPersistedEndpoint()
    {
        try
        {
            var shared = _sharedEndpointLoader();
            if (shared is not null && !string.IsNullOrWhiteSpace(shared.BaseUrl))
            {
                BaseUrl = shared.BaseUrl;
                ApiKey = shared.ApiKey;
                Model = shared.Model;
            }
        }
        catch
        {
            // Best-effort
        }
    }

    /// <summary>
    /// Sort the site rankings by the specified column. Toggles direction if same column clicked again.
    /// </summary>
    [RelayCommand]
    private void Sort(string column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            // Default to descending for numeric columns, ascending for text
            SortAscending = column == "SiteName";
        }

        ApplySort();
        OnPropertyChanged(nameof(RankSortIndicator));
        OnPropertyChanged(nameof(SiteSortIndicator));
        OnPropertyChanged(nameof(P50SortIndicator));
        OnPropertyChanged(nameof(ThroughputSortIndicator));
        OnPropertyChanged(nameof(SuccessSortIndicator));
        OnPropertyChanged(nameof(ScoreSortIndicator));
    }

    private void ApplySort()
    {
        var sorted = SortColumn switch
        {
            "Rank" => SortAscending
                ? SiteRankings.OrderBy(x => x.Rank).ToList()
                : SiteRankings.OrderByDescending(x => x.Rank).ToList(),
            "SiteName" => SortAscending
                ? SiteRankings.OrderBy(x => x.SiteName, StringComparer.OrdinalIgnoreCase).ToList()
                : SiteRankings.OrderByDescending(x => x.SiteName, StringComparer.OrdinalIgnoreCase).ToList(),
            "LatencyP50" => SortAscending
                ? SiteRankings.OrderBy(x => x.LatencyP50).ToList()
                : SiteRankings.OrderByDescending(x => x.LatencyP50).ToList(),
            "Throughput" => SortAscending
                ? SiteRankings.OrderBy(x => x.Throughput).ToList()
                : SiteRankings.OrderByDescending(x => x.Throughput).ToList(),
            "SuccessRate" => SortAscending
                ? SiteRankings.OrderBy(x => x.SuccessRate).ToList()
                : SiteRankings.OrderByDescending(x => x.SuccessRate).ToList(),
            "CompositeScore" => SortAscending
                ? SiteRankings.OrderBy(x => x.CompositeScore).ToList()
                : SiteRankings.OrderByDescending(x => x.CompositeScore).ToList(),
            _ => SiteRankings.OrderByDescending(x => x.CompositeScore).ToList()
        };

        SiteRankings.Clear();
        int rank = 1;
        foreach (var entry in sorted)
        {
            entry.Rank = rank++;
            SiteRankings.Add(entry);
        }
    }

    partial void OnSortColumnChanged(string value)
    {
        OnPropertyChanged(nameof(RankSortIndicator));
        OnPropertyChanged(nameof(SiteSortIndicator));
        OnPropertyChanged(nameof(P50SortIndicator));
        OnPropertyChanged(nameof(ThroughputSortIndicator));
        OnPropertyChanged(nameof(SuccessSortIndicator));
        OnPropertyChanged(nameof(ScoreSortIndicator));
    }

    partial void OnSortAscendingChanged(bool value)
    {
        OnPropertyChanged(nameof(RankSortIndicator));
        OnPropertyChanged(nameof(SiteSortIndicator));
        OnPropertyChanged(nameof(P50SortIndicator));
        OnPropertyChanged(nameof(ThroughputSortIndicator));
        OnPropertyChanged(nameof(SuccessSortIndicator));
        OnPropertyChanged(nameof(ScoreSortIndicator));
    }

    [RelayCommand]
    private void AddCurrentEndpoint()
    {
        var shared = _sharedEndpointLoader();
        var baseUrl = FirstNonEmpty(BaseUrl, shared?.BaseUrl);
        var apiKey = FirstNonEmpty(ApiKey, shared?.ApiKey);
        var model = FirstNonEmpty(Model, shared?.Model);

        if (NormalizeCandidateBaseUrl(baseUrl) is not { } normalizedUrl)
        {
            StatusText = "当前入口地址无效，无法加入入口组。";
            return;
        }

        var entry = new BatchSiteEntry(
            normalizedUrl,
            apiKey ?? string.Empty,
            model ?? string.Empty,
            groupName: "当前入口",
            name: TryGetHost(normalizedUrl) ?? "当前入口");
        var added = SiteEditor.AddGeneratedCandidates([entry]);
        RefreshSelectionSummary();
        StatusText = added > 0
            ? "已把当前入口加入入口组。"
            : "入口组已包含当前接口地址。";
    }

    [RelayCommand]
    private async Task GenerateCandidatesAsync()
    {
        try
        {
            List<BatchSiteEntry> candidates = [];

            AddCandidate(candidates, BaseUrl, ApiKey, Model, "当前输入");

            var shared = _sharedEndpointLoader();
            if (shared is not null)
            {
                AddCandidate(candidates, shared.BaseUrl, shared.ApiKey, shared.Model, "最近使用");
            }

            var history = await _endpointHistoryLoader(CancellationToken.None);
            foreach (var item in history.OrderByDescending(static item => item.UsedAt))
            {
                var model = !string.IsNullOrWhiteSpace(item.Model)
                    ? item.Model
                    : item.Models?.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                AddCandidate(candidates, item.BaseUrl, item.ApiKey, model, "History接口");
            }

            var added = SiteEditor.AddGeneratedCandidates(candidates);
            RefreshSelectionSummary();
            StatusText = added > 0
                ? $"已生成 {added} 条候选入口，可直接勾选后开始批量评测。"
                : "没有发现新的候选入口；当前列表已经包含最近使用的接口。";
        }
        catch (Exception ex)
        {
            StatusText = $"生成候选失败: {ex.Message}";
        }
    }

    private static void AddCandidate(
        ICollection<BatchSiteEntry> candidates,
        string? baseUrl,
        string? apiKey,
        string? model,
        string groupName)
    {
        var normalizedUrl = NormalizeCandidateBaseUrl(baseUrl);
        if (normalizedUrl is null)
        {
            return;
        }

        var displayName = TryGetHost(normalizedUrl) ?? groupName;
        candidates.Add(new BatchSiteEntry(
            normalizedUrl,
            apiKey?.Trim() ?? string.Empty,
            model?.Trim() ?? string.Empty,
            groupName: groupName,
            name: displayName));
    }

    private static string? NormalizeCandidateBaseUrl(string? value)
        => BatchEndpointText.NormalizeBaseUrl(value);

    private static string? TryGetHost(string? value)
        => BatchEndpointText.TryGetHost(value);

    private static string DescribeDraftRow(BatchSiteDraftRow row)
        => FirstNonEmpty(row.Name, TryGetHost(row.BaseUrl), row.BaseUrl) ?? "草稿行";

    [RelayCommand]
    private async Task FetchModelsForSiteAsync(BatchSiteEntry? site)
    {
        if (site is null)
        {
            StatusText = "请先选择要拉取模型的入口行。";
            return;
        }

        var baseUrl = NormalizeCandidateBaseUrl(site.BaseUrl);
        var context = ResolveEffectiveLineContext(site);
        var apiKey = context.ApiKey;

        if (baseUrl is null || string.IsNullOrWhiteSpace(apiKey))
        {
            site.ModelCatalogSummary = "请先填写该行接口地址和 API Key";
            StatusText = site.ModelCatalogSummary;
            return;
        }

        site.IsFetchingModels = true;
        site.ModelCatalogSummary = "正在拉取模型...";
        StatusText = $"{site.DisplayName}: 正在拉取模型列表...";
        try
        {
            var settings = new ProxyEndpointSettings(
                baseUrl,
                apiKey,
                context.Model ?? string.Empty,
                IgnoreTlsErrors: site.TlsIgnore,
                TimeoutSeconds: site.Timeout);
            var result = await _modelCatalogFetcher(settings, CancellationToken.None);

            site.AvailableModels.Clear();
            foreach (var model in result.Models
                         .Where(static model => !string.IsNullOrWhiteSpace(model))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                site.AvailableModels.Add(model);
            }

            if (result.Success && site.AvailableModels.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(site.Model) ||
                    !site.AvailableModels.Contains(site.Model, StringComparer.OrdinalIgnoreCase))
                {
                    site.Model = site.AvailableModels[0];
                }

                site.ModelCatalogSummary = $"已拉取 {site.AvailableModels.Count} 个模型";
                StatusText = $"{site.DisplayName}: {site.ModelCatalogSummary}";
                await CacheModelCatalogResultAsync(settings, result);
                await _modelHistoryRecorder(
                    baseUrl,
                    apiKey,
                    site.Model,
                    site.AvailableModels.ToList());
                await StartProtocolDetectionAfterFetchAsync(
                    settings,
                    site.AvailableModels.ToList(),
                    summary =>
                    {
                        site.ModelCatalogSummary = summary;
                        StatusText = $"{site.DisplayName}: {summary}";
                    },
                    sortedModels => ReplaceAvailableModels(site.AvailableModels, sortedModels),
                    protocolSummary => site.ProtocolSummary = protocolSummary);
            }
            else
            {
                site.ModelCatalogSummary = result.Summary;
                StatusText = $"{site.DisplayName}: {result.Summary}";
            }
        }
        catch (Exception ex)
        {
            site.ModelCatalogSummary = $"拉取模型失败: {ex.Message}";
            StatusText = $"{site.DisplayName}: {site.ModelCatalogSummary}";
        }
        finally
        {
            site.IsFetchingModels = false;
        }
    }

    [RelayCommand]
    private async Task FetchModelsForDraftRowAsync(BatchSiteDraftRow? row)
    {
        row ??= SiteEditor.SelectedDraftRow;
        if (row is null)
        {
            SiteEditor.DraftStatusText = "请先点击要拉取模型的草稿行。";
            StatusText = SiteEditor.DraftStatusText;
            return;
        }

        var baseUrl = NormalizeCandidateBaseUrl(row.BaseUrl);
        var context = ResolveEffectiveDraftRowContext(row);
        var apiKey = context.ApiKey;

        if (baseUrl is null || string.IsNullOrWhiteSpace(apiKey))
        {
            row.ModelCatalogSummary = "请先填写本行接口地址和 API Key；Key 为空时会沿用上一行";
            SiteEditor.DraftStatusText = row.ModelCatalogSummary;
            StatusText = row.ModelCatalogSummary;
            return;
        }

        row.BaseUrl = baseUrl;
        row.IsFetchingModels = true;
        row.ModelCatalogSummary = "正在拉取模型...";
        SiteEditor.DraftStatusText = $"{DescribeDraftRow(row)}: 正在按本行接口拉取模型列表...";
        StatusText = SiteEditor.DraftStatusText;
        try
        {
            var settings = new ProxyEndpointSettings(
                baseUrl,
                apiKey,
                context.Model ?? string.Empty,
                IgnoreTlsErrors: false,
                TimeoutSeconds: 30);
            var result = await _modelCatalogFetcher(settings, CancellationToken.None);

            row.AvailableModels.Clear();
            foreach (var model in result.Models
                         .Where(static model => !string.IsNullOrWhiteSpace(model))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                row.AvailableModels.Add(model);
            }

            if (result.Success && row.AvailableModels.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(row.Model) ||
                    !row.AvailableModels.Contains(row.Model, StringComparer.OrdinalIgnoreCase))
                {
                    row.Model = row.AvailableModels[0];
                }

                row.ModelCatalogSummary = $"已拉取 {row.AvailableModels.Count} 个模型";
                SiteEditor.DraftStatusText = $"{DescribeDraftRow(row)}: {row.ModelCatalogSummary}，可在模型列直接下拉选择。";
                StatusText = SiteEditor.DraftStatusText;
                await CacheModelCatalogResultAsync(settings, result);
                await _modelHistoryRecorder(
                    baseUrl,
                    apiKey,
                    row.Model,
                    row.AvailableModels.ToList());
                await StartProtocolDetectionAfterFetchAsync(
                    settings,
                    row.AvailableModels.ToList(),
                    summary =>
                    {
                        row.ModelCatalogSummary = summary;
                        SiteEditor.DraftStatusText = $"{DescribeDraftRow(row)}: {summary}";
                        StatusText = SiteEditor.DraftStatusText;
                    },
                    sortedModels => ReplaceAvailableModels(row.AvailableModels, sortedModels),
                    protocolSummary => row.ProtocolSummary = protocolSummary);
            }
            else
            {
                row.ModelCatalogSummary = result.Summary;
                SiteEditor.DraftStatusText = $"{DescribeDraftRow(row)}: {result.Summary}";
                StatusText = SiteEditor.DraftStatusText;
            }
        }
        catch (Exception ex)
        {
            row.ModelCatalogSummary = $"拉取模型失败: {ex.Message}";
            SiteEditor.DraftStatusText = $"{DescribeDraftRow(row)}: {row.ModelCatalogSummary}";
            StatusText = SiteEditor.DraftStatusText;
        }
        finally
        {
            row.IsFetchingModels = false;
        }
    }

    private async Task CacheModelCatalogResultAsync(
        ProxyEndpointSettings settings,
        ProxyModelCatalogResult result)
    {
        try
        {
            await _modelCatalogCacheSaver(settings, result, CancellationToken.None);
        }
        catch
        {
            // Best-effort cache warm-up only; the fetched model list remains usable.
        }
    }

    private Task<IReadOnlyList<ModelProtocolInfo>> RunModelProtocolDetectionCoreAsync(
        ProxyEndpointSettings settings,
        IReadOnlyList<string> models,
        IProgress<ModelProtocolProbeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var protocolProbeService = new ProxyEndpointProtocolProbeService(new ProxyDiagnosticsService(), _modelCacheService);
        var detectionService = new ModelProtocolDetectionService(protocolProbeService, _modelCacheService);
        return detectionService.DetectAllAsync(settings, models, progress, cancellationToken);
    }

    private async Task StartProtocolDetectionAfterFetchAsync(
        ProxyEndpointSettings settings,
        IReadOnlyList<string> models,
        Action<string> updateSummary,
        Action<IReadOnlyList<string>> applySortedModels,
        Action<string>? applyProtocolSummary = null)
    {
        if (!_modelProtocolDetectionEnabled)
        {
            return;
        }

        var normalizedModels = NormalizeModelList(models);
        if (normalizedModels.Count == 0)
        {
            return;
        }

        IReadOnlyList<CachedProxyEndpointModelInfo> cachedModels;
        try
        {
            cachedModels = await _cachedModelsLoader(settings, CancellationToken.None);
        }
        catch (Exception ex)
        {
            updateSummary($"已拉取 {normalizedModels.Count} 个模型 · 协议缓存检查失败：{ex.Message}");
            return;
        }

        if (!ShouldProbeProtocolsAfterFetch(normalizedModels, cachedModels))
        {
            applyProtocolSummary?.Invoke(FormatProtocolSummary(cachedModels));
            updateSummary($"已拉取 {normalizedModels.Count} 个模型 · 协议缓存已存在");
            return;
        }

        updateSummary($"已拉取 {normalizedModels.Count} 个模型 · 正在后台真实探测三种协议");
        applyProtocolSummary?.Invoke("探测中");
        _ = RunBackgroundProtocolDetectionAsync(settings, normalizedModels, updateSummary, applySortedModels, applyProtocolSummary);
    }

    private async Task RunBackgroundProtocolDetectionAsync(
        ProxyEndpointSettings settings,
        IReadOnlyList<string> models,
        Action<string> updateSummary,
        Action<IReadOnlyList<string>> applySortedModels,
        Action<string>? applyProtocolSummary = null)
    {
        try
        {
            var progress = new Progress<ModelProtocolProbeProgress>(probeProgress =>
            {
                updateSummary(probeProgress.Total <= 0
                    ? "正在准备协议探测..."
                    : $"正在真实请求三种协议：{probeProgress.Completed}/{probeProgress.Total}，最多 8 并发");
            });
            var results = await _modelProtocolDetector(settings, models, progress, CancellationToken.None);
            if (results.Count > 0)
            {
                applySortedModels(SortModelsByProtocolResults(models, results));
                applyProtocolSummary?.Invoke(FormatProtocolSummary(results));
            }

            updateSummary($"协议探测完成：已记录 {results.Count}/{models.Count} 个模型");
        }
        catch (Exception ex)
        {
            updateSummary($"后台协议探测失败：{ex.Message}");
        }
    }

    private static string FormatProtocolSummary(IReadOnlyList<ModelProtocolInfo> results)
    {
        if (results.Count == 0)
        {
            return "未探测";
        }

        var responses = results.Count(static item => item.Protocol == DetectedProtocol.Responses);
        var anthropic = results.Count(static item => item.Protocol == DetectedProtocol.Anthropic);
        var chat = results.Count(static item => item.Protocol == DetectedProtocol.V1Chat);
        return FormatProtocolSummary(responses, anthropic, chat);
    }

    private static string FormatProtocolSummary(IReadOnlyList<CachedProxyEndpointModelInfo> cachedModels)
    {
        if (cachedModels.Count == 0)
        {
            return "未探测";
        }

        var responses = cachedModels.Count(static item => item.ResponsesSupported == true);
        var anthropic = cachedModels.Count(static item => item.AnthropicMessagesSupported == true);
        var chat = cachedModels.Count(static item => item.ChatCompletionsSupported == true);
        return FormatProtocolSummary(responses, anthropic, chat);
    }

    private static string FormatProtocolSummary(int responses, int anthropic, int chat)
        => responses + anthropic + chat <= 0
            ? "未探测"
            : $"R{responses} A{anthropic} C{chat}";

    private static IReadOnlyList<string> NormalizeModelList(IEnumerable<string> models)
        => models
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> SortModelsByProtocolResults(
        IReadOnlyList<string> originalModels,
        IReadOnlyList<ModelProtocolInfo> results)
    {
        var sorted = results
            .Where(static result => !string.IsNullOrWhiteSpace(result.Model))
            .OrderBy(static result => result.Protocol)
            .ThenBy(static result => result.Model, StringComparer.OrdinalIgnoreCase)
            .Select(static result => result.Model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var model in originalModels)
        {
            if (!sorted.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                sorted.Add(model);
            }
        }

        return sorted;
    }

    private static void ReplaceAvailableModels(ObservableCollection<string> target, IReadOnlyList<string> models)
    {
        target.Clear();
        foreach (var model in models)
        {
            target.Add(model);
        }
    }

    private static bool ShouldProbeProtocolsAfterFetch(
        IReadOnlyList<string> fetchedModels,
        IReadOnlyList<CachedProxyEndpointModelInfo> cachedModels)
    {
        var fetched = NormalizeModelList(fetchedModels);
        if (fetched.Count == 0)
        {
            return false;
        }

        var cachedByModel = cachedModels
            .Where(static item => !string.IsNullOrWhiteSpace(item.Model))
            .GroupBy(static item => item.Model.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (cachedByModel.Count != fetched.Count ||
            fetched.Any(model => !cachedByModel.ContainsKey(model)))
        {
            return true;
        }

        return fetched.Any(model =>
        {
            var cached = cachedByModel[model];
            return (cached.ProtocolProbeVersion ?? 0) < ProxyWireApiProbeService.CurrentProtocolProbeVersion ||
                   (!cached.ChatCompletionsSupported.HasValue &&
                    !cached.ResponsesSupported.HasValue &&
                    !cached.AnthropicMessagesSupported.HasValue &&
                    string.IsNullOrWhiteSpace(cached.PreferredWireApi));
        });
    }

    [RelayCommand]
    private async Task SyncToTransparentProxyAsync()
    {
        var sites = BuildTransparentProxySyncCandidates();
        if (sites.Count == 0)
        {
            StatusText = "没有可同步的启用入口，请先在入口组里填写接口地址。";
            return;
        }

        try
        {
            var existingRoutes = await _routeRepository.GetAllAsync();
            var basePriority = Math.Max(
                1000,
                existingRoutes.Select(static route => route.Priority).DefaultIfEmpty(0).Max() + sites.Count);
            var synced = 0;

            for (var index = 0; index < sites.Count; index++)
            {
                var site = sites[index];
                var route = BuildRouteDefinition(site, existingRoutes, basePriority - index);
                await _routeRepository.UpsertAsync(route);
                synced++;
            }

            StatusText = $"已同步 {synced} 条入口到透明代理路由";
            TransparentProxyRoutesSynced?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = $"同步到透明代理失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyRankingEntryAsync(SiteRankEntry? entry)
    {
        if (!TryResolveRankingEndpoint(entry, out var baseUrl, out var apiKey, out var model))
        {
            StatusText = entry is null
                ? "请先选择一个排行入口。"
                : $"“{entry.SiteName}”缺少地址、API Key 或模型，暂时不能设为当前入口。";
            return;
        }

        BaseUrl = baseUrl;
        ApiKey = apiKey;
        Model = model;
        await _sharedEndpointSaver(baseUrl, apiKey, model);
        await _modelHistoryRecorder(baseUrl, apiKey, model, [model]);
        StatusText = $"已将“{entry!.SiteName}”设为当前入口，应用接入和其他测试页可复用。";
    }

    public IReadOnlyList<BatchTopCandidateApplicationCandidate> BuildTopCandidateApplicationCandidates(int maxCount = 8)
    {
        var candidates = new List<BatchTopCandidateApplicationCandidate>();
        foreach (var entry in SiteRankings
                     .OrderBy(static item => item.Rank)
                     .ThenByDescending(static item => item.CompositeScore)
                     .Take(Math.Max(1, maxCount)))
        {
            if (!TryResolveRankingEndpoint(entry, out var baseUrl, out var apiKey, out var model))
            {
                continue;
            }

            var source = ResolveRankingSourceByName(entry.SiteName);
            var models = new List<string>();
            AddModel(models, model);
            if (source is not null)
            {
                foreach (var availableModel in source.AvailableModels)
                {
                    AddModel(models, availableModel);
                }
            }

            candidates.Add(new BatchTopCandidateApplicationCandidate(
                entry.Rank,
                entry.SiteName,
                baseUrl,
                apiKey,
                model,
                models,
                entry.ScoreDisplay,
                string.IsNullOrWhiteSpace(entry.ProtocolSummary) ? "--" : entry.ProtocolSummary,
                $"{entry.LatencyDisplay} | TTFT {entry.TtftDisplay} | 成功率 {entry.SuccessRateDisplay}"));
        }

        if (candidates.Count == 0 &&
            NormalizeCandidateBaseUrl(BaseUrl) is { } currentBaseUrl &&
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(Model))
        {
            candidates.Add(new BatchTopCandidateApplicationCandidate(
                1,
                "当前输入",
                currentBaseUrl,
                ApiKey.Trim(),
                Model.Trim(),
                [Model.Trim()],
                "--",
                "--",
                "来自批量评测当前输入"));
        }

        return candidates;
    }

    private static void AddModel(List<string> models, string? model)
    {
        if (string.IsNullOrWhiteSpace(model) ||
            models.Contains(model.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        models.Add(model.Trim());
    }

    private bool TryResolveRankingEndpoint(
        SiteRankEntry? entry,
        out string baseUrl,
        out string apiKey,
        out string model)
    {
        baseUrl = string.Empty;
        apiKey = string.Empty;
        model = string.Empty;
        if (entry is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.BaseUrl) &&
            !string.IsNullOrWhiteSpace(entry.ApiKey) &&
            !string.IsNullOrWhiteSpace(entry.Model))
        {
            baseUrl = NormalizeCandidateBaseUrl(entry.BaseUrl) ?? entry.BaseUrl.Trim();
            apiKey = entry.ApiKey.Trim();
            model = entry.Model.Trim();
            return true;
        }

        var matched = ResolveRankingSourceByName(entry.SiteName);
        if (matched is null)
        {
            return false;
        }

        baseUrl = NormalizeCandidateBaseUrl(matched.BaseUrl) ?? matched.BaseUrl.Trim();
        apiKey = matched.ApiKey.Trim();
        model = matched.Model.Trim();
        return !string.IsNullOrWhiteSpace(baseUrl) &&
               !string.IsNullOrWhiteSpace(apiKey) &&
               !string.IsNullOrWhiteSpace(model);
    }

    private IReadOnlyList<BatchSiteEntry> BuildTransparentProxySyncCandidates()
    {
        var sites = BuildResolvedIncludedSites(requireCredentials: false, out _);
        var rankByName = SiteRankings
            .GroupBy(static item => item.SiteName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Min(item => item.Rank), StringComparer.OrdinalIgnoreCase);

        return sites
            .Select((site, index) => new
            {
                Site = site,
                OriginalIndex = index,
                Rank = rankByName.TryGetValue(site.DisplayName, out var rank) ? rank : int.MaxValue
            })
            .OrderBy(static item => item.Rank)
            .ThenBy(static item => item.OriginalIndex)
            .Select(static item => item.Site)
            .ToList();
    }

    private BatchSiteEntry? ResolveRankingSourceByName(string? siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            return null;
        }

        var sites = BuildResolvedIncludedSites(requireCredentials: false, out _).ToList();
        if (sites.Count == 0 &&
            NormalizeCandidateBaseUrl(BaseUrl) is { } currentUrl)
        {
            sites.Add(new BatchSiteEntry(currentUrl, ApiKey.Trim(), Model.Trim()));
        }

        return sites.FirstOrDefault(site =>
            string.Equals(site.DisplayName, siteName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private RouteDefinition BuildRouteDefinition(
        BatchSiteEntry site,
        IReadOnlyList<RouteDefinition> existingRoutes,
        int priority)
    {
        var normalizedUrl = site.BaseUrl.Trim().TrimEnd('/');
        var existing = existingRoutes.FirstOrDefault(route =>
            string.Equals(route.UpstreamUrl.Trim().TrimEnd('/'), normalizedUrl, StringComparison.OrdinalIgnoreCase));
        var model = !string.IsNullOrWhiteSpace(site.Model)
            ? site.Model.Trim()
            : !string.IsNullOrWhiteSpace(Model)
                ? Model.Trim()
                : null;
        var apiKey = !string.IsNullOrWhiteSpace(site.ApiKey)
            ? site.ApiKey.Trim()
            : !string.IsNullOrWhiteSpace(ApiKey)
                ? ApiKey.Trim()
                : null;

        return new RouteDefinition(
            Id: existing?.Id ?? BuildBatchRouteId(normalizedUrl),
            Name: string.IsNullOrWhiteSpace(site.DisplayName) ? normalizedUrl : site.DisplayName,
            UpstreamUrl: normalizedUrl,
            ApiKeyProtected: apiKey,
            Priority: priority,
            ModelFilter: model,
            Enabled: true,
            UpdatedAtUtc: DateTime.UtcNow);
    }

    private static string BuildBatchRouteId(string normalizedUrl)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl.ToLowerInvariant()));
        return $"batch-{Convert.ToHexString(bytes)[..16].ToLowerInvariant()}";
    }

    private IReadOnlyList<BatchSiteEntry> BuildResolvedIncludedSites(bool requireCredentials, out string? error)
    {
        error = null;
        List<BatchSiteEntry> resolvedSites = [];
        var previousApiKey = NormalizeNullable(ApiKey);
        var previousModel = NormalizeNullable(Model);

        foreach (var site in SiteEditor.Sites.Where(static site => site.IsIncluded))
        {
            var normalizedUrl = NormalizeCandidateBaseUrl(site.BaseUrl);
            if (normalizedUrl is null)
            {
                if (requireCredentials)
                {
                    error = $"入口“{site.DisplayName}”的接口地址无效。";
                    return [];
                }

                continue;
            }

            var apiKey = FirstNonEmpty(site.ApiKey, previousApiKey, ApiKey);
            var model = FirstNonEmpty(site.Model, previousModel, Model);

            if (requireCredentials && string.IsNullOrWhiteSpace(apiKey))
            {
                error = $"入口“{site.DisplayName}”缺少 API Key；可在本行填写，或让它沿用上一行 Key。";
                return [];
            }

            if (requireCredentials && string.IsNullOrWhiteSpace(model))
            {
                error = $"入口“{site.DisplayName}”缺少模型；可在本行填写，或让它沿用上一行模型。";
                return [];
            }

            var resolved = site.Duplicate();
            resolved.BaseUrl = normalizedUrl;
            resolved.ApiKey = apiKey ?? string.Empty;
            resolved.Model = model ?? string.Empty;
            resolvedSites.Add(resolved);

            previousApiKey = apiKey ?? previousApiKey;
            previousModel = model ?? previousModel;
        }

        return resolvedSites;
    }

    private (string? ApiKey, string? Model) ResolveEffectiveLineContext(BatchSiteEntry targetSite)
    {
        var previousApiKey = NormalizeNullable(ApiKey);
        var previousModel = NormalizeNullable(Model);

        foreach (var site in SiteEditor.Sites)
        {
            var currentApiKey = FirstNonEmpty(site.ApiKey, previousApiKey, ApiKey);
            var currentModel = FirstNonEmpty(site.Model, previousModel, Model);
            if (ReferenceEquals(site, targetSite))
            {
                return (currentApiKey, currentModel);
            }

            previousApiKey = currentApiKey ?? previousApiKey;
            previousModel = currentModel ?? previousModel;
        }

        return (
            FirstNonEmpty(targetSite.ApiKey, previousApiKey, ApiKey),
            FirstNonEmpty(targetSite.Model, previousModel, Model));
    }

    private (string? ApiKey, string? Model) ResolveEffectiveDraftRowContext(BatchSiteDraftRow targetRow)
    {
        var previousApiKey = NormalizeNullable(ApiKey);
        var previousModel = NormalizeNullable(Model);

        foreach (var row in SiteEditor.DraftRows)
        {
            var currentApiKey = FirstNonEmpty(row.ApiKey, previousApiKey, ApiKey);
            var currentModel = FirstNonEmpty(row.Model, previousModel, Model);
            if (ReferenceEquals(row, targetRow))
            {
                return (currentApiKey, currentModel);
            }

            previousApiKey = currentApiKey ?? previousApiKey;
            previousModel = currentModel ?? previousModel;
        }

        return (
            FirstNonEmpty(targetRow.ApiKey, previousApiKey, ApiKey),
            FirstNonEmpty(targetRow.Model, previousModel, Model));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(NormalizeNullable).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<ProxyThroughputBenchmarkResult?> RunThroughputBenchmarkCoreAsync(
        ProxyEndpointSettings settings,
        ProxyDiagnosticsResult baselineResult,
        IProgress<ProxyThroughputBenchmarkLiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var throughputSettings = string.IsNullOrWhiteSpace(baselineResult.EffectiveModel)
            ? settings
            : settings with { Model = baselineResult.EffectiveModel.Trim() };

        return await _diagnosticsService.RunThroughputBenchmarkAsync(
            throughputSettings,
            baselineResult: baselineResult,
            liveProgress: progress,
            cancellationToken: cancellationToken);
    }

    private Task<ProxyDiagnosticsResult> RunStandardBatchDeepSupplementalScenariosAsync(
        ProxyEndpointSettings settings,
        ProxyDiagnosticsResult baselineResult,
        IProgress<ProxyDiagnosticsLiveProgress>? progress,
        CancellationToken cancellationToken)
        => _diagnosticsService.RunSupplementalScenariosAsync(
            settings,
            baselineResult,
            includeProtocolCompatibility: true,
            includeErrorTransparency: true,
            includeStreamingIntegrity: true,
            includeOfficialReferenceIntegrity: false,
            officialReferenceBaseUrl: null,
            officialReferenceApiKey: null,
            officialReferenceModel: null,
            includeMultiModal: true,
            includeCacheMechanism: true,
            includeCacheIsolation: false,
            cacheIsolationAlternateApiKey: null,
            includeInstructionFollowing: true,
            includeDataExtraction: true,
            includeStructuredOutputEdge: true,
            includeToolCallDeep: true,
            includeReasonMathConsistency: false,
            includeCodeBlockDiscipline: true,
            progress: progress,
            cancellationToken: cancellationToken);
}
