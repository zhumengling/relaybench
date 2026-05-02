using System.Text;
using System.Text.Json;

namespace RelayBench.Core.Services;

public static class ModelResponseTextExtractor
{
    public static string? TryExtractAssistantText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryExtractAssistantText(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? TryExtractAssistantText(JsonElement root)
    {
        if (TryExtractChatCompletionsText(root, out var chatText))
        {
            return chatText;
        }

        if (TryExtractDirectText(root, out var directText))
        {
            return directText;
        }

        if (root.TryGetProperty("content", out var content) &&
            TryExtractContentPartsText(content, out var contentText))
        {
            return contentText;
        }

        if (root.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            StringBuilder builder = new();
            foreach (var outputItem in output.EnumerateArray())
            {
                if (TryExtractDirectText(outputItem, out var outputText))
                {
                    builder.Append(outputText);
                    continue;
                }

                if (outputItem.TryGetProperty("content", out var outputContent) &&
                    TryExtractContentPartsText(outputContent, out var outputContentText))
                {
                    builder.Append(outputContentText);
                }
            }

            if (builder.Length > 0)
            {
                return builder.ToString();
            }
        }

        if (root.TryGetProperty("delta", out var delta) &&
            delta.ValueKind == JsonValueKind.String)
        {
            return delta.GetString();
        }

        return null;
    }

    private static bool TryExtractChatCompletionsText(JsonElement root, out string? text)
    {
        text = null;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        StringBuilder builder = new();
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var messageContent) &&
                    TryExtractContentPartsText(messageContent, out var messageText))
                {
                    builder.Append(messageText);
                }
            }

            if (choice.TryGetProperty("delta", out var delta) &&
                TryExtractContentPartsText(delta, out var deltaText))
            {
                builder.Append(deltaText);
            }
        }

        if (builder.Length <= 0)
        {
            return false;
        }

        text = builder.ToString();
        return true;
    }

    private static bool TryExtractContentPartsText(JsonElement element, out string? text)
    {
        text = null;
        if (element.ValueKind == JsonValueKind.String)
        {
            text = element.GetString();
            return !string.IsNullOrEmpty(text);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            return TryExtractDirectText(element, out text);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        StringBuilder builder = new();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                builder.Append(item.GetString());
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                TryExtractDirectText(item, out var itemText))
            {
                builder.Append(itemText);
            }
        }

        if (builder.Length <= 0)
        {
            return false;
        }

        text = builder.ToString();
        return true;
    }

    private static bool TryExtractDirectText(JsonElement element, out string? text)
    {
        foreach (var propertyName in new[] { "output_text", "text", "content", "completion" })
        {
            if (element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                text = property.GetString();
                return !string.IsNullOrEmpty(text);
            }
        }

        text = null;
        return false;
    }
}
