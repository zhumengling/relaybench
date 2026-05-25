namespace RelayBench.Services;

internal sealed class TransparentProxyRoutePolicyService
{
    public IReadOnlyList<TransparentProxyRoute> OrderCandidateRoutes(
        TransparentProxyServerConfig config,
        IReadOnlyList<TransparentProxyRoutePolicyCandidate> available,
        string? boundRouteId,
        ref int roundRobinCursor)
    {
        if (available.Count == 0)
        {
            return Array.Empty<TransparentProxyRoute>();
        }

        var strategy = TransparentProxyRouteStrategies.Normalize(config.RouteStrategy);
        if (string.Equals(strategy, TransparentProxyRouteStrategies.RoundRobin, StringComparison.Ordinal))
        {
            var ordered = available
                .OrderBy(static item => ResolveConfiguredPrioritySort(ResolveEffectivePriority(item.Route), item.Index))
                .ThenBy(static item => item.Index)
                .Select(static item => item.Route)
                .ToArray();
            var offset = Math.Abs(roundRobinCursor++ % ordered.Length);
            return ordered.Skip(offset).Concat(ordered.Take(offset)).ToArray();
        }

        if (string.Equals(strategy, TransparentProxyRouteStrategies.LowestLatency, StringComparison.Ordinal))
        {
            return available
                .OrderBy(static item => item.State?.LastLatencyMs > 0 ? item.State.LastLatencyMs : long.MaxValue)
                .ThenBy(static item => ResolveConfiguredPrioritySort(ResolveEffectivePriority(item.Route), item.Index))
                .ThenBy(static item => item.Index)
                .Select(static item => item.Route)
                .ToArray();
        }

        if (string.Equals(strategy, TransparentProxyRouteStrategies.SessionAffinity, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(boundRouteId) &&
            available.Any(item => string.Equals(item.Route.Id, boundRouteId, StringComparison.OrdinalIgnoreCase)))
        {
            return available
                .OrderBy(item => string.Equals(item.Route.Id, boundRouteId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(static item => CalculateRouteScheduleScore(item.State, item.Index, ResolveEffectivePriority(item.Route)))
                .ThenBy(static item => item.Index)
                .Select(static item => item.Route)
                .ToArray();
        }

        if (string.Equals(strategy, TransparentProxyRouteStrategies.Priority, StringComparison.Ordinal) ||
            string.Equals(strategy, TransparentProxyRouteStrategies.FillFirst, StringComparison.Ordinal) ||
            string.Equals(strategy, TransparentProxyRouteStrategies.SessionAffinity, StringComparison.Ordinal))
        {
            return available
                .OrderBy(static item => ResolveConfiguredPrioritySort(ResolveEffectivePriority(item.Route), item.Index))
                .ThenBy(static item => item.State?.CircuitState == TransparentProxyCircuitState.HalfOpen ? 1 : 0)
                .ThenBy(static item => item.Index)
                .Select(static item => item.Route)
                .ToArray();
        }

        return available
            .OrderBy(static item => CalculateRouteScheduleScore(item.State, item.Index, ResolveEffectivePriority(item.Route)))
            .ThenBy(static item => item.Index)
            .Select(static item => item.Route)
            .ToArray();
    }

    private static int ResolveEffectivePriority(TransparentProxyRoute route)
        => route.RuntimePriority > 0 ? route.RuntimePriority : route.Priority;

    private static int ResolveConfiguredPrioritySort(int configuredPriority, int routeIndex)
        => configuredPriority > 0 ? configuredPriority : 1_000 + routeIndex;

    private static double CalculateRouteScheduleScore(
        TransparentProxyRouteRuntimeState? state,
        int routeIndex,
        int configuredPriority)
    {
        var score = routeIndex * 8d;
        if (configuredPriority > 0)
        {
            score += configuredPriority * 32d;
        }

        if (state is null || state.Sent <= 0)
        {
            return score;
        }

        if (state.CircuitState == TransparentProxyCircuitState.HalfOpen)
        {
            score += 1_000d;
        }

        var windowRequests = state.CircuitWindowRequests > 0 ? state.CircuitWindowRequests : state.Sent;
        var windowFailures = state.CircuitWindowRequests > 0 ? state.CircuitWindowFailures : state.Failed;
        var failureRate = windowFailures / (double)Math.Max(1, windowRequests);
        score += Math.Clamp(failureRate, 0d, 1d) * 120d;
        score += Math.Min(6, state.ConsecutiveFailures) * 80d;

        if (state.LastLatencyMs > 0)
        {
            score += Math.Clamp(state.LastLatencyMs / 35d, 0d, 80d);
        }

        return score;
    }
}

internal sealed record TransparentProxyRoutePolicyCandidate(
    TransparentProxyRoute Route,
    int Index,
    TransparentProxyRouteRuntimeState? State);
