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
    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (!TryBuildSettings(out var settings, requireModel: false))
        {
            StatusText = "请输入入口 URL 和 API 密钥";
            return;
        }

        IsFetchingModels = true;
        StatusText = "正在拉取模型列表...";
        try
        {
            var result = await _diagnosticsService.FetchModelsAsync(settings);
            AvailableModels.Clear();
            foreach (var model in result.Models.Where(static model => !string.IsNullOrWhiteSpace(model)))
            {
                AvailableModels.Add(model);
            }

            if (result.Success && AvailableModels.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(Model) ||
                    !AvailableModels.Contains(Model, StringComparer.OrdinalIgnoreCase))
                {
                    Model = AvailableModels[0];
                }

                await CacheModelCatalogResultAsync(settings, result);
                await GlobalEndpointProtocolProbeCoordinator.Instance.RecordEndpointAsync(BaseUrl, ApiKey, Model, AvailableModels);
                GlobalEndpointProtocolProbeCoordinator.Instance.EnqueueEndpointProbe(
                    BaseUrl,
                    ApiKey,
                    Model,
                    AvailableModels,
                    force: true);
                RefreshAccessOverview();
                StatusText = $"已加载 {AvailableModels.Count} 个模型";
            }
            else
            {
                StatusText = result.Summary;
                RefreshAccessOverview();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"拉取模型失败: {ex.Message}";
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    [RelayCommand]
    private async Task FetchApplicationCenterProxyModelsAsync()
        => await FetchModelsAsync();

    private async Task CacheModelCatalogResultAsync(
        ProxyEndpointSettings settings,
        ProxyModelCatalogResult result)
    {
        try
        {
            await _modelCacheService.SaveCatalogAsync(settings, result);
        }
        catch
        {
            // Best-effort cache warm-up only; application access remains usable.
        }
    }

    [RelayCommand]
    private void RefreshCurrentEndpoint()
    {
        if (LoadPersistedEndpoint())
        {
            _lastProbeResult = null;
            ResetProbeRows();
            RefreshTargets();
            RefreshTemplateRows();
            StatusText = "\u5df2\u540c\u6b65\u5f53\u524d\u5165\u53e3";
            ProbeResult = "尚未探测；写入前会重新检查协议";
            return;
        }

        StatusText = "暂无可同步的当前入口";
    }

    public bool TryApplyTransparentProxyEndpoint(TransparentProxyViewModel proxy, bool overwrite)
    {
        var alreadyOnProxy = IsSameEndpoint(BaseUrl, proxy.LocalEndpoint);
        if (!overwrite && !alreadyOnProxy && !CanReplaceWithTransparentProxy() && !IsTransparentProxySentinelEndpoint())
        {
            return false;
        }

        var models = BuildTransparentProxyModelList(proxy);
        var selected = alreadyOnProxy && !string.IsNullOrWhiteSpace(Model) &&
                       models.Contains(Model.Trim(), StringComparer.OrdinalIgnoreCase)
            ? Model.Trim()
            : models.FirstOrDefault() ?? string.Empty;

        _lastProbeResult = null;
        BaseUrl = proxy.LocalEndpoint;
        ApiKey = "relaybench-local";
        Model = selected;
        PreferredProtocol = ResolveTransparentProxyPreferredProtocol(proxy);
        ResponsesSupported = false;
        AnthropicSupported = false;
        ChatSupported = false;

        AvailableModels.Clear();
        foreach (var model in models)
        {
            AvailableModels.Add(model);
        }

        SelectedTargetName = "\u672c\u5730\u900f\u660e\u4ee3\u7406";
        StatusText = string.IsNullOrWhiteSpace(selected)
            ? "\u5e94\u7528\u63a5\u5165\u5df2\u63a5\u5165\u900f\u660e\u4ee3\u7406\uff0c\u7b49\u5f85\u771f\u5b9e\u6a21\u578b\u6c60"
            : $"\u5e94\u7528\u63a5\u5165\u5df2\u63a5\u5165\u900f\u660e\u4ee3\u7406\uff1a{selected}";
        StatusMessage = BuildTransparentProxyContext(proxy, models.Count);
        ProbeResult = "正在使用本地透明代理；写入前会重新检查协议。";
        ResetProbeRows();
        RefreshTargets();
        RefreshTemplateRows();
        WriteTraceSteps.Clear();
        WriteTraceSteps.Add(new WriteTraceItem("1", "\u63a5\u5165\u672c\u5730\u4ee3\u7406", proxy.LocalEndpoint, "\u5b8c\u6210", ToneHealthy));
        WriteTraceSteps.Add(new WriteTraceItem("2", "\u540c\u6b65\u6a21\u578b\u6c60", $"{models.Count} \u4e2a\u5019\u9009\u6a21\u578b", models.Count > 0 ? "\u5b8c\u6210" : "\u7b49\u5f85", models.Count > 0 ? ToneHealthy : ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("3", "\u9009\u62e9\u5ba2\u6237\u7aef", "Codex / Claude", "\u5c31\u7eea", ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("4", "\u771f\u5b9e\u534f\u8bae\u590d\u6838", "\u5199\u5165\u524d\u6309\u5f53\u524d\u6a21\u578b\u63a2\u6d4b", "\u7b49\u5f85", ToneAccent));
        WriteTraceSteps.Add(new WriteTraceItem("5", "\u5907\u4efd\u5e76\u5199\u5165", "\u4fdd\u7559\u539f\u914d\u7f6e\u540e\u5e94\u7528", "\u7b49\u5f85", ToneAccent));
        return true;
    }

}
