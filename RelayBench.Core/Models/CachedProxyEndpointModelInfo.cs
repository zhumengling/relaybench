namespace RelayBench.Core.Models;

public sealed record CachedProxyEndpointModelInfo(
    string BaseUrl,
    string Model,
    int? ContextWindow,
    string? PreferredWireApi,
    bool? ChatCompletionsSupported,
    bool? ResponsesSupported,
    bool? AnthropicMessagesSupported,
    DateTimeOffset CheckedAt);
