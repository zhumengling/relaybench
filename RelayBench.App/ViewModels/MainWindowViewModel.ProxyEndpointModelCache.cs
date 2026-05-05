using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

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
            RememberCodexWireApiCompatibility(
                settings.BaseUrl,
                settings.ApiKey,
                FirstNonEmpty(result.EffectiveModel, result.RequestedModel, settings.Model),
                responsesSupported || result.ChatRequestSucceeded);
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
            StatusMessage = "正在检测接口链接方式（chat / responses / Anthropic messages）...";
            var resolution = await _proxyEndpointProtocolProbeService.ResolveAsync(
                settings,
                new ProxyEndpointProtocolProbeOptions(
                    ForceProbe: forceProbe,
                    UseCache: true,
                    SaveResult: true),
                cancellationToken);
            var result = resolution.Result;
            RememberCodexWireApiCompatibility(
                settings.BaseUrl,
                settings.ApiKey,
                settings.Model,
                result.ResponsesSupported || result.ChatCompletionsSupported);
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

        var hasCurrentProtocolProbe =
            (cached?.ProtocolProbeVersion ?? 0) >= ProxyWireApiProbeService.CurrentProtocolProbeVersion;
        return new CodexApplyCachedModelInfo(
            cached?.ContextWindow ?? ResolveProxyModelContextWindow(model),
            hasCurrentProtocolProbe &&
            (cached is { ResponsesSupported: true } or { ChatCompletionsSupported: true })
                ? cached.PreferredWireApi
                : null);
    }

    private sealed record CodexApplyCachedModelInfo(
        int? ContextWindow,
        string? PreferredWireApi);

}
