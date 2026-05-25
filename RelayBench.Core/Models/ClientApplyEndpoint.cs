namespace RelayBench.Core.Models;

public sealed record ClientApplyEndpoint(
    string BaseUrl,
    string ApiKey,
    string Model,
    string? DisplayName,
    int? ContextWindow,
    string? PreferredWireApi);
