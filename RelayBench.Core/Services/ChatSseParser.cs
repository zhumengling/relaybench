using System.Text.Json;

namespace RelayBench.Core.Services;

internal static class ChatSseParser
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
        => string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase);

    public static string? TryExtractDelta(string data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        if (TryExtractChatCompletionsDelta(root, out var chatDelta))
        {
            return chatDelta;
        }

        if (TryExtractResponsesDelta(root, out var responsesDelta))
        {
            return responsesDelta;
        }

        if (root.TryGetProperty("error", out var error))
        {
            return TryExtractErrorMessage(error);
        }

        return null;
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
