using System.Text.Json.Nodes;
using RelayBench.Core.Services;

namespace RelayBench.Core.AdvancedTesting;

public sealed record AdvancedPreparedWireRequest(
    string RelativePath,
    string RequestBody,
    string WireApi,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    IReadOnlyDictionary<string, string>? ToolNameAliases = null);

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
            ProxyWireApiProbeService.ResponsesWireApi => BuildResponsesPreparedRequest(requestBody, stream),
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

    private static AdvancedPreparedWireRequest BuildResponsesPreparedRequest(string requestBody, bool stream)
    {
        var body = ConvertChatPayloadToResponsesPayload(
            requestBody,
            stream,
            out var toolNameAliases);
        return new AdvancedPreparedWireRequest(
            "responses",
            body,
            ProxyWireApiProbeService.ResponsesWireApi,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            toolNameAliases);
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

    private static string ConvertChatPayloadToResponsesPayload(
        string chatPayload,
        bool stream,
        out IReadOnlyDictionary<string, string> toolNameAliases)
    {
        var root = ParseChatPayload(chatPayload);
        var toolNameMap = BuildOpenAiToolNameMap(root["tools"]);
        toolNameAliases = BuildToolNameAliases(toolNameMap);
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

        if (TryReadString(root, "reasoning_effort") is { } reasoningEffort &&
            !string.IsNullOrWhiteSpace(reasoningEffort))
        {
            output["reasoning"] = new JsonObject
            {
                ["effort"] = reasoningEffort.Trim().ToLowerInvariant()
            };
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

        output["input"] = BuildResponsesInput(messages, toolNameMap);

        if (ConvertOpenAiToolsToResponsesTools(root["tools"], toolNameMap) is { Count: > 0 } tools)
        {
            output["tools"] = tools;
        }

        if (ConvertOpenAiToolChoiceToResponses(root["tool_choice"], toolNameMap) is { } toolChoice)
        {
            output["tool_choice"] = toolChoice;
        }

        ApplyResponsesTextSettings(root, output);

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

    private static JsonArray BuildResponsesInput(
        JsonArray? messages,
        IReadOnlyDictionary<string, string> toolNameMap)
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
                    ["output"] = BuildResponsesToolOutput(message["content"])
                });
                continue;
            }

            var content = ConvertChatContentToResponsesContent(message["content"], normalizedRole);
            if (!string.Equals(normalizedRole, "assistant", StringComparison.OrdinalIgnoreCase) ||
                content.Count > 0)
            {
                input.Add(new JsonObject
                {
                    ["role"] = normalizedRole,
                    ["content"] = content
                });
            }

            if (string.Equals(normalizedRole, "assistant", StringComparison.OrdinalIgnoreCase) &&
                message["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var toolCall in toolCalls.OfType<JsonObject>())
                {
                    if (toolCall["function"] is not JsonObject function)
                    {
                        continue;
                    }

                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = TryReadString(toolCall, "id") ?? $"call_{input.Count + 1}",
                        ["name"] = ResolveOpenAiToolName(TryReadString(function, "name") ?? string.Empty, toolNameMap),
                        ["arguments"] = TryReadString(function, "arguments") ?? function["arguments"]?.ToJsonString() ?? "{}"
                    });
                }
            }
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
                continue;
            }

            if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase) &&
                item["file"] is JsonObject file &&
                BuildResponsesFilePart(file) is { } filePart)
            {
                converted.Add(filePart);
            }
        }

        return converted;
    }

    private static JsonNode BuildResponsesToolOutput(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text ?? string.Empty;
        }

        if (content is not JsonArray array)
        {
            return content.ToJsonString();
        }

        JsonArray output = [];
        foreach (var item in array)
        {
            output.Add(ConvertChatToolOutputPart(item));
        }

        return output;
    }

    private static JsonObject ConvertChatToolOutputPart(JsonNode? item)
    {
        if (item is not JsonObject obj)
        {
            return BuildTextToolOutputPart(item?.ToJsonString() ?? string.Empty);
        }

        var type = TryReadString(obj, "type");
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
        {
            return BuildTextToolOutputPart(TryReadString(obj, "text") ?? string.Empty);
        }

        if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase) &&
            obj["image_url"] is JsonObject imageUrl)
        {
            var url = TryReadString(imageUrl, "url");
            var fileId = TryReadString(imageUrl, "file_id");
            if (!string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(fileId))
            {
                JsonObject part = new()
                {
                    ["type"] = "input_image"
                };
                if (!string.IsNullOrWhiteSpace(url))
                {
                    part["image_url"] = url;
                }

                if (!string.IsNullOrWhiteSpace(fileId))
                {
                    part["file_id"] = fileId;
                }

                if (TryReadString(imageUrl, "detail") is { Length: > 0 } detail)
                {
                    part["detail"] = detail;
                }

                return part;
            }
        }

        if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase) &&
            obj["file"] is JsonObject file)
        {
            var fileId = TryReadString(file, "file_id");
            var fileData = TryReadString(file, "file_data");
            var fileUrl = TryReadString(file, "file_url");
            if (!string.IsNullOrWhiteSpace(fileId) ||
                !string.IsNullOrWhiteSpace(fileData) ||
                !string.IsNullOrWhiteSpace(fileUrl))
            {
                JsonObject part = new()
                {
                    ["type"] = "input_file"
                };
                if (!string.IsNullOrWhiteSpace(fileId))
                {
                    part["file_id"] = fileId;
                }

                if (!string.IsNullOrWhiteSpace(fileData))
                {
                    part["file_data"] = fileData;
                }

                if (!string.IsNullOrWhiteSpace(fileUrl))
                {
                    part["file_url"] = fileUrl;
                }

                if (TryReadString(file, "filename") is { Length: > 0 } filename)
                {
                    part["filename"] = filename;
                }

                return part;
            }
        }

        return BuildTextToolOutputPart(obj.ToJsonString());
    }

    private static JsonObject BuildTextToolOutputPart(string text)
        => new()
        {
            ["type"] = "input_text",
            ["text"] = text
        };

    private static JsonObject? BuildResponsesFilePart(JsonObject file)
    {
        var fileId = TryReadString(file, "file_id");
        var fileData = TryReadString(file, "file_data");
        var fileUrl = TryReadString(file, "file_url");
        if (string.IsNullOrWhiteSpace(fileId) &&
            string.IsNullOrWhiteSpace(fileData) &&
            string.IsNullOrWhiteSpace(fileUrl))
        {
            return null;
        }

        JsonObject part = new()
        {
            ["type"] = "input_file"
        };

        if (!string.IsNullOrWhiteSpace(fileId))
        {
            part["file_id"] = fileId;
        }

        if (!string.IsNullOrWhiteSpace(fileData))
        {
            part["file_data"] = fileData;
        }

        if (!string.IsNullOrWhiteSpace(fileUrl))
        {
            part["file_url"] = fileUrl;
        }

        if (TryReadString(file, "filename") is { Length: > 0 } filename)
        {
            part["filename"] = filename;
        }

        return part;
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

    private static JsonArray? ConvertOpenAiToolsToResponsesTools(
        JsonNode? toolsNode,
        IReadOnlyDictionary<string, string> toolNameMap)
    {
        if (toolsNode is not JsonArray tools)
        {
            return null;
        }

        JsonArray converted = new();
        foreach (var tool in tools.OfType<JsonObject>())
        {
            var toolType = TryReadString(tool, "type");
            if (!string.IsNullOrWhiteSpace(toolType) &&
                !string.Equals(toolType, "function", StringComparison.OrdinalIgnoreCase))
            {
                converted.Add(CloneNode(tool));
                continue;
            }

            if (tool["function"] is not JsonObject function)
            {
                continue;
            }

            converted.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = ResolveOpenAiToolName(TryReadString(function, "name") ?? string.Empty, toolNameMap),
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

    private static JsonNode? ConvertOpenAiToolChoiceToResponses(
        JsonNode? toolChoice,
        IReadOnlyDictionary<string, string> toolNameMap)
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
            return new JsonObject
            {
                ["type"] = "function",
                ["name"] = ResolveOpenAiToolName(name, toolNameMap)
            };
        }

        if (toolChoice is JsonObject objectChoice &&
            !string.IsNullOrWhiteSpace(TryReadString(objectChoice, "type")))
        {
            return CloneNode(toolChoice);
        }

        return null;
    }

    private static void ApplyResponsesTextSettings(JsonObject root, JsonObject output)
    {
        if (root["response_format"] is JsonObject responseFormat &&
            ConvertResponseFormatToResponsesTextFormat(responseFormat) is { } format)
        {
            EnsureJsonObject(output, "text")["format"] = format;
        }

        if (root["text"] is JsonObject textOptions &&
            CloneNode(textOptions["verbosity"]) is { } verbosity)
        {
            EnsureJsonObject(output, "text")["verbosity"] = verbosity;
        }
    }

    private static JsonObject? ConvertResponseFormatToResponsesTextFormat(JsonObject responseFormat)
    {
        var type = TryReadString(responseFormat, "type")?.Trim();
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "json_object", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["type"] = type.ToLowerInvariant()
            };
        }

        if (!string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase) ||
            responseFormat["json_schema"] is not JsonObject jsonSchema)
        {
            return null;
        }

        JsonObject format = new()
        {
            ["type"] = "json_schema"
        };

        if (CloneNode(jsonSchema["name"]) is { } name)
        {
            format["name"] = name;
        }

        if (CloneNode(jsonSchema["strict"]) is { } strict)
        {
            format["strict"] = strict;
        }

        if (CloneNode(jsonSchema["schema"]) is { } schema)
        {
            format["schema"] = schema;
        }

        return format;
    }

    private static JsonObject EnsureJsonObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject obj)
        {
            return obj;
        }

        obj = [];
        root[propertyName] = obj;
        return obj;
    }

    private static IReadOnlyDictionary<string, string> BuildOpenAiToolNameMap(JsonNode? toolsNode)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        if (toolsNode is not JsonArray tools)
        {
            return map;
        }

        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (var tool in tools.OfType<JsonObject>())
        {
            var toolType = TryReadString(tool, "type");
            if (!string.IsNullOrWhiteSpace(toolType) &&
                !string.Equals(toolType, "function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (tool["function"] is not JsonObject function)
            {
                continue;
            }

            var name = TryReadString(function, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var candidate = MakeUniqueToolName(BuildCodexToolNameCandidate(name), used);
            used.Add(candidate);
            map[name] = candidate;
        }

        return map;
    }

    private static string ResolveOpenAiToolName(string name, IReadOnlyDictionary<string, string> toolNameMap)
        => !string.IsNullOrWhiteSpace(name) && toolNameMap.TryGetValue(name, out var mapped)
            ? mapped
            : name;

    private static IReadOnlyDictionary<string, string> BuildToolNameAliases(IReadOnlyDictionary<string, string> originalToShort)
    {
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        foreach (var (original, shortened) in originalToShort)
        {
            if (!string.Equals(original, shortened, StringComparison.Ordinal))
            {
                aliases[shortened] = original;
            }
        }

        return aliases;
    }

    private static string BuildCodexToolNameCandidate(string name)
    {
        const int limit = 64;
        if (name.Length <= limit)
        {
            return name;
        }

        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var lastSeparator = name.LastIndexOf("__", StringComparison.Ordinal);
            if (lastSeparator > 0)
            {
                var candidate = $"mcp__{name[(lastSeparator + 2)..]}";
                return candidate.Length <= limit
                    ? candidate
                    : candidate[..limit];
            }
        }

        return name[..limit];
    }

    private static string MakeUniqueToolName(string candidate, HashSet<string> used)
    {
        const int limit = 64;
        if (!used.Contains(candidate))
        {
            return candidate;
        }

        for (var index = 1; ; index++)
        {
            var suffix = $"_{index}";
            var allowed = Math.Max(0, limit - suffix.Length);
            var prefix = candidate.Length > allowed
                ? candidate[..allowed]
                : candidate;
            var unique = prefix + suffix;
            if (!used.Contains(unique))
            {
                return unique;
            }
        }
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
