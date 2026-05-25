using RelayBench.Services.Infrastructure;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.Services;

internal sealed class TransparentProxyProtocolDiscoveryService(
    ProxyDiagnosticsService proxyDiagnosticsService,
    ProxyEndpointModelCacheService modelCacheService,
    ProxyEndpointProtocolProbeService protocolProbeService)
{
    private static readonly TimeSpan MinimumDiscoveryOperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumDiscoveryOperationTimeout = TimeSpan.FromSeconds(20);
    private const int MaxProtocolProbeModelsPerRoute = 3;
    private const int MaxConcurrentRouteDiscoveries = 4;

    public async Task<TransparentProxyProtocolDiscoveryResult> DiscoverAsync(
        IReadOnlyList<TransparentProxyRoute> routes,
        TransparentProxyProtocolDiscoveryOptions options,
        IProgress<TransparentProxyProtocolDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new TransparentProxyRouteDiscoveryOutcome?[routes.Count];
        var completedRoutes = 0;
        using SemaphoreSlim routeGate = new(ResolveRouteDiscoveryConcurrency(routes.Count));

        var tasks = routes
            .Select((route, index) => DiscoverRouteWithGateAsync(
                route,
                index,
                routes.Count,
                options,
                progress,
                outcomes,
                routeGate,
                () => Interlocked.Increment(ref completedRoutes),
                cancellationToken))
            .ToArray();
        await Task.WhenAll(tasks);

        List<TransparentProxyRoute> hydratedRoutes = new(routes.Count);
        Dictionary<string, TransparentProxyProtocolDiscoverySnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
        var probedModels = 0;
        var cachedModels = 0;
        var skippedRoutes = 0;

        foreach (var outcome in outcomes)
        {
            if (outcome is null)
            {
                continue;
            }

            hydratedRoutes.Add(outcome.HydratedRoute);
            probedModels += outcome.ProbedModels;
            cachedModels += outcome.CachedModels;
            if (outcome.Skipped)
            {
                skippedRoutes++;
                continue;
            }

            if (outcome.Snapshot is not null)
            {
                snapshots[outcome.HydratedRoute.Id] = outcome.Snapshot;
            }
        }

        return new TransparentProxyProtocolDiscoveryResult(
            hydratedRoutes,
            snapshots,
            probedModels,
            cachedModels,
            skippedRoutes);
    }

    private async Task DiscoverRouteWithGateAsync(
        TransparentProxyRoute route,
        int routeIndex,
        int totalRoutes,
        TransparentProxyProtocolDiscoveryOptions options,
        IProgress<TransparentProxyProtocolDiscoveryProgress>? progress,
        TransparentProxyRouteDiscoveryOutcome?[] outcomes,
        SemaphoreSlim routeGate,
        Func<int> markCompleted,
        CancellationToken cancellationToken)
    {
        await routeGate.WaitAsync(cancellationToken);
        try
        {
            outcomes[routeIndex] = await DiscoverRouteAsync(route, routeIndex, options, cancellationToken);
        }
        finally
        {
            routeGate.Release();
            var completed = markCompleted();
            progress?.Report(new TransparentProxyProtocolDiscoveryProgress(
                completed,
                totalRoutes,
                options.FetchCatalogModels ? route.Name : string.Empty));
        }
    }

    private async Task<TransparentProxyRouteDiscoveryOutcome> DiscoverRouteAsync(
        TransparentProxyRoute route,
        int routeIndex,
        TransparentProxyProtocolDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelNames = options.FetchCatalogModels
                ? await FetchRouteModelsSafelyAsync(route, options, cancellationToken)
                : BuildRouteProbeModels(route);
            var hydratedRoute = modelNames.Count > 0
                ? route.WithModels(modelNames)
                : route;
            var probeModels = BuildProtocolProbeModels(modelNames, route, options);

            List<TransparentProxyProtocolDiscoverySnapshot> modelSnapshots = [];
            var probedModels = 0;
            var cachedModels = 0;
            foreach (var model in probeModels)
            {
                var snapshot = await ResolveModelProtocolSafelyAsync(
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
                return new TransparentProxyRouteDiscoveryOutcome(
                    routeIndex,
                    hydratedRoute,
                    null,
                    probedModels,
                    cachedModels,
                    Skipped: true);
            }

            return new TransparentProxyRouteDiscoveryOutcome(
                routeIndex,
                hydratedRoute.WithProtocol(
                    routeSnapshot.PreferredWireApi,
                    routeSnapshot.ChatCompletionsSupported,
                    routeSnapshot.ResponsesSupported,
                    routeSnapshot.AnthropicMessagesSupported,
                    routeSnapshot.CheckedAt),
                routeSnapshot,
                probedModels,
                cachedModels,
                Skipped: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxyProtocolDiscovery.DiscoverRoute", ex);
            return new TransparentProxyRouteDiscoveryOutcome(
                routeIndex,
                route,
                null,
                ProbedModels: 0,
                CachedModels: 0,
                Skipped: true);
        }
    }

    private async Task<IReadOnlyList<string>> FetchRouteModelsSafelyAsync(
        TransparentProxyRoute route,
        TransparentProxyProtocolDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ResolveDiscoveryOperationTimeout(options));
            return await FetchRouteModelsAsync(route, options, timeoutSource.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppDiagnosticLog.Write("TransparentProxyProtocolDiscovery.FetchRouteModelsTimeout", ex);
            return BuildRouteProbeModels(route);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxyProtocolDiscovery.FetchRouteModels", ex);
            return BuildRouteProbeModels(route);
        }
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

    private static IReadOnlyList<string> BuildProtocolProbeModels(
        IReadOnlyList<string> fetchedModels,
        TransparentProxyRoute route,
        TransparentProxyProtocolDiscoveryOptions options)
    {
        IReadOnlyList<string> primaryModels = route.Models.Count > 0
                ? route.Models
                : string.IsNullOrWhiteSpace(options.FallbackModel)
                    ? Array.Empty<string>()
                    : new[] { options.FallbackModel };

        return primaryModels
            .Concat(fetchedModels)
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxProtocolProbeModelsPerRoute)
            .ToArray();
    }

    private async Task<TransparentProxyProtocolDiscoverySnapshot?> ResolveModelProtocolSafelyAsync(
        TransparentProxyRoute route,
        string model,
        TransparentProxyProtocolDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ResolveDiscoveryOperationTimeout(options));
            return await ResolveModelProtocolAsync(route, model, options, timeoutSource.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppDiagnosticLog.Write("TransparentProxyProtocolDiscovery.ResolveModelProtocolTimeout", ex);
            return BuildFailedProtocolSnapshot();
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxyProtocolDiscovery.ResolveModelProtocol", ex);
            return BuildFailedProtocolSnapshot();
        }
    }

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

        var settings = BuildEndpointSettings(route, model, options);
        var resolution = await protocolProbeService.ResolveAsync(
            settings,
            new ProxyEndpointProtocolProbeOptions(
                ForceProbe: options.ForceProbe,
                UseCache: !options.ForceProbe,
                SaveResult: true),
            cancellationToken);
        var result = resolution.Result;
        return new TransparentProxyProtocolDiscoverySnapshot(
            result.PreferredWireApi,
            result.ChatCompletionsSupported,
            result.ResponsesSupported,
            result.AnthropicMessagesSupported,
            result.CheckedAt,
            WasProbed: !resolution.FromCache);
    }

    private static TransparentProxyProtocolDiscoverySnapshot BuildFailedProtocolSnapshot()
        => new(
            PreferredWireApi: null,
            ChatCompletionsSupported: false,
            ResponsesSupported: false,
            AnthropicMessagesSupported: false,
            CheckedAt: DateTimeOffset.Now,
            WasProbed: true);

    private static TimeSpan ResolveDiscoveryOperationTimeout(TransparentProxyProtocolDiscoveryOptions options)
    {
        var requestedTimeout = TimeSpan.FromSeconds(Math.Max(1, options.UpstreamTimeoutSeconds));
        if (requestedTimeout < MinimumDiscoveryOperationTimeout)
        {
            return MinimumDiscoveryOperationTimeout;
        }

        return requestedTimeout > MaximumDiscoveryOperationTimeout
            ? MaximumDiscoveryOperationTimeout
            : requestedTimeout;
    }

    private static int ResolveRouteDiscoveryConcurrency(int routeCount)
        => Math.Clamp(routeCount, 1, MaxConcurrentRouteDiscoveries);

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

internal sealed record TransparentProxyRouteDiscoveryOutcome(
    int RouteIndex,
    TransparentProxyRoute HydratedRoute,
    TransparentProxyProtocolDiscoverySnapshot? Snapshot,
    int ProbedModels,
    int CachedModels,
    bool Skipped);

internal sealed record TransparentProxyProtocolDiscoverySnapshot(
    string? PreferredWireApi,
    bool? ChatCompletionsSupported,
    bool? ResponsesSupported,
    bool? AnthropicMessagesSupported,
    DateTimeOffset CheckedAt,
    bool WasProbed);
