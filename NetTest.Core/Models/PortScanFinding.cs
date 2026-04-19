namespace NetTest.Core.Models;

public sealed record PortScanFinding(
    string Address,
    int Port,
    string Protocol,
    long ConnectLatencyMilliseconds,
    string ServiceHint,
    string? Banner,
    string? TlsSummary,
    string? HttpSummary,
    string? ProbeNotes)
{
    public string Endpoint
        => Address.Contains(':', StringComparison.Ordinal) && !Address.StartsWith("[", StringComparison.Ordinal)
            ? $"[{Address}]:{Port}"
            : $"{Address}:{Port}";

    public string LatencyText => $"{ConnectLatencyMilliseconds} ms";

    public string ApplicationSummary
        => HttpSummary ?? TlsSummary ?? Banner ?? string.Empty;
}
