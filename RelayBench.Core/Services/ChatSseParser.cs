using System.Text.Json;

namespace RelayBench.Core.Services;

public static class ChatSseParser
{
    public static bool TryReadDataLine(string line, out string data)
    {
        data = string.Empty;
        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        data = line[5..].Trim();
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

            if (root.TryGetProperty("error", out var error))
            {
                return TryExtractErrorMessage(error);
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
            return TryReadIntProperty(usageMetadata, "candidatesTokenCount");
        }

        return 0;
    }

    private static int TryExtractUsageOutputTokens(JsonElement root, params string[] path)
    {
        var usage = root;
        foreach (var segment in path)
        {
            if (usage.ValueKind != JsonValueKind.Object ||
                !usage.TryGetProperty(segment, out usage))
            {
                return 0;
            }
        }

        if (usage.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return Math.Max(
            Math.Max(
                TryReadIntProperty(usage, "completion_tokens"),
                TryReadIntProperty(usage, "output_tokens")),
            Math.Max(
                TryReadIntProperty(usage, "completionTokens"),
                TryReadIntProperty(usage, "generated_tokens")));
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
