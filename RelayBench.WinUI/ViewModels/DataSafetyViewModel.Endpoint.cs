using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RelayBench.Core.Models;
using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.AdvancedTesting.Reporting;
using RelayBench.Core.AdvancedTesting.Runners;
using RelayBench.Core.Services;
using RelayBench.WinUI.Storage;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class DataSafetyViewModel : ObservableObject
{
    [RelayCommand]
    private async Task UseTransparentProxyAsync()
    {
        var proxy = global::RelayBench.WinUI.App.TransparentProxyViewModel;
        if (!proxy.IsRunning)
        {
            if (proxy.Routes.Count == 0)
            {
                StatusText = "透明代理还没有可用路由，暂时不能接入数据安全";
                TransparentProxyContextText = "未发现透明代理路由";
                return;
            }

            StatusText = "正在启动透明代理...";
            await proxy.ToggleProxyAsync();
        }

        if (!proxy.IsRunning)
        {
            StatusText = "透明代理未运行，无法接入数据安全";
            return;
        }

        var models = BuildTransparentProxyModelList(proxy);
        var selectedModel = !string.IsNullOrWhiteSpace(Model) &&
                            models.Contains(Model.Trim(), StringComparer.OrdinalIgnoreCase)
            ? Model.Trim()
            : models.FirstOrDefault() ?? string.Empty;
        ApplyTransparentProxyEndpoint(
            proxy.LocalEndpoint,
            "relaybench-local",
            models,
            selectedModel,
            BuildTransparentProxyContext(proxy, models.Count));
    }

    public void ApplyTransparentProxyEndpoint(
        string baseUrl,
        string apiKey,
        IEnumerable<string> models,
        string selectedModel,
        string routeContext)
    {
        var normalizedBaseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://127.0.0.1:8080"
            : baseUrl.Trim();
        if (normalizedBaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ProtocolIndex = 1;
        }
        else if (normalizedBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ProtocolIndex = 0;
        }

        var uniqueModels = models
            .Select(static model => model?.Trim() ?? string.Empty)
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        BaseUrl = normalizedBaseUrl;
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? "relaybench-local" : apiKey.Trim();
        AvailableModels.Clear();
        if (uniqueModels.Count == 0)
        {
            Model = string.Empty;
            IsTransparentProxyEndpoint = true;
            EndpointSourceText = "透明代理模型池 · 0";
            TransparentProxyContextText = string.IsNullOrWhiteSpace(routeContext)
                ? $"已接入 {normalizedBaseUrl}，等待真实模型池"
                : routeContext.Trim();
            StatusText = "透明代理还没有可用模型，请先拉取模型或运行协议发现";
            return;
        }

        foreach (var model in uniqueModels)
        {
            AvailableModels.Add(model);
        }

        Model = !string.IsNullOrWhiteSpace(selectedModel) &&
                uniqueModels.Contains(selectedModel.Trim(), StringComparer.OrdinalIgnoreCase)
            ? selectedModel.Trim()
            : uniqueModels[0];
        IsTransparentProxyEndpoint = true;
        EndpointSourceText = $"透明代理模型池 · {Model}";
        TransparentProxyContextText = string.IsNullOrWhiteSpace(routeContext)
            ? $"已接入 {normalizedBaseUrl}"
            : routeContext.Trim();
        StatusText = $"数据安全已接入透明代理模型池：{Model}";
        RefreshProtocolPriorityFromSelection();
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

        return models;
    }

    private static string BuildTransparentProxyContext(TransparentProxyViewModel proxy, int modelCount)
    {
        var enabledRoutes = proxy.Routes.Count(static route => route.Enabled);
        return $"透明代理：{proxy.LocalEndpoint} · {enabledRoutes} 条启用路由 · {modelCount} 个候选模型";
    }

    private static IEnumerable<string> SplitModelFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            yield break;
        }

        foreach (var item in filter.Split([',', ';', '|', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = item.Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && normalized != "*")
            {
                yield return normalized;
            }
        }
    }

    private static void AddModel(List<string> models, string? model)
    {
        var normalized = model?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) ||
            models.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        models.Add(normalized);
    }

    private void MarkDirectEndpoint()
    {
        IsTransparentProxyEndpoint = false;
        EndpointSourceText = "直接接口";
        TransparentProxyContextText = "未接入透明代理模型池";
    }

    private static bool IsLocalTransparentProxyBaseUrl(string? baseUrl)
    {
        var normalized = baseUrl?.Trim() ?? string.Empty;
        return normalized.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase);
    }

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

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusText = "请填写接口地址和接口密钥";
            return;
        }

        IsTesting = true;
        StatusText = "正在获取模型...";
        try
        {
            var diagnosticsService = new RelayBench.Core.Services.ProxyDiagnosticsService();
            var normalizedBaseUrl = NormalizeBaseUrl(BaseUrl);
            var settings = new RelayBench.Core.Models.ProxyEndpointSettings(
                normalizedBaseUrl, ApiKey.Trim(), Model.Trim(), IgnoreTlsErrors, Math.Clamp(Timeout, 5, 300));
            var result = await diagnosticsService.FetchModelsAsync(settings);
            StatusText = result.Summary;

            AvailableModels.Clear();
            if (result.Success && result.Models is { Count: > 0 })
            {
                foreach (var model in result.Models)
                    AvailableModels.Add(model);

                if (string.IsNullOrWhiteSpace(Model) && AvailableModels.Count > 0)
                    Model = AvailableModels[0];

                BaseUrl = normalizedBaseUrl;
                await CacheModelCatalogResultAsync(settings, result);
                await _historyStore.RecordWithModelsAsync(BaseUrl, ApiKey, Model, result.Models.ToList());
                await SharedEndpointStore.SaveAsync(BaseUrl, ApiKey, Model);
                await LoadEndpointHistoryAsync();
                await RefreshProtocolPriorityAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

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
            // Best-effort cache warm-up only; the fetched model list remains usable.
        }
    }

    [RelayCommand]
    private async Task RefreshProtocolCacheAsync()
    {
        await RefreshProtocolPriorityAsync();
    }

    private async Task RefreshProtocolPriorityAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey))
        {
        ProtocolCacheSummary = "填写接口地址和接口密钥后显示协议缓存";
            RefreshProtocolPriorityFromSelection();
            return;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            ProtocolCacheSummary = "选择模型后显示该模型的协议记录";
            RefreshProtocolPriorityFromSelection();
            return;
        }

        try
        {
            var cached = await _modelCacheService.TryResolveAsync(
                NormalizeBaseUrl(BaseUrl),
                ApiKey.Trim(),
                Model.Trim(),
                cancellationToken);
            if (cached is null || (cached.ProtocolProbeVersion ?? 0) < ProxyWireApiProbeService.CurrentProtocolProbeVersion)
            {
        ProtocolCacheSummary = "暂无有效协议缓存，拉取模型或开始测试后会自动探测";
                RefreshProtocolPriorityFromSelection();
                return;
            }

        ProtocolCacheSummary = $"协议缓存 v{cached.ProtocolProbeVersion} · {cached.CheckedAt.ToLocalTime():MM-dd HH:mm}";
            RefreshProtocolPriorityFromSelection(cached);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProtocolCacheSummary = $"协议缓存读取失败: {ex.Message}";
            RefreshProtocolPriorityFromSelection();
        }
    }

    private void RefreshProtocolPriorityFromSelection(CachedProxyEndpointModelInfo? cached = null)
    {
        var forcedWireApi = GetPreferredWireApi();
        var preferredWireApi = forcedWireApi ?? cached?.PreferredWireApi;
        ProtocolPriorityItems.Clear();
        ProtocolPriorityItems.Add(BuildProtocolPriorityItem(
            "Responses 接口",
            "POST /v1/responses",
            ProxyWireApiProbeService.ResponsesWireApi,
            cached?.ResponsesSupported,
            preferredWireApi,
            forcedWireApi));
        ProtocolPriorityItems.Add(BuildProtocolPriorityItem(
            "Anthropic 消息",
            "POST /v1/messages",
            ProxyWireApiProbeService.AnthropicMessagesWireApi,
            cached?.AnthropicMessagesSupported,
            preferredWireApi,
            forcedWireApi));
        ProtocolPriorityItems.Add(BuildProtocolPriorityItem(
            "OpenAI 聊天",
            "POST /v1/chat/completions",
            ProxyWireApiProbeService.ChatCompletionsWireApi,
            cached?.ChatCompletionsSupported,
            preferredWireApi,
            forcedWireApi));

        bool? clientSupported = cached is null
            ? null
            : (cached.ChatCompletionsSupported == true ||
               cached.ResponsesSupported == true ||
               cached.AnthropicMessagesSupported == true);
        ProtocolPriorityItems.Add(BuildClientProtocolItem(clientSupported, preferredWireApi, forcedWireApi));
    }

    private static ProtocolPriorityItem BuildProtocolPriorityItem(
        string protocol,
        string requestPath,
        string wireApi,
        bool? supported,
        string? preferredWireApi,
        string? forcedWireApi)
    {
        var isPreferred = string.Equals(preferredWireApi, wireApi, StringComparison.Ordinal);
        var statusText = supported switch
        {
            true when isPreferred && forcedWireApi is not null => "已选择",
            true when isPreferred => "优先",
            true => "支持",
            false => "未通过",
            _ when isPreferred && forcedWireApi is not null => "已选择",
            _ => "待探测"
        };
        var weight = supported switch
        {
            true when isPreferred => 100,
            true => 72,
            false => 22,
            _ when isPreferred => 58,
            _ => 40
        };

        return new ProtocolPriorityItem(
            protocol,
            requestPath,
            isPreferred ? "1" : "-",
            $"{weight}%",
            Math.Max(16, weight * 1.42),
            statusText,
            ResolveProtocolStatusTone(statusText));
    }

    private static ProtocolPriorityItem BuildClientProtocolItem(bool? supported, string? preferredWireApi, string? forcedWireApi)
    {
        var statusText = supported switch
        {
            true when forcedWireApi is not null => "跟随选择",
            true => "健康",
            false => "未通过",
            _ => "待探测"
        };
        var weight = supported switch
        {
            true when string.Equals(preferredWireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal) => 86,
            true => 68,
            false => 20,
            _ => 42
        };

        return new ProtocolPriorityItem(
            "Codex / Claude 客户端",
            "客户端适配",
            "2",
            $"{weight}%",
            Math.Max(16, weight * 1.42),
            statusText,
            ResolveProtocolStatusTone(statusText));
    }

    private static ProtocolPriorityStatusTone ResolveProtocolStatusTone(string statusText)
        => statusText switch
        {
            "未通过" => ProtocolPriorityStatusTone.Danger,
            "待探测" => ProtocolPriorityStatusTone.Muted,
            "已选择" or "优先" or "跟随选择" => ProtocolPriorityStatusTone.Accent,
            _ => ProtocolPriorityStatusTone.Healthy
        };

}
