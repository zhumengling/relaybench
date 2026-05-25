using System.Text.Json;

namespace RelayBench.Core.Services;

public static class ChatSseParser
{
    public static bool TryReadSseFieldLine(string line, string fieldName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(line) ||
            string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        var span = line.AsSpan().TrimStart();
        var separatorIndex = span.IndexOf(':');
        if (separatorIndex < 0)
        {
            return false;
        }

        var candidate = span[..separatorIndex].Trim();
        if (!candidate.Equals(fieldName.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fieldValue = span[(separatorIndex + 1)..];
        if (!fieldValue.IsEmpty && fieldValue[0] == ' ')
        {
            fieldValue = fieldValue[1..];
        }

        value = fieldValue.ToString();
        return true;
    }

    public static bool TryReadDataLine(string line, out string data)
    {
        data = string.Empty;
        if (!TryReadSseFieldLine(line, "data", out var value))
        {
            return false;
        }

        data = value.Trim();
        return data.Length > 0;
    }

    public static bool IsDone(string data)
    {
        if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var type = typeElement.GetString();
            return string.Equals(type, "message_stop", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "response.completed", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string? TryExtractDelta(string data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (TryExtractChatCompletionsDelta(root, out var chatDelta))
            {
                return chatDelta;
            }

            if (TryExtractResponsesDelta(root, out var responsesDelta))
            {
                return responsesDelta;
            }

            if (TryExtractAnthropicDelta(root, out var anthropicDelta))
            {
                return anthropicDelta;
            }

            if (TryExtractGeminiDelta(root, out var geminiDelta))
            {
                return geminiDelta;
            }

            if (root.TryGetProperty("error", out var error))
            {
                return TryExtractErrorMessage(error);
            }

            return null;
        }
    }

    public static string? TryExtractReasoningDelta(string data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (TryExtractChatCompletionsReasoningDelta(root, out var chatReasoning))
            {
                return chatReasoning;
            }

            if (TryExtractResponsesReasoningDelta(root, out var responsesReasoning))
            {
                return responsesReasoning;
            }

            if (TryExtractAnthropicReasoningDelta(root, out var anthropicReasoning))
            {
                return anthropicReasoning;
            }

            if (TryExtractGeminiReasoningDelta(root, out var geminiReasoning))
            {
                return geminiReasoning;
            }

            return null;
        }
    }

    public static bool TryExtractOutputTokenCount(string data, out int tokenCount)
    {
        tokenCount = 0;
        if (string.IsNullOrWhiteSpace(data) ||
            string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            tokenCount = TryExtractOutputTokenCount(document.RootElement);
            return tokenCount > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryExtractInputTokenCount(string data, out int tokenCount)
    {
        tokenCount = 0;
        if (string.IsNullOrWhiteSpace(data) ||
            string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            tokenCount = TryExtractInputTokenCount(document.RootElement);
            return tokenCount > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryExtractCachedTokenCount(string data, out int tokenCount)
    {
        tokenCount = 0;
        if (string.IsNullOrWhiteSpace(data) ||
            string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            tokenCount = TryExtractCachedTokenCount(document.RootElement);
            return tokenCount > 0;
        }
        catch
        {
            return false;
        }
    }

    public static string? TryExtractError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return TryExtractErrorMessage(error);
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(body) ? null : body.Trim();
    }

    private static int TryExtractOutputTokenCount(JsonElement root)
    {
        var count = Math.Max(
            TryExtractUsageOutputTokens(root, "usage"),
            Math.Max(
                TryExtractUsageOutputTokens(root, "response", "usage"),
                TryExtractUsageOutputTokens(root, "message", "usage")));
        if (count > 0)
        {
            return count;
        }

        if (root.TryGetProperty("usage", out var usage) &&
            usage.ValueKind == JsonValueKind.Object)
        {
            count = Math.Max(
                TryReadIntProperty(usage, "generated_tokens"),
                TryReadIntProperty(usage, "completionTokens"));
            if (count > 0)
            {
                return count;
            }
        }

        if (root.TryGetProperty("usageMetadata", out var usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return TryReadGeminiCompletionTokenCount(usageMetadata);
        }
        if (root.TryGetProperty("usage_metadata", out usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return TryReadGeminiCompletionTokenCount(usageMetadata);
        }

        if (TryResolveGeminiUsageMetadata(root, out usageMetadata))
        {
            return TryReadGeminiCompletionTokenCount(usageMetadata);
        }

        return 0;
    }

    private static int TryExtractInputTokenCount(JsonElement root)
    {
        var count = Math.Max(
            TryExtractUsageInputTokens(root, "usage"),
            Math.Max(
                TryExtractUsageInputTokens(root, "response", "usage"),
                TryExtractUsageInputTokens(root, "message", "usage")));
        if (count > 0)
        {
            return count;
        }

        if (root.TryGetProperty("usageMetadata", out var usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return TryReadIntProperty(usageMetadata, "promptTokenCount");
        }
        if (root.TryGetProperty("usage_metadata", out usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return TryReadIntProperty(usageMetadata, "promptTokenCount");
        }

        if (TryResolveGeminiUsageMetadata(root, out usageMetadata))
        {
            return TryReadIntProperty(usageMetadata, "promptTokenCount");
        }

        return 0;
    }

    private static int TryExtractCachedTokenCount(JsonElement root)
    {
        var count = Math.Max(
            TryExtractUsageCachedTokens(root, "usage"),
            Math.Max(
                TryExtractUsageCachedTokens(root, "response", "usage"),
                TryExtractUsageCachedTokens(root, "message", "usage")));
        if (count > 0)
        {
            return count;
        }

        if (root.TryGetProperty("usageMetadata", out var usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return TryReadIntProperty(usageMetadata, "cachedContentTokenCount");
        }
        if (root.TryGetProperty("usage_metadata", out usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return TryReadIntProperty(usageMetadata, "cachedContentTokenCount");
        }

        if (TryResolveGeminiUsageMetadata(root, out usageMetadata))
        {
            return TryReadIntProperty(usageMetadata, "cachedContentTokenCount");
        }

        return 0;
    }

    private static bool TryExtractGeminiDelta(JsonElement root, out string? delta)
    {
        delta = null;
        if (root.TryGetProperty("response", out var response) &&
            TryExtractGeminiDelta(response, out delta))
        {
            return true;
        }

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) &&
                    thought.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (part.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    delta = text.GetString();
                    return !string.IsNullOrEmpty(delta);
                }
            }
        }

        return false;
    }

    private static bool TryResolveGeminiUsageMetadata(JsonElement root, out JsonElement usageMetadata)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object &&
            TryGetGeminiUsageMetadataProperty(response, out usageMetadata))
        {
            return true;
        }

        usageMetadata = default;
        return false;
    }

    private static bool TryGetGeminiUsageMetadataProperty(JsonElement root, out JsonElement usageMetadata)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("usageMetadata", out usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("usage_metadata", out usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        usageMetadata = default;
        return false;
    }

    private static int TryReadGeminiCompletionTokenCount(JsonElement usageMetadata)
    {
        var promptTokens = TryReadIntProperty(usageMetadata, "promptTokenCount");
        var candidateTokens = TryReadIntProperty(usageMetadata, "candidatesTokenCount");
        var reasoningTokens = TryReadIntProperty(usageMetadata, "thoughtsTokenCount");
        var totalTokens = TryReadIntProperty(usageMetadata, "totalTokenCount");
        var visibleAndReasoningTokens = Math.Min(1_000_000, candidateTokens + reasoningTokens);

        if (totalTokens > 0 && promptTokens > 0)
        {
            return Math.Max(visibleAndReasoningTokens, Math.Max(0, totalTokens - promptTokens));
        }

        return visibleAndReasoningTokens > 0
            ? visibleAndReasoningTokens
            : Math.Max(0, totalTokens - promptTokens);
    }

    private static bool TryExtractGeminiReasoningDelta(JsonElement root, out string? delta)
    {
        delta = null;
        if (root.TryGetProperty("response", out var response) &&
            TryExtractGeminiReasoningDelta(response, out delta))
        {
            return true;
        }

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("thought", out var thought) ||
                    thought.ValueKind != JsonValueKind.True)
                {
                    continue;
                }

                if (part.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    delta = text.GetString();
                    return !string.IsNullOrEmpty(delta);
                }
            }
        }

        return false;
    }

    private static int TryExtractUsageInputTokens(JsonElement root, params string[] path)
    {
        var usage = ResolvePath(root, path);
        if (usage is null)
        {
            return 0;
        }

        return Math.Max(
            Math.Max(
                TryReadIntProperty(usage.Value, "prompt_tokens"),
                TryReadIntProperty(usage.Value, "input_tokens")),
            Math.Max(
                TryReadIntProperty(usage.Value, "promptTokens"),
                TryReadIntProperty(usage.Value, "inputTokens")));
    }

    private static int TryExtractUsageCachedTokens(JsonElement root, params string[] path)
    {
        var usage = ResolvePath(root, path);
        if (usage is null)
        {
            return 0;
        }

        var direct = Math.Max(
            Math.Max(
                TryReadIntProperty(usage.Value, "cache_read_input_tokens"),
                TryReadIntProperty(usage.Value, "cached_tokens")),
            Math.Max(
                TryReadIntProperty(usage.Value, "cachedTokens"),
                TryReadIntProperty(usage.Value, "cachedContentTokenCount")));
        if (direct > 0)
        {
            return direct;
        }

        return Math.Max(
            Math.Max(
                TryReadIntPath(usage.Value, "input_tokens_details", "cached_tokens"),
                TryReadIntPath(usage.Value, "prompt_tokens_details", "cached_tokens")),
            Math.Max(
                TryReadIntPath(usage.Value, "inputTokenDetails", "cachedTokens"),
                TryReadIntPath(usage.Value, "promptTokenDetails", "cachedTokens")));
    }

    private static int TryExtractUsageOutputTokens(JsonElement root, params string[] path)
    {
        var usage = ResolvePath(root, path);
        if (usage is null)
        {
            return 0;
        }

        return Math.Max(
            Math.Max(
                TryReadIntProperty(usage.Value, "completion_tokens"),
                TryReadIntProperty(usage.Value, "output_tokens")),
            Math.Max(
                TryReadIntProperty(usage.Value, "completionTokens"),
                TryReadIntProperty(usage.Value, "generated_tokens")));
    }

    private static JsonElement? ResolvePath(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Object ? current : null;
    }

    private static int TryReadIntPath(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return 0;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Number when current.TryGetInt32(out var number) => Math.Max(0, number),
            JsonValueKind.String when int.TryParse(current.GetString(), out var number) => Math.Max(0, number),
            _ => 0
        };
    }

    private static int TryReadIntProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => Math.Max(0, number),
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => Math.Max(0, number),
            _ => 0
        };
    }

    private static bool TryExtractChatCompletionsDelta(JsonElement root, out string? delta)
    {
        delta = null;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return false;
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var deltaElement))
        {
            return false;
        }

        if (deltaElement.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            delta = content.GetString();
            return !string.IsNullOrEmpty(delta);
        }

        return false;
    }

    private static bool TryExtractChatCompletionsReasoningDelta(JsonElement root, out string? delta)
    {
        delta = null;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return false;
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var deltaElement))
        {
            return false;
        }

        if (deltaElement.TryGetProperty("reasoning_content", out var reasoningContent) &&
            reasoningContent.ValueKind == JsonValueKind.String)
        {
            delta = reasoningContent.GetString();
            return !string.IsNullOrEmpty(delta);
        }

        return false;
    }

    private static bool TryExtractAnthropicDelta(JsonElement root, out string? delta)
    {
        delta = null;
        if (root.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            string.Equals(typeElement.GetString(), "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("delta", out var deltaElement))
        {
            if (deltaElement.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                delta = text.GetString();
                return !string.IsNullOrEmpty(delta);
            }
        }

        if (root.TryGetProperty("content_block", out var contentBlock) &&
            contentBlock.TryGetProperty("text", out var contentBlockText) &&
            contentBlockText.ValueKind == JsonValueKind.String)
        {
            delta = contentBlockText.GetString();
            return !string.IsNullOrEmpty(delta);
        }

        if (root.TryGetProperty("completion", out var completion) &&
            completion.ValueKind == JsonValueKind.String)
        {
            delta = completion.GetString();
            return !string.IsNullOrEmpty(delta);
        }

        return false;
    }

    private static bool TryExtractAnthropicReasoningDelta(JsonElement root, out string? delta)
    {
        delta = null;
        if (root.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            string.Equals(typeElement.GetString(), "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("delta", out var deltaElement))
        {
            if (deltaElement.TryGetProperty("type", out var deltaType) &&
                deltaType.ValueKind == JsonValueKind.String &&
                !string.Equals(deltaType.GetString(), "thinking_delta", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (deltaElement.TryGetProperty("thinking", out var thinking) &&
                thinking.ValueKind == JsonValueKind.String)
            {
                delta = thinking.GetString();
                return !string.IsNullOrEmpty(delta);
            }
        }

        if (root.TryGetProperty("content_block", out var contentBlock) &&
            contentBlock.TryGetProperty("type", out var contentBlockType) &&
            contentBlockType.ValueKind == JsonValueKind.String &&
            string.Equals(contentBlockType.GetString(), "thinking", StringComparison.OrdinalIgnoreCase) &&
            contentBlock.TryGetProperty("thinking", out var contentBlockThinking) &&
            contentBlockThinking.ValueKind == JsonValueKind.String)
        {
            delta = contentBlockThinking.GetString();
            return !string.IsNullOrEmpty(delta);
        }

        return false;
    }

    private static bool TryExtractResponsesDelta(JsonElement root, out string? delta)
    {
        delta = null;
        if (root.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String)
        {
            var type = typeElement.GetString();
            if ((string.Equals(type, "response.output_text.delta", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "response.refusal.delta", StringComparison.OrdinalIgnoreCase)) &&
                root.TryGetProperty("delta", out var eventDelta) &&
                eventDelta.ValueKind == JsonValueKind.String)
            {
                delta = eventDelta.GetString();
                return !string.IsNullOrEmpty(delta);
            }
        }

        if (root.TryGetProperty("delta", out var directDelta) &&
            directDelta.ValueKind == JsonValueKind.String)
        {
            delta = directDelta.GetString();
            return !string.IsNullOrEmpty(delta);
        }

        return false;
    }

    private static bool TryExtractResponsesReasoningDelta(JsonElement root, out string? delta)
    {
        delta = null;
        if (!root.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var type = typeElement.GetString();
        if ((string.Equals(type, "response.reasoning_summary_text.delta", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(type, "response.reasoning_text.delta", StringComparison.OrdinalIgnoreCase)) &&
            root.TryGetProperty("delta", out var eventDelta) &&
            eventDelta.ValueKind == JsonValueKind.String)
        {
            delta = eventDelta.GetString();
            return !string.IsNullOrEmpty(delta);
        }

        return false;
    }

    private static string? TryExtractErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        return error.GetRawText();
    }
}
