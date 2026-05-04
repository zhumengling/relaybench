using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyProtocolDiscoveryService(
    ProxyDiagnosticsService proxyDiagnosticsService,
    ProxyEndpointModelCacheService modelCacheService)
{
    public async Task<TransparentProxyProtocolDiscoveryResult> DiscoverAsync(
        IReadOnlyList<TransparentProxyRoute> routes,
        TransparentProxyProtocolDiscoveryOptions options,
        IProgress<TransparentProxyProtocolDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<TransparentProxyRoute> hydratedRoutes = new(routes.Count);
        Dictionary<string, TransparentProxyProtocolDiscoverySnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
        var routeIndex = 0;
        var probedModels = 0;
        var cachedModels = 0;
        var skippedRoutes = 0;

        foreach (var route in routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            routeIndex++;
            progress?.Report(new TransparentProxyProtocolDiscoveryProgress(
                routeIndex,
                routes.Count,
                options.FetchCatalogModels ? route.Name : string.Empty));

            var modelNames = options.FetchCatalogModels
                ? await FetchRouteModelsAsync(route, options, cancellationToken)
                : BuildRouteProbeModels(route);
            var hydratedRoute = route.WithModels(modelNames);

            List<TransparentProxyProtocolDiscoverySnapshot> modelSnapshots = [];
            foreach (var model in modelNames)
            {
                var snapshot = await ResolveModelProtocolAsync(
                    hydratedRoute,
                    model,
                    options,
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

                modelSnapshots.Add(snapshot);
            }

            var routeSnapshot = BuildRouteProtocolSnapshot(modelSnapshots);
            if (routeSnapshot is null)
            {
                skippedRoutes++;
                hydratedRoutes.Add(hydratedRoute);
                continue;
            }

            snapshots[hydratedRoute.Id] = routeSnapshot;
            hydratedRoutes.Add(hydratedRoute.WithProtocol(
                routeSnapshot.PreferredWireApi,
                routeSnapshot.ChatCompletionsSupported,
                routeSnapshot.ResponsesSupported,
                routeSnapshot.AnthropicMessagesSupported,
                routeSnapshot.CheckedAt));
        }

        return new TransparentProxyProtocolDiscoveryResult(
            hydratedRoutes,
            snapshots,
            probedModels,
            cachedModels,
            skippedRoutes);
    }

    private async Task<IReadOnlyList<string>> FetchRouteModelsAsync(
        TransparentProxyRoute route,
        TransparentProxyProtocolDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        var fallbackModels = BuildRouteProbeModels(route);
        if (string.IsNullOrWhiteSpace(route.ApiKey))
        {
            return fallbackModels;
        }

        var settings = BuildEndpointSettings(route, route.Models.FirstOrDefault() ?? options.FallbackModel, options);
        var catalog = await proxyDiagnosticsService.FetchModelsAsync(settings, cancellationToken);
        await SaveCatalogAsync(settings, catalog, cancellationToken);
        if (!catalog.Success)
        {
            return fallbackModels;
        }

        var models = catalog.ModelItems is { Count: > 0 }
            ? catalog.ModelItems.Select(static item => item.Id)
            : catalog.Models;
        return models
            .Concat(route.Models)
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildRouteProbeModels(TransparentProxyRoute route)
        => route.Models.Count == 0
            ? Array.Empty<string>()
            : route.Models;

    private async Task<TransparentProxyProtocolDiscoverySnapshot?> ResolveModelProtocolAsync(
        TransparentProxyRoute route,
        string model,
        TransparentProxyProtocolDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(route.ApiKey) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        if (!options.ForceProbe)
        {
            var cached = await modelCacheService.TryResolveAsync(
                route.BaseUrl,
                route.ApiKey,
                model,
                cancellationToken);
            if (cached is not null &&
                (cached.ChatCompletionsSupported.HasValue ||
                 cached.ResponsesSupported.HasValue ||
                 cached.AnthropicMessagesSupported.HasValue))
            {
                return new TransparentProxyProtocolDiscoverySnapshot(
                    cached.PreferredWireApi,
                    cached.ChatCompletionsSupported,
                    cached.ResponsesSupported,
                    cached.AnthropicMessagesSupported,
                    cached.CheckedAt,
                    WasProbed: false);
            }
        }

        var settings = BuildEndpointSettings(route, model, options);
        var result = await proxyDiagnosticsService.ProbeProtocolAsync(settings, cancellationToken);
        await SaveProtocolProbeAsync(settings, result, cancellationToken);
        return new TransparentProxyProtocolDiscoverySnapshot(
            result.PreferredWireApi,
            result.ChatCompletionsSupported,
            result.ResponsesSupported,
            result.AnthropicMessagesSupported,
            result.CheckedAt,
            WasProbed: true);
    }

    private static ProxyEndpointSettings BuildEndpointSettings(
        TransparentProxyRoute route,
        string model,
        TransparentProxyProtocolDiscoveryOptions options)
        => new(
            route.BaseUrl,
            route.ApiKey,
            string.IsNullOrWhiteSpace(model) ? "relaybench-proxy" : model,
            options.IgnoreTlsErrors,
            options.UpstreamTimeoutSeconds);

    private static TransparentProxyProtocolDiscoverySnapshot? BuildRouteProtocolSnapshot(
        IReadOnlyList<TransparentProxyProtocolDiscoverySnapshot> modelSnapshots)
    {
        if (modelSnapshots.Count == 0)
        {
            return null;
        }

        var responsesSupported = MergeProtocolSupport(modelSnapshots.Select(static item => item.ResponsesSupported));
        var anthropicSupported = MergeProtocolSupport(modelSnapshots.Select(static item => item.AnthropicMessagesSupported));
        var chatSupported = MergeProtocolSupport(modelSnapshots.Select(static item => item.ChatCompletionsSupported));
        var preferredWireApi = responsesSupported == true
            ? ProxyWireApiProbeService.ResponsesWireApi
            : anthropicSupported == true
                ? ProxyWireApiProbeService.AnthropicMessagesWireApi
                : chatSupported == true
                    ? ProxyWireApiProbeService.ChatCompletionsWireApi
                    : modelSnapshots
                        .Select(static item => ProxyWireApiProbeService.NormalizeWireApi(item.PreferredWireApi))
                        .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item));
        return new TransparentProxyProtocolDiscoverySnapshot(
            preferredWireApi,
            chatSupported,
            responsesSupported,
            anthropicSupported,
            modelSnapshots.Max(static item => item.CheckedAt),
            modelSnapshots.Any(static item => item.WasProbed));
    }

    private static bool? MergeProtocolSupport(IEnumerable<bool?> values)
    {
        var sawFalse = false;
        foreach (var value in values)
        {
            if (value == true)
            {
                return true;
            }

            sawFalse |= value == false;
        }

        return sawFalse ? false : null;
    }

    private async Task SaveCatalogAsync(
        ProxyEndpointSettings settings,
        ProxyModelCatalogResult catalog,
        CancellationToken cancellationToken)
    {
        try
        {
            await modelCacheService.SaveCatalogAsync(settings, catalog, cancellationToken);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxyProtocolDiscovery.SaveCatalog", ex);
        }
    }

    private async Task SaveProtocolProbeAsync(
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await modelCacheService.SaveProtocolProbeAsync(settings, result, cancellationToken);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxyProtocolDiscovery.SaveProtocolProbe", ex);
        }
    }
}

internal sealed record TransparentProxyProtocolDiscoveryOptions(
    bool ForceProbe,
    bool FetchCatalogModels,
    bool IgnoreTlsErrors,
    int UpstreamTimeoutSeconds,
    string FallbackModel);

internal sealed record TransparentProxyProtocolDiscoveryProgress(
    int CurrentRoute,
    int TotalRoutes,
    string RouteName);

internal sealed record TransparentProxyProtocolDiscoveryResult(
    IReadOnlyList<TransparentProxyRoute> HydratedRoutes,
    IReadOnlyDictionary<string, TransparentProxyProtocolDiscoverySnapshot> Snapshots,
    int ProbedModels,
    int CachedModels,
    int SkippedRoutes);

internal sealed record TransparentProxyProtocolDiscoverySnapshot(
    string? PreferredWireApi,
    bool? ChatCompletionsSupported,
    bool? ResponsesSupported,
    bool? AnthropicMessagesSupported,
    DateTimeOffset CheckedAt,
    bool WasProbed);
