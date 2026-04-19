namespace NetTest.Core.Models;

public sealed record ChatGptTraceResult(
    DateTimeOffset CheckedAt,
    string RawTrace,
    IReadOnlyDictionary<string, string> Values,
    string? PublicIp,
    string? LocationCode,
    string? LocationName,
    string? CloudflareColo,
    bool IsSupportedRegion,
    string SupportSummary,
    string? Error);
