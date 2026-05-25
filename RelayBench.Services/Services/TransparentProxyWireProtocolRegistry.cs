using RelayBench.Core.Services;

namespace RelayBench.Services;

internal sealed class TransparentProxyWireProtocolRegistry
{
    private static readonly TransparentProxyWireProtocolDescriptor[] OrderedProtocols =
    [
        new(
            ProxyWireApiProbeService.ResponsesWireApi,
            "Responses",
            static route => route.ResponsesSupported),
        new(
            ProxyWireApiProbeService.AnthropicMessagesWireApi,
            "Anthropic Messages",
            static route => route.AnthropicMessagesSupported),
        new(
            ProxyWireApiProbeService.ChatCompletionsWireApi,
            "OpenAI Chat",
            static route => route.ChatCompletionsSupported)
    ];

    public IReadOnlyList<string> BuildWireApiAttempts(TransparentProxyRoute route)
        => BuildProtocolAttempts(route)
            .Select(static protocol => protocol.WireApi)
            .ToArray();

    public IReadOnlyList<TransparentProxyWireProtocolDescriptor> BuildProtocolAttempts(TransparentProxyRoute route)
    {
        var hasProtocolProbe =
            route.ResponsesSupported.HasValue ||
            route.AnthropicMessagesSupported.HasValue ||
            route.ChatCompletionsSupported.HasValue;

        List<TransparentProxyWireProtocolDescriptor> candidates = [];
        foreach (var protocol in OrderedProtocols)
        {
            var supported = protocol.ReadSupport(route);
            if (!hasProtocolProbe || supported == true)
            {
                AddProtocolCandidate(candidates, protocol);
            }
        }

        if (candidates.Count == 0 ||
            !candidates.Any(static item => string.Equals(
                item.WireApi,
                ProxyWireApiProbeService.ChatCompletionsWireApi,
                StringComparison.Ordinal)))
        {
            var chat = OrderedProtocols.First(static item => string.Equals(
                item.WireApi,
                ProxyWireApiProbeService.ChatCompletionsWireApi,
                StringComparison.Ordinal));
            if (!hasProtocolProbe || route.ChatCompletionsSupported != false || candidates.Count == 0)
            {
                AddProtocolCandidate(candidates, chat);
            }
        }

        return candidates;
    }

    private static void AddProtocolCandidate(
        List<TransparentProxyWireProtocolDescriptor> candidates,
        TransparentProxyWireProtocolDescriptor descriptor)
    {
        var normalized = ProxyWireApiProbeService.NormalizeWireApi(descriptor.WireApi);
        if (string.IsNullOrWhiteSpace(normalized) ||
            candidates.Any(item => string.Equals(item.WireApi, normalized, StringComparison.Ordinal)))
        {
            return;
        }

        candidates.Add(descriptor with { WireApi = normalized });
    }
}

internal sealed record TransparentProxyWireProtocolDescriptor(
    string WireApi,
    string DisplayName,
    Func<TransparentProxyRoute, bool?> ReadSupport);
