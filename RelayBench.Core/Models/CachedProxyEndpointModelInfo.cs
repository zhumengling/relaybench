namespace RelayBench.Core.Models;

public sealed record CachedProxyEndpointModelInfo(
    string BaseUrl,
    string Model,
    int? ContextWindow,
    string? PreferredWireApi,
    bool? ChatCompletionsSupported,
    bool? ResponsesSupported,
    DateTimeOffset CheckedAt);
