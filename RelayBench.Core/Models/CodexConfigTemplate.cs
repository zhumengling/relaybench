namespace RelayBench.Core.Models;

public sealed record CodexConfigTemplate(
    string Model,
    string ModelProvider,
    int? ModelContextWindow,
    int? ModelAutoCompactTokenLimit,
    string ProviderName,
    string BaseUrl,
    string WireApi,
    string ExperimentalBearerToken,
    string HttpHeaders,
    int? RequestMaxRetries,
    int? StreamMaxRetries,
    int? StreamIdleTimeoutMs,
    IReadOnlyDictionary<string, string>? AdditionalRawSettings = null);
