using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task CacheProxyModelCatalogResultAsync(
        ProxyEndpointSettings settings,
        ProxyModelCatalogResult result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _proxyEndpointModelCacheService.SaveCatalogAsync(settings, result, cancellationToken);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("ProxyEndpointModelCache.SaveCatalog", ex);
        }
    }

    private async Task CacheProxyDiagnosticsResultAsync(
        ProxyEndpointSettings settings,
        ProxyDiagnosticsResult result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _proxyEndpointModelCacheService.SaveDiagnosticsAsync(settings, result, cancellationToken);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("ProxyEndpointModelCache.SaveDiagnostics", ex);
        }
    }

    private async Task DetectAndCacheProxyWireApiAsync(
        ProxyEndpointSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var cachedWireApi = await _proxyEndpointModelCacheService.TryResolvePreferredWireApiAsync(
                settings.BaseUrl,
                settings.ApiKey,
                settings.Model,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(cachedWireApi))
            {
                StatusMessage = $"已使用缓存的接口链接方式：{cachedWireApi}。";
                return;
            }

            StatusMessage = "正在检测接口链接方式（chat / responses）...";
            var result = await _proxyDiagnosticsService.ProbeProtocolAsync(settings, cancellationToken);
            await _proxyEndpointModelCacheService.SaveProtocolProbeAsync(settings, result, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.PreferredWireApi))
            {
                StatusMessage = $"已识别接口链接方式：{result.PreferredWireApi}。";
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
            {
                StatusMessage = $"接口链接方式暂未识别：{result.Error}";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("ProxyEndpointModelCache.DetectWireApi", ex);
        }
    }

    private async Task<CodexApplyCachedModelInfo> ResolveCachedCodexApplyInfoAsync(
        string baseUrl,
        string apiKey,
        string model)
    {
        CachedProxyEndpointModelInfo? cached = null;
        try
        {
            cached = await _proxyEndpointModelCacheService.TryResolveAsync(baseUrl, apiKey, model);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("ProxyEndpointModelCache.Resolve", ex);
        }

        return new CodexApplyCachedModelInfo(
            cached?.ContextWindow ?? ResolveProxyModelContextWindow(model),
            cached?.PreferredWireApi);
    }

    private sealed record CodexApplyCachedModelInfo(
        int? ContextWindow,
        string? PreferredWireApi);
}
