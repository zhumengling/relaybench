using System.Text.Json.Nodes;
using RelayBench.Core.Services;

namespace RelayBench.Core.AdvancedTesting;

public sealed record AdvancedPreparedWireRequest(
    string RelativePath,
    string RequestBody,
    string WireApi,
    IReadOnlyDictionary<string, string> ExtraHeaders);

public static class AdvancedWireRequestBuilder
{
    private const int AnthropicMinMaxTokens = 512;

    public static AdvancedPreparedWireRequest PreparePostJson(
        string relativePath,
        string requestBody,
        string? preferredWireApi,
        bool stream)
    {
        var normalizedPath = relativePath.Trim().TrimStart('/');
        if (!IsChatCompletionsPath(normalizedPath))
        {
            return new AdvancedPreparedWireRequest(
                normalizedPath,
                requestBody,
                InferWireApiFromPath(normalizedPath),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var wireApi = ProxyWireApiProbeService.NormalizeWireApi(preferredWireApi) ??
                      ProxyWireApiProbeService.ChatCompletionsWireApi;

        return wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => new AdvancedPreparedWireRequest(
                "responses",
                ConvertChatPayloadToResponsesPayload(requestBody, stream),
                ProxyWireApiProbeService.ResponsesWireApi,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            ProxyWireApiProbeService.AnthropicMessagesWireApi => new AdvancedPreparedWireRequest(
                "messages",
                ConvertChatPayloadToAnthropicMessagesPayload(requestBody, stream),
                ProxyWireApiProbeService.AnthropicMessagesWireApi,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["anthropic-version"] = "2023-06-01"
                }),
            _ => new AdvancedPreparedWireRequest(
                normalizedPath,
                requestBody,
                ProxyWireApiProbeService.ChatCompletionsWireApi,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        };
    }

    private static bool IsChatCompletionsPath(string relativePath)
        => relativePath.Equals("chat/completions", StringComparison.OrdinalIgnoreCase) ||
           relativePath.Equals("v1/chat/completions", StringComparison.OrdinalIgnoreCase);

    private static string InferWireApiFromPath(string relativePath)
    {
        if (relativePath.EndsWith("responses", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyWireApiProbeService.ResponsesWireApi;
        }

        if (relativePath.EndsWith("messages", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyWireApiProbeService.AnthropicMessagesWireApi;
        }

        return ProxyWireApiProbeService.ChatCompletionsWireApi;
    }

    private static string ConvertChatPayloadToResponsesPayload(string chatPayload, bool stream)
    {
        var root = ParseChatPayload(chatPayload);
        var output = new JsonObject
        {
            ["model"] = CloneNode(root["model"]),
            ["max_output_tokens"] = CloneNode(root["max_output_tokens"]) ??
                                    CloneNode(root["max_tokens"]) ??
                                    512
        };

        if (CloneNode(root["temperature"]) is { } temperature)
        {
            output["temperature"] = temperature;
        }

        output["stream"] = TryReadBool(root, "stream", out var payloadStream)
            ? payloadStream
            : stream;

        var messages = root["messages"] as JsonArray;
        var systemPrompt = BuildSystemPromptFromChatMessages(messages);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            output["instructions"] = systemPrompt;
        }

        output["input"] = BuildResponsesInput(messages);

        if (ConvertOpenAiToolsToResponsesTools(root["tools"]) is { Count: > 0 } tools)
        {
            output["tools"] = tools;
        }

        if (ConvertOpenAiToolChoiceToResponses(root["tool_choice"]) is { } toolChoice)
        {
            output["tool_choice"] = toolChoice;
        }

        return output.ToJsonString();
    }

    private static string ConvertChatPayloadToAnthropicMessagesPayload(string chatPayload, bool stream)
    {
        var root = ParseChatPayload(chatPayload);
        var output = new JsonObject
        {
            ["model"] = CloneNode(root["model"]),
            ["max_tokens"] = ResolveAnthropicMaxTokens(root)
        };

        if (CloneNode(root["temperature"]) is { } temperature)
        {
            output["temperature"] = temperature;
        }

        output["thinking"] = CloneNode(root["thinking"]) ?? new JsonObject
        {
            ["type"] = "disabled"
        };

        output["stream"] = TryReadBool(root, "stream", out var payloadStream)
            ? payloadStream
            : stream;

        var messages = root["messages"] as JsonArray;
        var systemPrompt = BuildSystemPromptFromChatMessages(messages);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            output["system"] = systemPrompt;
        }

        output["messages"] = BuildAnthropicMessages(messages);

        if (ConvertOpenAiToolsToAnthropicTools(root["tools"]) is { Count: > 0 } tools)
        {
            output["tools"] = tools;
        }

        if (ConvertOpenAiToolChoiceToAnthropic(root["tool_choice"]) is { } toolChoice)
        {
            output["tool_choice"] = toolChoice;
        }

        return output.ToJsonString();
    }

    private static JsonObject ParseChatPayload(string chatPayload)
        => JsonNode.Parse(chatPayload)?.AsObject() ??
           throw new InvalidOperationException("Chat payload JSON is not an object.");

    private static int ResolveAnthropicMaxTokens(JsonObject root)
    {
        var requested =
            TryReadPositiveJsonInt(root["max_tokens"]) ??
            TryReadPositiveJsonInt(root["max_output_tokens"]) ??
            512;

        return Math.Max(requested, AnthropicMinMaxTokens);
    }

    private static JsonArray BuildResponsesInput(JsonArray? messages)
    {
        JsonArray input = new();
        if (messages is null)
        {
            return input;
        }

        foreach (var message in messages.OfType<JsonObject>())
        {
            var role = TryReadString(message, "role");
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedRole = NormalizeResponsesRole(role);
            if (string.Equals(normalizedRole, "tool", StringComparison.OrdinalIgnoreCase))
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = TryReadString(message, "tool_call_id") ?? "call_1",
                    ["output"] = PlainTextFromChatContent(message["content"])
                });
                continue;
            }

            input.Add(new JsonObject
            {
                ["role"] = normalizedRole,
                ["content"] = ConvertChatContentToResponsesContent(message["content"], normalizedRole)
            });
        }

        return input;
    }

    private static JsonArray BuildAnthropicMessages(JsonArray? messages)
    {
        JsonArray output = new();
        if (messages is null)
        {
            return output;
        }

        foreach (var message in messages.OfType<JsonObject>())
        {
            var role = TryReadString(message, "role")?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(role) || role == "system")
            {
                continue;
            }

            if (role == "tool")
            {
                output.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = TryReadString(message, "tool_call_id") ?? "call_1",
                            ["content"] = PlainTextFromChatContent(message["content"])
                        }
                    }
                });
                continue;
            }

            var content = ConvertChatContentToAnthropicContent(message);
            if (content is null)
            {
                continue;
            }

            output.Add(new JsonObject
            {
                ["role"] = role == "assistant" ? "assistant" : "user",
                ["content"] = content
            });
        }

        return output;
    }

    private static JsonNode? ConvertChatContentToAnthropicContent(JsonObject message)
    {
        if (message["tool_calls"] is JsonArray toolCalls && toolCalls.Count > 0)
        {
            JsonArray toolUseContent = new();
            foreach (var toolCall in toolCalls.OfType<JsonObject>())
            {
                if (toolCall["function"] is not JsonObject function)
                {
                    continue;
                }

                toolUseContent.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = TryReadString(toolCall, "id") ?? $"call_{toolUseContent.Count + 1}",
                    ["name"] = TryReadString(function, "name") ?? string.Empty,
                    ["input"] = ParseJsonObjectOrEmpty(TryReadString(function, "arguments") ?? function["arguments"]?.ToJsonString())
                });
            }

            return toolUseContent.Count > 0 ? toolUseContent : null;
        }

        return ConvertChatContentToAnthropicContent(message["content"]);
    }

    private static JsonNode? ConvertChatContentToAnthropicContent(JsonNode? content)
    {
        if (content is null)
        {
            return null;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (content is not JsonArray array)
        {
            return PlainTextFromChatContent(content);
        }

        JsonArray converted = new();
        foreach (var item in array.OfType<JsonObject>())
        {
            var type = TryReadString(item, "type");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                converted.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = TryReadString(item, "text") ?? string.Empty
                });
                continue;
            }

            if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase) &&
                item["image_url"] is JsonObject imageUrl &&
                TryReadString(imageUrl, "url") is { } url &&
                TryParseDataUri(url, out var mediaType, out var data))
            {
                converted.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = mediaType,
                        ["data"] = data
                    }
                });
            }
        }

        return converted.Count > 0 ? converted : null;
    }

    private static JsonArray ConvertChatContentToResponsesContent(JsonNode? content, string role)
    {
        JsonArray converted = new();
        if (content is null)
        {
            return converted;
        }

        var textType = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? "output_text"
            : "input_text";

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            converted.Add(new JsonObject { ["type"] = textType, ["text"] = text });
            return converted;
        }

        if (content is not JsonArray array)
        {
            converted.Add(new JsonObject { ["type"] = textType, ["text"] = PlainTextFromChatContent(content) });
            return converted;
        }

        foreach (var item in array.OfType<JsonObject>())
        {
            var type = TryReadString(item, "type");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                converted.Add(new JsonObject
                {
                    ["type"] = textType,
                    ["text"] = TryReadString(item, "text") ?? string.Empty
                });
                continue;
            }

            if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase) &&
                item["image_url"] is JsonObject imageUrl &&
                TryReadString(imageUrl, "url") is { } url)
            {
                converted.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = url
                });
            }
        }

        return converted;
    }

    private static JsonArray? ConvertOpenAiToolsToAnthropicTools(JsonNode? toolsNode)
    {
        if (toolsNode is not JsonArray tools)
        {
            return null;
        }

        JsonArray converted = new();
        foreach (var tool in tools.OfType<JsonObject>())
        {
            if (tool["function"] is not JsonObject function)
            {
                continue;
            }

            converted.Add(new JsonObject
            {
                ["name"] = TryReadString(function, "name") ?? string.Empty,
                ["description"] = TryReadString(function, "description") ?? string.Empty,
                ["input_schema"] = CloneNode(function["parameters"]) ?? new JsonObject { ["type"] = "object" }
            });
        }

        return converted;
    }

    private static JsonArray? ConvertOpenAiToolsToResponsesTools(JsonNode? toolsNode)
    {
        if (toolsNode is not JsonArray tools)
        {
            return null;
        }

        JsonArray converted = new();
        foreach (var tool in tools.OfType<JsonObject>())
        {
            if (tool["function"] is not JsonObject function)
            {
                continue;
            }

            converted.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = TryReadString(function, "name") ?? string.Empty,
                ["description"] = TryReadString(function, "description") ?? string.Empty,
                ["parameters"] = CloneNode(function["parameters"]) ?? new JsonObject { ["type"] = "object" }
            });
        }

        return converted;
    }

    private static JsonNode? ConvertOpenAiToolChoiceToAnthropic(JsonNode? toolChoice)
    {
        if (toolChoice is null)
        {
            return null;
        }

        if (toolChoice is JsonValue value && value.TryGetValue<string>(out var choiceText))
        {
            return string.Equals(choiceText, "auto", StringComparison.OrdinalIgnoreCase)
                ? new JsonObject { ["type"] = "auto" }
                : null;
        }

        if (toolChoice is JsonObject choice &&
            choice["function"] is JsonObject function &&
            TryReadString(function, "name") is { } name)
        {
            return new JsonObject { ["type"] = "tool", ["name"] = name };
        }

        return null;
    }

    private static JsonNode? ConvertOpenAiToolChoiceToResponses(JsonNode? toolChoice)
    {
        if (toolChoice is null)
        {
            return null;
        }

        if (toolChoice is JsonValue value && value.TryGetValue<string>(out var choiceText))
        {
            return choiceText;
        }

        if (toolChoice is JsonObject choice &&
            choice["function"] is JsonObject function &&
            TryReadString(function, "name") is { } name)
        {
            return new JsonObject { ["type"] = "function", ["name"] = name };
        }

        return null;
    }

    private static string BuildSystemPromptFromChatMessages(JsonArray? messages)
    {
        if (messages is null)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            messages
                .OfType<JsonObject>()
                .Where(static message => string.Equals(TryReadString(message, "role"), "system", StringComparison.OrdinalIgnoreCase))
                .Select(static message => PlainTextFromChatContent(message["content"]))
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string PlainTextFromChatContent(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (content is JsonArray array)
        {
            return string.Join(
                "\n",
                array.OfType<JsonObject>()
                    .Select(static item => TryReadString(item, "text"))
                    .Where(static text => !string.IsNullOrWhiteSpace(text)));
        }

        return content.ToJsonString();
    }

    private static JsonObject ParseJsonObjectOrEmpty(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject parsed)
                {
                    return parsed;
                }
            }
            catch
            {
            }
        }

        return new JsonObject();
    }

    private static string NormalizeResponsesRole(string? role)
        => string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? "assistant"
            : string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)
                ? "system"
                : string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)
                    ? "tool"
                    : "user";

    private static string? TryReadString(JsonObject obj, string propertyName)
        => obj.TryGetPropertyValue(propertyName, out var node) &&
           node is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static bool TryReadBool(JsonObject obj, string propertyName, out bool value)
    {
        value = false;
        return obj.TryGetPropertyValue(propertyName, out var node) &&
               node is JsonValue jsonValue &&
               jsonValue.TryGetValue(out value);
    }

    private static int? TryReadPositiveJsonInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var intValue) && intValue > 0)
        {
            return intValue;
        }

        if (value.TryGetValue<long>(out var longValue) &&
            longValue > 0 &&
            longValue <= int.MaxValue)
        {
            return (int)longValue;
        }

        if (value.TryGetValue<string>(out var text) &&
            int.TryParse(
                text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return null;
    }

    private static JsonNode? CloneNode(JsonNode? node)
        => node?.DeepClone();

    private static bool TryParseDataUri(string url, out string mediaType, out string data)
    {
        mediaType = "application/octet-stream";
        data = string.Empty;
        const string prefix = "data:";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = url.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
        {
            return false;
        }

        var metadata = url[prefix.Length..commaIndex];
        var separatorIndex = metadata.IndexOf(';', StringComparison.Ordinal);
        mediaType = separatorIndex >= 0 ? metadata[..separatorIndex] : metadata;
        data = url[(commaIndex + 1)..];
        return !string.IsNullOrWhiteSpace(mediaType) && !string.IsNullOrWhiteSpace(data);
    }
}
