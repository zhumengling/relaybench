namespace RelayBench.Core.Models;

public sealed record ProxyEndpointProtocolProbeResult(
    DateTimeOffset CheckedAt,
    string BaseUrl,
    string ProbeModel,
    bool ChatCompletionsSupported,
    bool ResponsesSupported,
    bool AnthropicMessagesSupported,
    string? PreferredWireApi,
    string Summary,
    string? Error);
