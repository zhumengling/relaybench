namespace RelayBench.Services;

internal sealed class TransparentProxySchedulerService
{
    private readonly TransparentProxyRoutePolicyService _routePolicy;

    public TransparentProxySchedulerService(TransparentProxyRoutePolicyService? routePolicy = null)
    {
        _routePolicy = routePolicy ?? new TransparentProxyRoutePolicyService();
    }

    public IReadOnlyList<TransparentProxyRoute> BuildCandidateRoutes(
        TransparentProxyServerConfig config,
        string? requestedModel,
        string? boundRouteId,
        IReadOnlyDictionary<string, TransparentProxyRouteRuntimeState> routeStates,
        TransparentProxyCircuitBreakerService circuitBreaker,
        DateTimeOffset now,
        ref int roundRobinCursor)
    {
        var explicitPoolExists = HasExplicitRouteModelPool(config.Routes, requestedModel);
        if (!config.EnableFallback || config.Routes.Count <= 1)
        {
            var servingRoutes = config.Routes
                .Where(route => CanRouteServeModel(route, requestedModel))
                .ToArray();
            var preferredRoutes = explicitPoolExists
                ? servingRoutes.Where(route => HasExplicitRouteModelMatch(route, requestedModel)).ToArray()
                : servingRoutes;
            return (preferredRoutes.Length > 0 ? preferredRoutes : servingRoutes)
                .Take(1)
                .ToArray();
        }

        List<TransparentProxyRoutePolicyCandidate> modelPoolCandidates = [];
        List<TransparentProxyRoutePolicyCandidate> passThroughFallbackCandidates = [];
        for (var index = 0; index < config.Routes.Count; index++)
        {
            var route = config.Routes[index];
            if (!CanRouteServeModel(route, requestedModel))
            {
                continue;
            }

            if (routeStates.TryGetValue(route.Id, out var state) &&
                !circuitBreaker.IsRouteAvailable(state, now))
            {
                continue;
            }

            var candidate = new TransparentProxyRoutePolicyCandidate(route, index, state);
            if (explicitPoolExists && !HasExplicitRouteModelMatch(route, requestedModel))
            {
                passThroughFallbackCandidates.Add(candidate);
                continue;
            }

            modelPoolCandidates.Add(candidate);
        }

        var ordered = _routePolicy.OrderCandidateRoutes(
            config,
            modelPoolCandidates,
            boundRouteId,
            ref roundRobinCursor);
        if (!explicitPoolExists || passThroughFallbackCandidates.Count == 0)
        {
            return ordered;
        }

        var fallbackOrdered = _routePolicy.OrderCandidateRoutes(
            config,
            passThroughFallbackCandidates,
            null,
            ref roundRobinCursor);
        return ordered.Concat(fallbackOrdered).ToArray();
    }

    public static bool CanRouteServeModel(TransparentProxyRoute route, string? requestedModel)
        => TransparentProxyModelAliasResolver.CanRouteServeModel(route, requestedModel);

    public static bool HasExplicitRouteModelMatch(TransparentProxyRoute route, string? requestedModel)
        => TransparentProxyModelAliasResolver.HasExplicitRouteModelMatch(route, requestedModel);

    public static bool HasExplicitRouteModelPool(
        IReadOnlyList<TransparentProxyRoute> routes,
        string? requestedModel)
        => !string.IsNullOrWhiteSpace(requestedModel) &&
           routes.Any(route => route.Models.Count > 0 && HasExplicitRouteModelMatch(route, requestedModel));

}
