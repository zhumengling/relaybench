namespace RelayBench.Core.Models;

public sealed record ExitIpRiskReviewResult(
    DateTimeOffset CheckedAt,
    string? PublicIp,
    string DetectSource,
    string? Country,
    string? City,
    string? Asn,
    string? Organization,
    string? CloudflareColo,
    IReadOnlyList<ExitIpRiskSourceResult> Sources,
    IReadOnlyList<string> RiskSignals,
    IReadOnlyList<string> PositiveSignals,
    string Verdict,
    string Summary,
    string? Error);
