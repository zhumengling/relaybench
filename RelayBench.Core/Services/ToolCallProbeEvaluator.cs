using System.Globalization;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed record ToolCallExpectation(
    string Name,
    IReadOnlyDictionary<string, object?> Arguments);

public sealed record ToolCallProbeEvaluation(
    bool Success,
    string Summary,
    string? Error,
    string? NormalizedPreview,
    IReadOnlyList<ProxyProbeEvaluationCheck> Checks);

public static class ToolCallProbeEvaluator
{
    public static ToolCallProbeEvaluation Evaluate(
        string scenarioId,
        string responseBody,
        IReadOnlyList<ToolCallExpectation> expectations)
    {
        List<ProxyProbeEvaluationCheck> checks = [];
        if (expectations.Count == 0)
        {
            return Fail("No tool call expectation was configured.", "Tool call probe has no expectation.", responseBody, checks);
        }

        if (!TryExtractToolCalls(responseBody, out var calls, out var parseError))
        {
            checks.Add(new ProxyProbeEvaluationCheck(
                "ToolSelection",
                false,
                expectations[0].Name,
                "no tool_calls",
                "The response did not contain a compatible tool call shape."));
            return Fail("ToolCall deep probe failed: no compatible tool_calls were returned.", parseError, responseBody, checks);
        }

        var expected = expectations[0];
        var actual = calls[0];
        var nameMatches = string.Equals(actual.Name, expected.Name, StringComparison.Ordinal);
        checks.Add(new ProxyProbeEvaluationCheck(
            "ToolSelection",
            nameMatches,
            expected.Name,
            actual.Name,
            nameMatches ? "The model selected the expected tool." : "The model selected a different tool."));

        Dictionary<string, JsonElement>? actualArgs = null;
        if (nameMatches)
        {
            actualArgs = ParseArguments(actual.ArgumentsJson);
        }

        var argsPass = actualArgs is not null;
        if (actualArgs is null)
        {
            checks.Add(new ProxyProbeEvaluationCheck(
                "ArgumentPrecision",
                false,
                FormatExpectedArguments(expected.Arguments),
                actual.ArgumentsJson,
                "Tool arguments are not valid JSON."));
        }
        else
        {
            foreach (var pair in expected.Arguments)
            {
                var argPassed = actualArgs.TryGetValue(pair.Key, out var actualValue) &&
                                JsonValueEquals(actualValue, pair.Value);
                argsPass &= argPassed;
                checks.Add(new ProxyProbeEvaluationCheck(
                    $"Argument:{pair.Key}",
                    argPassed,
                    FormatExpectedValue(pair.Value),
                    actualArgs.TryGetValue(pair.Key, out actualValue) ? actualValue.GetRawText() : "<missing>",
                    argPassed ? "Argument name, type and value match." : "Argument name, type or value drifted."));
            }
        }

        var singleCall = calls.Count == expectations.Count;
        checks.Add(new ProxyProbeEvaluationCheck(
            "CallDiscipline",
            singleCall,
            expectations.Count.ToString(CultureInfo.InvariantCulture),
            calls.Count.ToString(CultureInfo.InvariantCulture),
            singleCall ? "The response contains the expected number of tool calls." : "The response contains extra or missing tool calls."));

        var success = nameMatches && argsPass && singleCall;
        return success
            ? new ToolCallProbeEvaluation(
                true,
                $"{scenarioId} passed: tool name and arguments are compatible.",
                null,
                BuildNormalizedPreview(actual),
                checks)
            : Fail(
                $"{scenarioId} failed: tool call shape or arguments did not match.",
                "The response cannot be consumed reliably by Agent/Codex-style tool clients.",
                BuildNormalizedPreview(actual),
                checks);
    }

    private static ToolCallProbeEvaluation Fail(
        string summary,
        string? error,
        string? preview,
        IReadOnlyList<ProxyProbeEvaluationCheck> checks)
        => new(false, summary, error, string.IsNullOrWhiteSpace(preview) ? null : preview.Trim(), checks);

    private static bool TryExtractToolCalls(
        string responseBody,
        out IReadOnlyList<ToolCallProbeCall> calls,
        out string? error)
    {
        calls = Array.Empty<ToolCallProbeCall>();
        error = null;

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            error = "Response body is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            List<ToolCallProbeCall> parsed = [];
            ExtractOpenAiToolCalls(document.RootElement, parsed);
            ExtractAnthropicToolCalls(document.RootElement, parsed);
            ExtractResponsesToolCalls(document.RootElement, parsed);

            calls = parsed;
            if (parsed.Count == 0)
            {
                error = "tool call array is missing or empty.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Response JSON parse failed: {ex.Message}";
            return false;
        }
    }

    private static void ExtractOpenAiToolCalls(JsonElement root, List<ToolCallProbeCall> parsed)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("tool_calls", out var toolCalls) ||
                toolCalls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in toolCalls.EnumerateArray())
            {
                if (!item.TryGetProperty("function", out var function) ||
                    function.ValueKind != JsonValueKind.Object ||
                    !function.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                parsed.Add(new ToolCallProbeCall(
                    nameElement.GetString() ?? string.Empty,
                    TryReadArgumentsJson(function, "arguments")));
            }
        }
    }

    private static void ExtractAnthropicToolCalls(JsonElement root, List<ToolCallProbeCall> parsed)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase) ||
                !item.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            parsed.Add(new ToolCallProbeCall(
                nameElement.GetString() ?? string.Empty,
                TryReadArgumentsJson(item, "input")));
        }
    }

    private static void ExtractResponsesToolCalls(JsonElement root, List<ToolCallProbeCall> parsed)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "function_call", StringComparison.OrdinalIgnoreCase) ||
                !item.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            parsed.Add(new ToolCallProbeCall(
                nameElement.GetString() ?? string.Empty,
                TryReadArgumentsJson(item, "arguments")));
        }
    }

    private static string TryReadArgumentsJson(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var argumentsElement))
        {
            return "{}";
        }

        return argumentsElement.ValueKind == JsonValueKind.String
            ? argumentsElement.GetString() ?? "{}"
            : argumentsElement.GetRawText();
    }

    private static Dictionary<string, JsonElement>? ParseArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return document.RootElement
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        }
        catch
        {
            return null;
        }
    }

    private static bool JsonValueEquals(JsonElement actual, object? expected)
        => expected switch
        {
            null => actual.ValueKind == JsonValueKind.Null,
            string expectedString => actual.ValueKind == JsonValueKind.String &&
                                     string.Equals(actual.GetString(), expectedString, StringComparison.OrdinalIgnoreCase),
            int expectedInt => actual.ValueKind == JsonValueKind.Number &&
                               actual.TryGetInt32(out var actualInt) &&
                               actualInt == expectedInt,
            long expectedLong => actual.ValueKind == JsonValueKind.Number &&
                                 actual.TryGetInt64(out var actualLong) &&
                                 actualLong == expectedLong,
            double expectedDouble => actual.ValueKind == JsonValueKind.Number &&
                                     actual.TryGetDouble(out var actualDouble) &&
                                     Math.Abs(actualDouble - expectedDouble) <= 0.000001d,
            decimal expectedDecimal => actual.ValueKind == JsonValueKind.Number &&
                                       actual.TryGetDecimal(out var actualDecimal) &&
                                       Math.Abs(actualDecimal - expectedDecimal) <= 0.000001m,
            bool expectedBool => actual.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                 actual.GetBoolean() == expectedBool,
            _ => string.Equals(actual.GetRawText(), JsonSerializer.Serialize(expected), StringComparison.Ordinal)
        };

    private static string FormatExpectedArguments(IReadOnlyDictionary<string, object?> arguments)
        => string.Join(", ", arguments.Select(pair => $"{pair.Key}={FormatExpectedValue(pair.Value)}"));

    private static string FormatExpectedValue(object? value)
        => value switch
        {
            null => "null",
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

    private static string BuildNormalizedPreview(ToolCallProbeCall call)
        => $"{call.Name}({call.ArgumentsJson})";

    private sealed record ToolCallProbeCall(string Name, string ArgumentsJson);
}
