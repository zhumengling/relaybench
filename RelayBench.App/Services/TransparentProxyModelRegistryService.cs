using System.Text.RegularExpressions;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyModelRegistryService
{
    public TransparentProxyModelRegistrySnapshot BuildSnapshot(
        IReadOnlyList<TransparentProxyRoute> routes,
        IReadOnlyList<TransparentProxyRouteMetrics>? routeMetrics = null)
    {
        var metricsByRouteId = (routeMetrics ?? Array.Empty<TransparentProxyRouteMetrics>())
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        Dictionary<string, TransparentProxyModelPoolBuilder> builders = new(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < routes.Count; index++)
        {
            var route = routes[index];
            metricsByRouteId.TryGetValue(route.Id, out var metrics);
            var mappings = EnumerateVisibleMappings(route).ToArray();
            if (mappings.Length == 0)
            {
                AddPoolMember(
                    builders,
                    TransparentProxyModelPoolSnapshot.PassThroughModelName,
                    isPassThrough: true,
                    route,
                    index,
                    metrics,
                    upstreamModel: string.Empty,
                    clientModel: TransparentProxyModelPoolSnapshot.PassThroughModelName,
                    isPrefixedAlias: false);
                continue;
            }

            foreach (var mapping in mappings)
            {
                AddPoolMember(
                    builders,
                    mapping.ClientModel,
                    isPassThrough: false,
                    route,
                    index,
                    metrics,
                    mapping.UpstreamModel,
                    mapping.ClientModel,
                    isPrefixedAlias: false);

                var prefix = route.Prefix.Trim().Trim('/');
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    AddPoolMember(
                        builders,
                        $"{prefix}/{mapping.ClientModel}",
                        isPassThrough: false,
                        route,
                        index,
                        metrics,
                        mapping.UpstreamModel,
                        mapping.ClientModel,
                        isPrefixedAlias: true);
                }
            }
        }

        var pools = builders.Values
            .Select(static builder => builder.Build())
            .OrderBy(static pool => pool.IsPassThrough)
            .ThenByDescending(static pool => pool.RouteCount)
            .ThenBy(static pool => pool.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new TransparentProxyModelRegistrySnapshot(pools);
    }

    private static IEnumerable<(string UpstreamModel, string ClientModel)> EnumerateVisibleMappings(TransparentProxyRoute route)
    {
        foreach (var mapping in route.ModelMappings)
        {
            var upstreamModel = mapping.Name.Trim();
            var clientModel = mapping.EffectiveAlias.Trim();
            if (string.IsNullOrWhiteSpace(upstreamModel) || string.IsNullOrWhiteSpace(clientModel))
            {
                continue;
            }

            if (route.ExcludedModelPatterns.Any(pattern =>
                    WildcardMatch(upstreamModel, pattern) ||
                    WildcardMatch(clientModel, pattern)))
            {
                continue;
            }

            yield return (upstreamModel, clientModel);
        }
    }

    private static void AddPoolMember(
        Dictionary<string, TransparentProxyModelPoolBuilder> builders,
        string modelName,
        bool isPassThrough,
        TransparentProxyRoute route,
        int routeIndex,
        TransparentProxyRouteMetrics? metrics,
        string upstreamModel,
        string clientModel,
        bool isPrefixedAlias)
    {
        var normalizedModel = string.IsNullOrWhiteSpace(modelName)
            ? TransparentProxyModelPoolSnapshot.PassThroughModelName
            : modelName.Trim();
        if (!builders.TryGetValue(normalizedModel, out var builder))
        {
            builder = new TransparentProxyModelPoolBuilder(normalizedModel, isPassThrough);
            builders[normalizedModel] = builder;
        }

        var modelCooldown = ResolveModelCooldown(metrics, upstreamModel);
        builder.Add(new TransparentProxyModelPoolMemberSnapshot(
            route.Id,
            route.Name,
            route.BaseUrl,
            route.Prefix,
            route.Priority,
            routeIndex,
            upstreamModel,
            clientModel,
            isPrefixedAlias,
            metrics?.Sent ?? 0,
            metrics?.Success ?? 0,
            metrics?.Failed ?? 0,
            metrics?.LastStatusCode ?? 0,
            metrics?.LastLatencyMs ?? 0,
            metrics?.ConsecutiveFailures ?? 0,
            metrics?.CircuitState ?? "Closed",
            metrics?.CircuitOpenUntil ?? DateTimeOffset.MinValue,
            modelCooldown?.CooldownUntil ?? DateTimeOffset.MinValue,
            modelCooldown?.ConsecutiveFailures ?? 0,
            route.PreferredWireApi,
            route.ChatCompletionsSupported,
            route.ResponsesSupported,
            route.AnthropicMessagesSupported));
    }

    private static TransparentProxyModelCooldownSnapshot? ResolveModelCooldown(
        TransparentProxyRouteMetrics? metrics,
        string upstreamModel)
    {
        if (metrics?.ModelCooldowns is not { Count: > 0 } cooldowns ||
            string.IsNullOrWhiteSpace(upstreamModel))
        {
            return null;
        }

        var normalized = NormalizeModelKey(upstreamModel);
        return cooldowns.FirstOrDefault(item =>
            string.Equals(NormalizeModelKey(item.ModelName), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeModelKey(string? modelName)
    {
        var normalized = (modelName ?? string.Empty).Trim();
        var arrow = normalized.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            normalized = normalized[(arrow + 2)..].Trim();
        }

        return string.IsNullOrWhiteSpace(normalized) || normalized == "-"
            ? string.Empty
            : normalized.ToLowerInvariant();
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = "^" + string.Concat(pattern.Trim().Select(static character => character switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(character.ToString())
        })) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed class TransparentProxyModelPoolBuilder(string modelName, bool isPassThrough)
    {
        private readonly List<TransparentProxyModelPoolMemberSnapshot> _members = [];

        public void Add(TransparentProxyModelPoolMemberSnapshot member)
        {
            if (_members.Any(existing =>
                    string.Equals(existing.RouteId, member.RouteId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.UpstreamModel, member.UpstreamModel, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.ClientModel, member.ClientModel, StringComparison.OrdinalIgnoreCase) &&
                    existing.IsPrefixedAlias == member.IsPrefixedAlias))
            {
                return;
            }

            _members.Add(member);
        }

        public TransparentProxyModelPoolSnapshot Build()
        {
            var members = _members
                .OrderBy(static item => ResolvePrioritySort(item.Priority, item.RouteIndex))
                .ThenBy(static item => item.RouteIndex)
                .ThenBy(static item => item.UpstreamModel, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var healthyMembers = members.Count(static item => item.IsSchedulable);
            var routeCount = members
                .Select(static item => item.RouteId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var protocols = BuildProtocolSummary(members);

            return new TransparentProxyModelPoolSnapshot(
                modelName,
                isPassThrough,
                routeCount,
                members.Length,
                healthyMembers,
                members.Count(static item => string.Equals(item.CircuitState, "Open", StringComparison.OrdinalIgnoreCase)),
                members.Sum(static item => item.Sent),
                members.Sum(static item => item.Success),
                members.Sum(static item => item.Failed),
                members.Where(static item => item.LastLatencyMs > 0).Select(static item => item.LastLatencyMs).DefaultIfEmpty(0).Min(),
                protocols,
                members);
        }

        private static int ResolvePrioritySort(int configuredPriority, int routeIndex)
            => configuredPriority > 0 ? configuredPriority : 1_000 + routeIndex;

        private static string BuildProtocolSummary(IReadOnlyList<TransparentProxyModelPoolMemberSnapshot> members)
        {
            var responses = members.Any(static item => item.ResponsesSupported == true);
            var anthropic = members.Any(static item => item.AnthropicMessagesSupported == true);
            var chat = members.Any(static item => item.ChatCompletionsSupported == true);
            List<string> parts = [];
            if (responses)
            {
                parts.Add("Responses");
            }

            if (anthropic)
            {
                parts.Add("Anthropic");
            }

            if (chat)
            {
                parts.Add("OpenAI");
            }

            return parts.Count == 0 ? "待探测" : string.Join(" / ", parts);
        }
    }
}

public sealed record TransparentProxyModelRegistrySnapshot(
    IReadOnlyList<TransparentProxyModelPoolSnapshot> Pools);

public sealed record TransparentProxyModelPoolSnapshot(
    string ModelName,
    bool IsPassThrough,
    int RouteCount,
    int MemberCount,
    int HealthyMembers,
    int OpenCircuitMembers,
    int Sent,
    int Success,
    int Failed,
    long BestLatencyMs,
    string ProtocolSummary,
    IReadOnlyList<TransparentProxyModelPoolMemberSnapshot> Members)
{
    public const string PassThroughModelName = "*";
}

public sealed record TransparentProxyModelPoolMemberSnapshot(
    string RouteId,
    string RouteName,
    string BaseUrl,
    string Prefix,
    int Priority,
    int RouteIndex,
    string UpstreamModel,
    string ClientModel,
    bool IsPrefixedAlias,
    int Sent,
    int Success,
    int Failed,
    int LastStatusCode,
    long LastLatencyMs,
    int ConsecutiveFailures,
    string CircuitState,
    DateTimeOffset CircuitOpenUntil,
    DateTimeOffset ModelCooldownUntil,
    int ModelCooldownFailures,
    string? PreferredWireApi,
    bool? ChatCompletionsSupported,
    bool? ResponsesSupported,
    bool? AnthropicMessagesSupported)
{
    public bool IsModelCooling
        => ModelCooldownUntil > DateTimeOffset.UtcNow;

    public bool IsSchedulable
        => !string.Equals(CircuitState, "Open", StringComparison.OrdinalIgnoreCase) &&
           !IsModelCooling;
}
