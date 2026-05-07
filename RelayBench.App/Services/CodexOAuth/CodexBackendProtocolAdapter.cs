using System.Text.Json;
using System.Text.Json.Nodes;

namespace RelayBench.App.Services;

internal static class CodexBackendProtocolAdapter
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string BuildResponsesUrl(TransparentProxyRoute route)
    {
        var baseUrl = string.IsNullOrWhiteSpace(route.CodexBackendBaseUrl)
            ? CodexOAuthConstants.DefaultBackendBaseUrl
            : route.CodexBackendBaseUrl.Trim();
        return $"{baseUrl.TrimEnd('/')}/responses";
    }

    public static byte[] NormalizeResponsesPayload(byte[] requestBody, bool streamRequested)
    {
        if (requestBody.Length == 0)
        {
            return requestBody;
        }

        try
        {
            var node = JsonNode.Parse(requestBody);
            if (node is not JsonObject root)
            {
                return requestBody;
            }

            root["stream"] = streamRequested || ReadBool(root, "stream");
            root.Remove("previous_response_id");
            root.Remove("prompt_cache_retention");
            root.Remove("safety_identifier");
            root.Remove("stream_options");
            return JsonSerializer.SerializeToUtf8Bytes(root, CompactJsonOptions);
        }
        catch
        {
            return requestBody;
        }
    }

    public static byte[] ConvertAnthropicMessagesToResponses(byte[] requestBody, bool streamRequested)
    {
        if (requestBody.Length == 0)
        {
            return requestBody;
        }

        try
        {
            var root = JsonNode.Parse(requestBody) as JsonObject;
            if (root is null)
            {
                return requestBody;
            }

            JsonObject output = new()
            {
                ["model"] = ReadString(root, "model"),
                ["stream"] = streamRequested || ReadBool(root, "stream")
            };

            JsonArray input = [];
            AddAnthropicSystem(root, input);
            if (root["messages"] is JsonArray messages)
            {
                foreach (var messageNode in messages)
                {
                    if (messageNode is JsonObject message)
                    {
                        input.Add(ConvertAnthropicMessage(message));
                    }
                }
            }

            output["input"] = input;
            if (root["max_tokens"] is { } maxTokens)
            {
                output["max_output_tokens"] = maxTokens.DeepClone();
            }

            if (root["temperature"] is { } temperature)
            {
                output["temperature"] = temperature.DeepClone();
            }

            if (ConvertAnthropicTools(root["tools"]) is { Count: > 0 } tools)
            {
                output["tools"] = tools;
            }

            return JsonSerializer.SerializeToUtf8Bytes(output, CompactJsonOptions);
        }
        catch
        {
            return requestBody;
        }
    }

    private static void AddAnthropicSystem(JsonObject root, JsonArray input)
    {
        if (root["system"] is null)
        {
            return;
        }

        var text = ReadContentText(root["system"]);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        input.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = text
                }
            }
        });
    }

    private static JsonObject ConvertAnthropicMessage(JsonObject message)
    {
        var role = ReadString(message, "role");
        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            role = "assistant";
        }
        else if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
        {
            role = "system";
        }
        else
        {
            role = "user";
        }

        return new JsonObject
        {
            ["role"] = role,
            ["content"] = ConvertAnthropicContent(message["content"], role)
        };
    }

    private static JsonArray ConvertAnthropicContent(JsonNode? content, string role)
    {
        JsonArray converted = [];
        if (content is JsonValue)
        {
            var text = ReadContentText(content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                converted.Add(BuildTextPart(text, role));
            }

            return converted;
        }

        if (content is not JsonArray parts)
        {
            return converted;
        }

        foreach (var part in parts)
        {
            if (part is not JsonObject obj)
            {
                continue;
            }

            var type = ReadString(obj, "type");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                var text = ReadString(obj, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    converted.Add(BuildTextPart(text, role));
                }
            }
            else if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                converted.Add(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = ReadContentText(obj["content"])
                });
            }
            else if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                converted.Add(new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = JsonSerializer.Serialize(obj, CompactJsonOptions)
                });
            }
        }

        return converted;
    }

    private static JsonObject BuildTextPart(string text, string role)
        => new()
        {
            ["type"] = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "output_text"
                : "input_text",
            ["text"] = text
        };

    private static JsonArray? ConvertAnthropicTools(JsonNode? toolsNode)
    {
        if (toolsNode is not JsonArray tools)
        {
            return null;
        }

        JsonArray converted = [];
        foreach (var toolNode in tools)
        {
            if (toolNode is not JsonObject tool)
            {
                continue;
            }

            var name = ReadString(tool, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            converted.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = name,
                ["description"] = ReadString(tool, "description"),
                ["parameters"] = tool["input_schema"]?.DeepClone() ?? new JsonObject()
            });
        }

        return converted;
    }

    private static string ReadContentText(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value &&
            value.TryGetValue<string>(out var text))
        {
            return text ?? string.Empty;
        }

        if (node is JsonArray array)
        {
            List<string> parts = [];
            foreach (var item in array)
            {
                if (item is JsonValue itemValue &&
                    itemValue.TryGetValue<string>(out var itemText) &&
                    !string.IsNullOrWhiteSpace(itemText))
                {
                    parts.Add(itemText);
                    continue;
                }

                if (item is JsonObject obj)
                {
                    var objectText = ReadString(obj, "text");
                    if (!string.IsNullOrWhiteSpace(objectText))
                    {
                        parts.Add(objectText);
                    }
                }
            }

            return string.Join("\n", parts);
        }

        return node.ToJsonString(CompactJsonOptions);
    }

    private static string ReadString(JsonObject root, string propertyName)
    {
        if (!root.TryGetPropertyValue(propertyName, out var node) ||
            node is not JsonValue value ||
            !value.TryGetValue<string>(out var text))
        {
            return string.Empty;
        }

        return text ?? string.Empty;
    }

    private static bool ReadBool(JsonObject root, string propertyName)
    {
        if (!root.TryGetPropertyValue(propertyName, out var node) ||
            node is not JsonValue value)
        {
            return false;
        }

        return value.TryGetValue<bool>(out var parsed) && parsed;
    }
}
