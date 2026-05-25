using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Core.Support;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class SingleStationViewModel
{    // ========== 接口History ==========
    [RelayCommand]
    private async Task LoadEndpointHistoryAsync()
    {
        var items = await _historyStore.LoadAsync();
        EndpointHistory.Clear();
        foreach (var item in items)
            EndpointHistory.Add(item);
    }

    [RelayCommand]
    private void ApplyHistoryEntry(EndpointHistoryItem? entry)
    {
        if (entry is null) return;
        BaseUrl = entry.BaseUrl;
        Model = entry.Model;
        ApiKey = entry.ApiKey;
    }

    [RelayCommand]
    private async Task ClearEndpointHistoryAsync()
    {
        await _historyStore.ClearAsync();
        EndpointHistory.Clear();
    }

    private async Task RecordEndpointUsageAsync()
    {
        await GlobalEndpointProtocolProbeCoordinator.Instance.RecordEndpointAsync(BaseUrl, ApiKey, Model);
        GlobalEndpointProtocolProbeCoordinator.Instance.EnqueueEndpointProbe(
            BaseUrl,
            ApiKey,
            Model,
            GetMultiModelCandidateModels());
        await LoadEndpointHistoryAsync();
        await RefreshModelProtocolCacheAsync();
    }

    public IReadOnlyList<string> GetMultiModelCandidateModels()
        => AvailableModels
            .Concat(ModelProtocolCacheRows.Select(static row => row.Model))
            .Append(Model)
            .Concat(GetSelectedMultiModelBenchmarkModels())
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<string> GetSelectedMultiModelBenchmarkModelNames()
        => GetSelectedMultiModelBenchmarkModels();

    public void SetMultiModelBenchmarkModels(IEnumerable<string> models)
    {
        MultiModelBenchmarkModelsText = string.Join(
            Environment.NewLine,
            models
                .Where(static model => !string.IsNullOrWhiteSpace(model))
                .Select(static model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private void AddAvailableModelIfMissing(string? model)
    {
        var normalized = model?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            AvailableModels.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        AvailableModels.Add(normalized);
    }

    private async Task RefreshModelProtocolCacheAsync(CancellationToken cancellationToken = default)
    {
        ModelProtocolCacheRows.Clear();
        HasModelProtocolCacheRows = false;

        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey))
        {
            ModelProtocolCacheSummary = "填写接口地址和 API 密钥后会显示协议记录";
            return;
        }

        try
        {
            var settings = BuildSettings();
            var models = await _modelCacheService
                .ListModelsAsync(settings.BaseUrl, settings.ApiKey, cancellationToken);
            foreach (var item in models
                         .OrderBy(item => ProtocolSortRank(item.PreferredWireApi))
                         .ThenBy(static item => item.Model, StringComparer.OrdinalIgnoreCase))
            {
                AddAvailableModelIfMissing(item.Model);
                ModelProtocolCacheRows.Add(new ModelProtocolCacheRow
                {
                    Model = item.Model,
                    PreferredProtocol = MapWireApiDisplay(item.PreferredWireApi),
                    ChatSupport = MapSupportDisplay(item.ChatCompletionsSupported),
                    ResponsesSupport = MapSupportDisplay(item.ResponsesSupported),
                    AnthropicSupport = MapSupportDisplay(item.AnthropicMessagesSupported),
                    CheckedAt = item.CheckedAt == DateTimeOffset.MinValue
                        ? "0"
                        : item.CheckedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                });
            }

            HasModelProtocolCacheRows = ModelProtocolCacheRows.Count > 0;
            ModelProtocolCacheSummary = HasModelProtocolCacheRows
                ? $"已记录 {ModelProtocolCacheRows.Count} 个模型的协议探测结果"
                : "暂无协议记录，拉取模型后会后台探测并写入";
        }
        catch (Exception ex)
        {
            ModelProtocolCacheSummary = $"协议记录读取失败: {ex.Message}";
        }
    }

    private static string MapWireApiDisplay(string? wireApi)
        => ProxyWireApiProbeService.NormalizeWireApi(wireApi) switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "响应",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "消息",
            ProxyWireApiProbeService.ChatCompletionsWireApi => "聊天",
            _ => "0"
        };

    private static string MapSupportDisplay(bool? supported)
        => supported.HasValue ? supported.Value ? "可用" : "不可用" : "未测";

    private static int ProtocolSortRank(string? wireApi)
        => ProxyWireApiProbeService.NormalizeWireApi(wireApi) switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => 0,
            ProxyWireApiProbeService.AnthropicMessagesWireApi => 1,
            ProxyWireApiProbeService.ChatCompletionsWireApi => 2,
            _ => 3
        };

    private static bool ShouldProbeProtocolsAfterFetch(
        IReadOnlyList<string> fetchedModels,
        IReadOnlyList<CachedProxyEndpointModelInfo> cachedModels)
    {
        var fetched = fetchedModels
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (fetched.Length == 0)
        {
            return false;
        }

        var cachedByModel = cachedModels
            .Where(static item => !string.IsNullOrWhiteSpace(item.Model))
            .GroupBy(static item => item.Model.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (cachedByModel.Count != fetched.Length ||
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

    /// <summary>
    /// Runs protocol detection for all models in the background.
    /// Results are saved to the SQLite cache and models are sorted by protocol priority.
    /// </summary>
    private async Task RunBackgroundProtocolDetectionAsync(List<string> models)
    {
        try
        {
            var settings = BuildSettings();
            var progress = new Progress<ModelProtocolProbeProgress>(ApplyModelProtocolProbeProgress);
            await GlobalEndpointProtocolProbeCoordinator.Instance.ProbeAsync(
                settings,
                models,
                force: true,
                progress);

            var cached = await _modelCacheService.ListModelsAsync(settings.BaseUrl, settings.ApiKey);
            var results = cached
                .Where(static item => !string.IsNullOrWhiteSpace(item.Model))
                .Select(static item => item.Model)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (results.Count > 0)
            {
                var sortedModels = results.ToList();
                foreach (var m in models)
                {
                    if (!sortedModels.Contains(m, StringComparer.OrdinalIgnoreCase))
                        sortedModels.Add(m);
                }

                AvailableModels.Clear();
                foreach (var m in sortedModels)
                    AddAvailableModelIfMissing(m);
            }

            await RefreshModelProtocolCacheAsync();
            ModelProtocolCacheSummary = $"真实协议探测完成：已记录 {ModelProtocolCacheRows.Count} 个模型";
        }
        catch (Exception ex)
        {
            ModelProtocolCacheSummary = $"后台协议探测失败：{ex.Message}";
        }
    }

    private void ApplyModelProtocolProbeProgress(ModelProtocolProbeProgress progress)
    {
        if (progress.LastOutcome is { } outcome)
        {
            AddAvailableModelIfMissing(outcome.Model);
            UpsertModelProtocolCacheRow(outcome);
            StatusText = $"协议探测 {progress.Completed}/{progress.Total}：{outcome.Model}";
        }

        HasModelProtocolCacheRows = ModelProtocolCacheRows.Count > 0;
        ModelProtocolCacheSummary = progress.Total <= 0
            ? "正在准备协议探测..."
            : $"正在真实请求三种协议：{progress.Completed}/{progress.Total}，最多 {ModelProtocolProbeMaxConcurrency} 并发";
    }

    private void UpsertModelProtocolCacheRow(ProxyModelProtocolProbeOutcome outcome)
    {
        var row = new ModelProtocolCacheRow
        {
            Model = outcome.Model,
            PreferredProtocol = MapWireApiDisplay(outcome.PreferredWireApi),
            ChatSupport = MapSupportDisplay(outcome.ChatCompletionsSupported),
            ResponsesSupport = MapSupportDisplay(outcome.ResponsesSupported),
            AnthropicSupport = MapSupportDisplay(outcome.AnthropicMessagesSupported),
            CheckedAt = outcome.CheckedAt.ToLocalTime().ToString("MM-dd HH:mm"),
        };

        for (var index = 0; index < ModelProtocolCacheRows.Count; index++)
        {
            if (string.Equals(ModelProtocolCacheRows[index].Model, outcome.Model, StringComparison.OrdinalIgnoreCase))
            {
                ModelProtocolCacheRows[index] = row;
                return;
            }
        }

        ModelProtocolCacheRows.Add(row);
    }

}
