namespace NetTest.Core.Models;

public sealed record ProxyModelCatalogResult(
    DateTimeOffset CheckedAt,
    string BaseUrl,
    bool Success,
    int? StatusCode,
    int ModelCount,
    IReadOnlyList<string> Models,
    TimeSpan? Latency,
    string Summary,
    string? Error,
    IReadOnlyList<string>? ResponseHeaders = null,
    IReadOnlyList<string>? ResolvedAddresses = null,
    string? CdnProvider = null,
    string? EdgeSignature = null,
    string? CdnSummary = null,
    string? RequestId = null,
    string? TraceId = null,
    string? TraceabilitySummary = null);
