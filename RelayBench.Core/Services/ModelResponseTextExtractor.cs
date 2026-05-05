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

        if (TryExtractResponseLikeText(root, out var responseText))
        {
            return responseText;
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

    private static bool TryExtractResponseLikeText(JsonElement root, out string? text)
    {
        text = null;
        StringBuilder builder = new();

        if (root.TryGetProperty("output_text", out var outputText))
        {
            AppendContentText(outputText, builder);
        }

        if (root.TryGetProperty("output", out var output))
        {
            AppendContentText(output, builder);
        }

        if (root.TryGetProperty("message", out var message))
        {
            AppendContentText(message, builder);
        }

        if (builder.Length <= 0)
        {
            return false;
        }

        text = builder.ToString();
        return true;
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
            StringBuilder objectBuilder = new();
            AppendContentText(element, objectBuilder);
            if (objectBuilder.Length <= 0)
            {
                return false;
            }

            text = objectBuilder.ToString();
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        StringBuilder builder = new();
        foreach (var item in element.EnumerateArray())
        {
            AppendContentText(item, builder);
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
        foreach (var propertyName in new[] { "output_text", "text", "content", "completion", "delta", "value" })
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                text = property.GetString();
                return !string.IsNullOrEmpty(text);
            }

            if ((property.ValueKind is JsonValueKind.Object or JsonValueKind.Array) &&
                TryExtractContentPartsText(property, out text))
            {
                return !string.IsNullOrEmpty(text);
            }
        }

        text = null;
        return false;
    }

    private static void AppendContentText(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.Append(element.GetString());
                return;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendContentText(item, builder);
                }

                return;
            case JsonValueKind.Object:
                break;
            default:
                return;
        }

        if (TryExtractDirectTextWithoutNestedContent(element, out var directText))
        {
            builder.Append(directText);
        }

        foreach (var propertyName in new[] { "content", "output", "message", "item", "delta", "text", "output_text", "summary" })
        {
            if (element.TryGetProperty(propertyName, out var nested) &&
                nested.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                AppendContentText(nested, builder);
            }
        }
    }

    private static bool TryExtractDirectTextWithoutNestedContent(JsonElement element, out string? text)
    {
        foreach (var propertyName in new[] { "output_text", "text", "content", "completion", "delta", "value" })
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
