using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public static class ProxyProbeTraceBuilder
{
    public static ProxyProbeTrace Build(
        HttpClient client,
        string path,
        string payload,
        ProxyProbeScenarioResult result,
        string? responseBody,
        IReadOnlyList<string>? responseHeaders,
        string? extractedOutput)
    {
        var model = TryExtractPayloadString(payload, "model") ?? string.Empty;
        var requestHeaders = client.DefaultRequestHeaders
            .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}")
            .Append("Content-Type: application/json")
            .ToArray();

        return new ProxyProbeTrace(
            result.Scenario.ToString(),
            result.DisplayName,
            client.BaseAddress?.ToString().TrimEnd('/') ?? string.Empty,
            path,
            model,
            ResolveWireApiName(path),
            ProbeTraceRedactor.RedactJsonBody(payload),
            ProbeTraceRedactor.RedactHeaders(requestHeaders),
            result.StatusCode,
            ProbeTraceRedactor.RedactText(responseBody),
            ProbeTraceRedactor.RedactHeaders(responseHeaders),
            extractedOutput,
            Array.Empty<ProxyProbeEvaluationCheck>(),
            result.Success ? "通过" : "异常",
            result.Success ? null : result.Error ?? result.Summary,
            result.RequestId,
            result.TraceId,
            result.Latency is null ? null : (long)Math.Round(result.Latency.Value.TotalMilliseconds),
            result.FirstTokenLatency is null ? null : (long)Math.Round(result.FirstTokenLatency.Value.TotalMilliseconds),
            result.Duration is null ? null : (long)Math.Round(result.Duration.Value.TotalMilliseconds));
    }

    private static string ResolveWireApiName(string path)
    {
        if (path.Contains("chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenAI Chat Completions";
        }

        if (path.Contains("responses", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenAI Responses";
        }

        if (path.Contains("messages", StringComparison.OrdinalIgnoreCase))
        {
            return "Anthropic Messages";
        }

        return path;
    }

    private static string? TryExtractPayloadString(string payload, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty(propertyName, out var element) &&
                   element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
