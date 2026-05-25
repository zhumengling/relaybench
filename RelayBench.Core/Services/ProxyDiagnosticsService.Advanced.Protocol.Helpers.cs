using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static bool MatchesSystemPromptExpectation(string? preview)
    {
        var normalized = NormalizeProbeText(preview);
        return normalized.Contains("systemmappingok", StringComparison.Ordinal) &&
               !normalized.Contains("useroverridefail", StringComparison.Ordinal);
    }

    private static bool MatchesFunctionCallingArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            return root.TryGetProperty("status", out var statusElement) &&
                   statusElement.ValueKind == JsonValueKind.String &&
                   string.Equals(statusElement.GetString(), "proxy-ok", StringComparison.OrdinalIgnoreCase) &&
                   root.TryGetProperty("channel", out var channelElement) &&
                   channelElement.ValueKind == JsonValueKind.String &&
                   string.Equals(channelElement.GetString(), "function-calling", StringComparison.OrdinalIgnoreCase) &&
                   root.TryGetProperty("round", out var roundElement) &&
                   roundElement.ValueKind == JsonValueKind.Number &&
                   roundElement.TryGetInt32(out var round) &&
                   round == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesFunctionCallingFinalAnswer(string? preview)
        => NormalizeProbeText(preview).Contains("functioncallfinishok", StringComparison.Ordinal);

    private static bool MatchesMultiModalExpectation(string? preview)
        => NormalizeProbeText(preview).Contains("multimodalok", StringComparison.Ordinal);

    private static bool MatchesCacheProbeExpectation(string? preview)
        => NormalizeProbeText(preview).Contains("cacheprobeok", StringComparison.Ordinal);

    private static bool MatchesCacheIsolationExpectation(string? preview, string expectedOutput)
        => string.Equals(
            NormalizeProbeText(preview),
            NormalizeProbeText(expectedOutput),
            StringComparison.Ordinal);

    private static string PreviewLabelForSupplementalScenario(string? preview)
        => string.IsNullOrWhiteSpace(preview)
            ? "（无）"
            : preview.Trim();

    private static string NormalizeIntegrityOutput(string? value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();

    private static string BuildStreamingIntegrityDigest(string nonStreamText, string streamText, bool outputsEqual)
    {
        var digestBuilder = new StringBuilder();
        digestBuilder.Append($"非流式 {CountIntegrityLines(nonStreamText)} 行 / {nonStreamText.Length} 字符；");
        digestBuilder.Append($"流式 {CountIntegrityLines(streamText)} 行 / {streamText.Length} 字符；");
        digestBuilder.Append(outputsEqual
            ? "输出一致"
            : $"首处差异：{DescribeFirstDifference(nonStreamText, streamText)}");
        return digestBuilder.ToString();
    }

    private static string BuildOfficialReferenceIntegrityDigest(
        string relayText,
        string officialText,
        bool relayMatchesTemplate,
        bool officialMatchesTemplate,
        bool outputsEqual)
    {
        var digestBuilder = new StringBuilder();
        digestBuilder.Append($"待测接口 {CountIntegrityLines(relayText)} 行 / {relayText.Length} 字符 / {(relayMatchesTemplate ? "命中模板" : "未命中模板")}；");
        digestBuilder.Append($"官方 {CountIntegrityLines(officialText)} 行 / {officialText.Length} 字符 / {(officialMatchesTemplate ? "命中模板" : "未命中模板")}；");
        digestBuilder.Append(outputsEqual
            ? "待测接口与官方输出一致"
            : $"首处差异：{DescribeFirstDifference(relayText, officialText, "待测接口", "官方")}");
        return digestBuilder.ToString();
    }

    private static string BuildCacheIsolationDigest(
        string? primaryPreview,
        string? alternatePreview,
        string secretA,
        bool leakedPrimarySecret,
        bool previewsEqual)
    {
        var primary = string.IsNullOrWhiteSpace(primaryPreview) ? "（无）" : primaryPreview.Trim();
        var alternate = string.IsNullOrWhiteSpace(alternatePreview) ? "（无）" : alternatePreview.Trim();
        return $"A={primary}；B={alternate}；A-secret={(leakedPrimarySecret ? "出现在 B 返回中" : "未出现在 B 返回中")}；A/B {(previewsEqual ? "完全一致" : "不同")}";
    }

    private static int CountIntegrityLines(string value)
        => string.IsNullOrWhiteSpace(value)
            ? 0
            : value.Split('\n').Length;

    private static string DescribeFirstDifference(string left, string right)
        => DescribeFirstDifference(left, right, "非流式", "流式");

    private static string DescribeFirstDifference(string left, string right, string leftLabel, string rightLabel)
    {
        var sharedLength = Math.Min(left.Length, right.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            if (left[index] == right[index])
            {
                continue;
            }

            return $"位置 {index + 1}，{leftLabel} {EscapeDifferenceChar(left[index])}，{rightLabel} {EscapeDifferenceChar(right[index])}";
        }

        if (left.Length != right.Length)
        {
            return $"长度不同（{leftLabel} {left.Length}，{rightLabel} {right.Length}）";
        }

        return "未定位到明显差异";
    }

    private static string EscapeDifferenceChar(char value)
        => value switch
        {
            '\n' => "LF",
            '\r' => "CR",
            '\t' => "TAB",
            _ => $"“{value}”"
        };

    private static bool IsLikelyCacheHit(double? firstTtftMs, double? secondTtftMs, bool outputsEqual)
    {
        if (!outputsEqual || !firstTtftMs.HasValue || !secondTtftMs.HasValue)
        {
            return false;
        }

        return firstTtftMs.Value >= 500 &&
               secondTtftMs.Value <= 400 &&
               secondTtftMs.Value <= firstTtftMs.Value * 0.55 &&
               secondTtftMs.Value + 120 < firstTtftMs.Value;
    }

    private static string NormalizeProbeText(string? value)
        => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string FormatMillisecondsValue(TimeSpan? value)
        => value?.TotalMilliseconds.ToString("F0") + " ms" ?? "--";

    private static bool TryParseToolCallResponse(string json, out (string Id, string Name, string ArgumentsJson) toolCall)
    {
        toolCall = default;

        using var document = JsonDocument.Parse(json);
        if (TryParseOpenAiToolCallResponse(document.RootElement, out toolCall))
        {
            return true;
        }

        if (TryParseAnthropicToolCallResponse(document.RootElement, out toolCall))
        {
            return true;
        }

        return TryParseResponsesToolCallResponse(document.RootElement, out toolCall);
    }

    private static bool TryParseOpenAiToolCallResponse(JsonElement root, out (string Id, string Name, string ArgumentsJson) toolCall)
    {
        toolCall = default;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("tool_calls", out var toolCalls) ||
                toolCalls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var toolCallElement in toolCalls.EnumerateArray())
            {
                if (!toolCallElement.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !toolCallElement.TryGetProperty("function", out var functionElement) ||
                    functionElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!functionElement.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                toolCall = (
                    idElement.GetString() ?? "tool-call-1",
                    nameElement.GetString() ?? string.Empty,
                    TryExtractToolCallArgumentsJson(functionElement));
                return !string.IsNullOrWhiteSpace(toolCall.Name);
            }
        }

        return false;
    }

    private static bool TryParseAnthropicToolCallResponse(JsonElement root, out (string Id, string Name, string ArgumentsJson) toolCall)
    {
        toolCall = default;
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? "tool-call-1"
                : "tool-call-1";
            var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var argumentsJson = item.TryGetProperty("input", out var inputElement)
                ? inputElement.ValueKind == JsonValueKind.String
                    ? inputElement.GetString() ?? "{}"
                    : inputElement.GetRawText()
                : "{}";

            if (!string.IsNullOrWhiteSpace(name))
            {
                toolCall = (id, name, argumentsJson);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseResponsesToolCallResponse(JsonElement root, out (string Id, string Name, string ArgumentsJson) toolCall)
    {
        toolCall = default;
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = item.TryGetProperty("call_id", out var callIdElement) && callIdElement.ValueKind == JsonValueKind.String
                ? callIdElement.GetString() ?? "tool-call-1"
                : item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString() ?? "tool-call-1"
                    : "tool-call-1";
            var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var argumentsJson = item.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.ValueKind == JsonValueKind.String
                    ? argumentsElement.GetString() ?? "{}"
                    : argumentsElement.GetRawText()
                : "{}";

            if (!string.IsNullOrWhiteSpace(name))
            {
                toolCall = (id, name, argumentsJson);
                return true;
            }
        }

        return false;
    }

    private static string TryExtractToolCallArgumentsJson(JsonElement functionElement)
    {
        if (!functionElement.TryGetProperty("arguments", out var argumentsElement))
        {
            return "{}";
        }

        return argumentsElement.ValueKind == JsonValueKind.String
            ? argumentsElement.GetString() ?? "{}"
            : argumentsElement.GetRawText();
    }

    private static string? ExtractErrorPreview(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String)
                {
                    return errorElement.GetString();
                }

                if (errorElement.ValueKind == JsonValueKind.Object)
                {
                    if (errorElement.TryGetProperty("message", out var messageElement) &&
                        messageElement.ValueKind == JsonValueKind.String)
                    {
                        return messageElement.GetString();
                    }

                    if (errorElement.TryGetProperty("type", out var typeElement) &&
                        typeElement.ValueKind == JsonValueKind.String)
                    {
                        return typeElement.GetString();
                    }
                }
            }

            if (root.TryGetProperty("message", out var rootMessage) &&
                rootMessage.ValueKind == JsonValueKind.String)
            {
                return rootMessage.GetString();
            }
        }
        catch
        {
        }

        return ExtractBodySample(body);
    }

    private static bool LooksLikeTransparentBadRequest(string? body)
    {
        var normalized = NormalizeProbeText(ExtractErrorPreview(body));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.Contains("internalservererror", StringComparison.Ordinal) ||
            normalized.Contains("unexpectederror", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("invalid", StringComparison.Ordinal) ||
               normalized.Contains("required", StringComparison.Ordinal) ||
               normalized.Contains("missing", StringComparison.Ordinal) ||
               normalized.Contains("mustprovide", StringComparison.Ordinal) ||
               normalized.Contains("mustbeprovided", StringComparison.Ordinal) ||
               normalized.Contains("oneof", StringComparison.Ordinal) ||
               normalized.Contains("messages", StringComparison.Ordinal) ||
               normalized.Contains("input", StringComparison.Ordinal) ||
               normalized.Contains("previousresponseid", StringComparison.Ordinal) ||
               normalized.Contains("parameter", StringComparison.Ordinal) ||
               normalized.Contains("schema", StringComparison.Ordinal) ||
               normalized.Contains("json", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string>? MergeHeaders(params IReadOnlyList<string>?[] headerGroups)
    {
        var merged = headerGroups
            .Where(group => group is { Count: > 0 })
            .SelectMany(group => group!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return merged.Length == 0 ? null : merged;
    }
}
