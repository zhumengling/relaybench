namespace RelayBench.Core.Models;

public sealed record ExitIpRiskSourceResult(
    string Key,
    string DisplayName,
    string Category,
    bool Succeeded,
    string Verdict,
    string Summary,
    string Detail,
    bool? IsDatacenter = null,
    bool? IsProxy = null,
    bool? IsVpn = null,
    bool? IsTor = null,
    bool? IsAbuse = null,
    double? RiskScore = null,
    string? Country = null,
    string? City = null,
    string? Asn = null,
    string? Organization = null,
    string? Error = null);
