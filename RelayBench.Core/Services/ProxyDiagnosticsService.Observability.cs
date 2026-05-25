using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private sealed record TraceabilityObservation(
        string? RequestId,
        string? TraceId,
        string Summary);

    private static TraceabilityObservation BuildTraceabilityObservation(IEnumerable<ProxyProbeScenarioResult> scenarioResults)
    {
        var headers = scenarioResults
            .SelectMany(static result => result.ResponseHeaders ?? Array.Empty<string>())
            .ToArray();

        var requestId = ExtractHeaderValue(headers,
            "x-request-id",
            "request-id",
            "openai-request-id",
            "anthropic-request-id");

        var traceId = ExtractHeaderValue(headers,
            "trace-id",
            "x-trace-id",
            "cf-ray",
            "x-amzn-trace-id");

        var summary = BuildTraceabilitySummary(requestId, traceId, headers);
        return new TraceabilityObservation(requestId, traceId, summary);
    }

    private static string? ExtractRequestId(IReadOnlyList<string>? headers)
        => headers is null
            ? null
            : ExtractHeaderValue(headers,
                "x-request-id",
                "request-id",
                "openai-request-id",
                "anthropic-request-id");

    private static string? ExtractTraceId(IReadOnlyList<string>? headers)
        => headers is null
            ? null
            : ExtractHeaderValue(headers,
                "trace-id",
                "x-trace-id",
                "cf-ray",
                "x-amzn-trace-id");

    private static string BuildTraceabilitySummary(
        string? requestId,
        string? traceId,
        IReadOnlyList<string>? headers)
    {
        if (!string.IsNullOrWhiteSpace(requestId) && !string.IsNullOrWhiteSpace(traceId))
        {
            return $"良好：Request-ID {AbbreviateId(requestId)}；Trace-ID {AbbreviateId(traceId)}";
        }

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            return $"良好：Request-ID {AbbreviateId(requestId)}";
        }

        if (!string.IsNullOrWhiteSpace(traceId))
        {
            return $"一般：Trace-ID {AbbreviateId(traceId)}";
        }

        var serverHeader = headers is null ? null : ExtractHeaderValue(headers, "server", "via");
        return string.IsNullOrWhiteSpace(serverHeader)
            ? "缺失：未在响应头里看到 Request-ID / Trace-ID。"
            : $"缺失：仅观察到 server/via 信息（{serverHeader}）。";
    }

    private static string? ExtractHeaderValue(IEnumerable<string> headers, params string[] headerNames)
    {
        foreach (var headerName in headerNames)
        {
            var value = GetHeaderValues(headers, headerName).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string AbbreviateId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length <= 18)
        {
            return normalized;
        }

        return $"{normalized[..12]}...{normalized[^4..]}";
    }
}
