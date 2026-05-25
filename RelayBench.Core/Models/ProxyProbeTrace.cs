namespace RelayBench.Core.Models;

public sealed record ProxyProbeTrace(
    string Scenario,
    string DisplayName,
    string BaseUrl,
    string Path,
    string Model,
    string WireApi,
    string RequestBody,
    IReadOnlyList<string> RequestHeaders,
    int? StatusCode,
    string? ResponseBody,
    IReadOnlyList<string> ResponseHeaders,
    string? ExtractedOutput,
    IReadOnlyList<ProxyProbeEvaluationCheck> Checks,
    string Verdict,
    string? FailureReason,
    string? RequestId,
    string? TraceId,
    long? LatencyMilliseconds,
    long? FirstTokenLatencyMilliseconds,
    long? DurationMilliseconds);

public sealed record ProxyProbeEvaluationCheck(
    string Name,
    bool Passed,
    string Expected,
    string Actual,
    string Detail);
