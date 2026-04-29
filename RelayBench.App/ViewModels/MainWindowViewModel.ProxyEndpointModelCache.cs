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
            var responsesSupported = result.ScenarioResults?
                .FirstOrDefault(static item => item.Scenario == ProxyProbeScenarioKind.Responses)?
                .Success == true;
            RememberCodexResponsesCompatibility(
                settings.BaseUrl,
                settings.ApiKey,
                FirstNonEmpty(result.EffectiveModel, result.RequestedModel, settings.Model),
                responsesSupported);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("ProxyEndpointModelCache.SaveDiagnostics", ex);
        }
    }

    private async Task<ProxyEndpointProtocolProbeResult?> DetectAndCacheProxyWireApiAsync(
        ProxyEndpointSettings settings,
        CancellationToken cancellationToken = default,
        bool forceProbe = false)
    {
        try
        {
            if (!forceProbe)
            {
                var cached = await _proxyEndpointModelCacheService.TryResolveAsync(
                    settings.BaseUrl,
                    settings.ApiKey,
                    settings.Model,
                    cancellationToken);
                if (cached is not null)
                {
                    RememberCodexResponsesCompatibility(
                        settings.BaseUrl,
                        settings.ApiKey,
                        settings.Model,
                        cached.ResponsesSupported == true);
                }

                if (!string.IsNullOrWhiteSpace(cached?.PreferredWireApi))
                {
                    StatusMessage = $"已使用缓存的 Codex 接口链接方式：{cached.PreferredWireApi}。";
                    return null;
                }
            }

            StatusMessage = "正在检测接口链接方式（chat / responses / Anthropic messages）...";
            var result = await _proxyDiagnosticsService.ProbeProtocolAsync(settings, cancellationToken);
            await _proxyEndpointModelCacheService.SaveProtocolProbeAsync(settings, result, cancellationToken);
            RememberCodexResponsesCompatibility(
                settings.BaseUrl,
                settings.ApiKey,
                settings.Model,
                result.ResponsesSupported);
            if (!string.IsNullOrWhiteSpace(result.PreferredWireApi))
            {
                StatusMessage = $"已识别 Codex 接口链接方式：{result.PreferredWireApi}。";
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
            {
                StatusMessage = $"接口链接方式暂未识别：{result.Error}";
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("ProxyEndpointModelCache.DetectWireApi", ex);
            return null;
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
            cached?.ResponsesSupported == true ? cached.PreferredWireApi : null);
    }

    private sealed record CodexApplyCachedModelInfo(
        int? ContextWindow,
        string? PreferredWireApi);
}
