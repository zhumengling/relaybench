using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RelayBench.Services;

internal static class CodexBackendProtocolAdapter
{
    private const string AnthropicBillingHeaderPrefix = "x-anthropic-billing-header:";

    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string BuildResponsesUrl(TransparentProxyRoute route, bool compact = false)
    {
        var baseUrl = string.IsNullOrWhiteSpace(route.CodexBackendBaseUrl)
            ? CodexOAuthConstants.DefaultBackendBaseUrl
            : route.CodexBackendBaseUrl.Trim();
        return $"{baseUrl.TrimEnd('/')}/responses{(compact ? "/compact" : string.Empty)}";
    }

    public static byte[] NormalizeResponsesPayload(
        byte[] requestBody,
        bool streamRequested,
        bool codexOAuthFastMode = false,
        bool forceStreaming = true)
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

            ApplyCodexResponsesCompatibility(root, codexOAuthFastMode, forceStreaming);
            return JsonSerializer.SerializeToUtf8Bytes(root, CompactJsonOptions);
        }
        catch
        {
            return requestBody;
        }
    }

    public static byte[] ConvertAnthropicMessagesToResponses(byte[] requestBody, bool streamRequested, bool codexOAuthFastMode = false)
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
                ["instructions"] = ReadAnthropicSystemInstructions(root["system"]),
                ["stream"] = streamRequested || ReadBool(root, "stream")
            };

            var toolNameMap = BuildAnthropicToolNameMap(root["tools"]);

            JsonArray input = [];
            if (root["messages"] is JsonArray messages)
            {
                foreach (var messageNode in messages)
                {
                    if (messageNode is JsonObject message)
                    {
                        AddConvertedAnthropicMessage(input, message, toolNameMap);
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

            if (ResolveAnthropicReasoningEffort(root) is { } effort)
            {
                output["reasoning"] = new JsonObject
                {
                    ["effort"] = effort
                };
            }

            var webSearchToolNames = BuildAnthropicWebSearchToolNameSet(root["tools"]);
            if (ConvertAnthropicTools(root["tools"], toolNameMap) is { Count: > 0 } tools)
            {
                output["tools"] = tools;
            }

            if (ConvertAnthropicToolChoice(root["tool_choice"], webSearchToolNames, toolNameMap) is { } toolChoice)
            {
                output["tool_choice"] = toolChoice;
            }

            if (AnthropicToolChoiceDisablesParallelTools(root["tool_choice"]))
            {
                output["parallel_tool_calls"] = false;
            }

            ApplyCodexResponsesCompatibility(output, codexOAuthFastMode, forceStreaming: true);
            return JsonSerializer.SerializeToUtf8Bytes(output, CompactJsonOptions);
        }
        catch
        {
            return requestBody;
        }
    }

    public static byte[] ConvertGeminiNativeToResponses(
        byte[] requestBody,
        string modelName,
        bool streamRequested,
        bool codexOAuthFastMode = false)
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

            var toolNameMap = BuildGeminiToolNameMap(root["tools"]);
            JsonArray input = [];
            AppendGeminiSystemInstruction(input, root["systemInstruction"] ?? root["system_instruction"]);
            AppendGeminiContents(input, root["contents"], toolNameMap);

            JsonObject output = new()
            {
                ["model"] = string.IsNullOrWhiteSpace(modelName) ? "gpt-5.5" : modelName.Trim(),
                ["instructions"] = string.Empty,
                ["input"] = input,
                ["stream"] = streamRequested
            };

            if (ConvertGeminiTools(root["tools"], toolNameMap) is { Count: > 0 } tools)
            {
                output["tools"] = tools;
            }

            if (ConvertGeminiToolChoice(root["toolConfig"] ?? root["tool_config"], toolNameMap) is { } toolChoice)
            {
                output["tool_choice"] = toolChoice;
            }

            if (ResolveGeminiReasoningEffort(root) is { Length: > 0 } effort)
            {
                output["reasoning"] = new JsonObject
                {
                    ["effort"] = effort
                };
            }

            ApplyGeminiResponsesTextFormat(root, output);
            ApplyCodexResponsesCompatibility(output, codexOAuthFastMode, forceStreaming: true);
            return JsonSerializer.SerializeToUtf8Bytes(output, CompactJsonOptions);
        }
        catch
        {
            return requestBody;
        }
    }

    internal static IReadOnlyDictionary<string, string> BuildGeminiToolNameAliases(byte[] requestBody)
    {
        if (requestBody.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var root = JsonNode.Parse(requestBody) as JsonObject;
            if (root is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return BuildToolNameAliases(BuildGeminiToolNameMap(root["tools"]));
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void AppendGeminiSystemInstruction(JsonArray input, JsonNode? systemInstruction)
    {
        if (systemInstruction is not JsonObject system ||
            system["parts"] is not JsonArray parts)
        {
            return;
        }

        JsonArray content = [];
        foreach (var partNode in parts)
        {
            if (partNode is JsonObject part)
            {
                var text = ReadString(part, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    content.Add(BuildTextPart(text, "developer"));
                }
            }
        }

        if (content.Count > 0)
        {
            input.Add(BuildGeminiMessageItem("developer", content));
        }
    }

    private static void AppendGeminiContents(
        JsonArray input,
        JsonNode? contentsNode,
        IReadOnlyDictionary<string, string> toolNameMap)
    {
        if (contentsNode is not JsonArray contents)
        {
            return;
        }

        Queue<string> pendingCallIds = [];
        foreach (var contentNode in contents)
        {
            if (contentNode is not JsonObject content ||
                content["parts"] is not JsonArray parts)
            {
                continue;
            }

            var role = MapGeminiRole(ReadString(content, "role"));
            JsonArray messageParts = [];
            foreach (var partNode in parts)
            {
                if (partNode is not JsonObject part)
                {
                    continue;
                }

                if (part.TryGetPropertyValue("functionCall", out var functionCallNode) &&
                    functionCallNode is JsonObject functionCall)
                {
                    messageParts = FlushGeminiMessageParts(input, role, messageParts);
                    var callId = $"call_relaybench_{Guid.NewGuid():N}";
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = callId,
                        ["name"] = ResolveGeminiToolName(ReadString(functionCall, "name"), toolNameMap),
                        ["arguments"] = functionCall["args"]?.ToJsonString(CompactJsonOptions) ?? "{}"
                    });
                    pendingCallIds.Enqueue(callId);
                    continue;
                }

                if (part.TryGetPropertyValue("functionResponse", out var functionResponseNode) &&
                    functionResponseNode is JsonObject functionResponse)
                {
                    messageParts = FlushGeminiMessageParts(input, role, messageParts);
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = pendingCallIds.TryDequeue(out var callId)
                            ? callId
                            : $"call_relaybench_{Guid.NewGuid():N}",
                        ["output"] = ReadGeminiFunctionResponseOutput(functionResponse)
                    });
                    continue;
                }

                AppendGeminiContentPart(messageParts, part, role);
            }

            FlushGeminiMessageParts(input, role, messageParts);
        }
    }

    private static JsonArray FlushGeminiMessageParts(JsonArray input, string role, JsonArray messageParts)
    {
        if (messageParts.Count > 0)
        {
            input.Add(BuildGeminiMessageItem(role, messageParts));
        }

        return [];
    }

    private static JsonObject BuildGeminiMessageItem(string role, JsonArray content)
        => new()
        {
            ["type"] = "message",
            ["role"] = role,
            ["content"] = content
        };

    private static void AppendGeminiContentPart(JsonArray messageParts, JsonObject part, string role)
    {
        var text = ReadString(part, "text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            messageParts.Add(BuildTextPart(text, role));
            return;
        }

        if ((part["inlineData"] ?? part["inline_data"]) is JsonObject inlineData)
        {
            AppendGeminiInlineDataPart(messageParts, inlineData);
            return;
        }

        if ((part["fileData"] ?? part["file_data"]) is JsonObject fileData)
        {
            var fileUri = ReadString(fileData, "fileUri");
            if (string.IsNullOrWhiteSpace(fileUri))
            {
                fileUri = ReadString(fileData, "file_uri");
            }

            if (!string.IsNullOrWhiteSpace(fileUri))
            {
                JsonObject filePart = new()
                {
                    ["type"] = "input_file",
                    ["file_url"] = fileUri
                };
                var mimeType = ReadGeminiMimeType(fileData);
                if (!string.IsNullOrWhiteSpace(mimeType))
                {
                    filePart["media_type"] = mimeType;
                }

                messageParts.Add(filePart);
            }
        }
    }

    private static void AppendGeminiInlineDataPart(JsonArray messageParts, JsonObject inlineData)
    {
        var data = ReadString(inlineData, "data");
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        var mimeType = ReadGeminiMimeType(inlineData);
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            messageParts.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = $"data:{mimeType};base64,{data}"
            });
            return;
        }

        messageParts.Add(new JsonObject
        {
            ["type"] = "input_file",
            ["file_data"] = BuildDataUrl(mimeType, data),
            ["media_type"] = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType
        });
    }

    private static string BuildDataUrl(string mimeType, string base64Data)
    {
        if (base64Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return base64Data;
        }

        var safeMimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType;
        return $"data:{safeMimeType};base64,{base64Data}";
    }

    private static string ReadGeminiMimeType(JsonObject source)
    {
        var mimeType = ReadString(source, "mimeType");
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            mimeType = ReadString(source, "mime_type");
        }

        return string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType;
    }

    private static string ReadGeminiFunctionResponseOutput(JsonObject functionResponse)
    {
        var response = functionResponse["response"];
        if (response is JsonObject responseObject &&
            responseObject.TryGetPropertyValue("result", out var result) &&
            result is not null)
        {
            return ReadContentText(result);
        }

        return response is null
            ? string.Empty
            : ReadContentText(response);
    }

    private static JsonArray? ConvertGeminiTools(JsonNode? toolsNode, IReadOnlyDictionary<string, string> toolNameMap)
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

            var declarations = tool["functionDeclarations"] ?? tool["function_declarations"];
            if (declarations is not JsonArray functionDeclarations)
            {
                continue;
            }

            foreach (var declarationNode in functionDeclarations)
            {
                if (declarationNode is not JsonObject declaration)
                {
                    continue;
                }

                var name = ReadString(declaration, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                converted.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = ResolveGeminiToolName(name, toolNameMap),
                    ["description"] = ReadString(declaration, "description"),
                    ["parameters"] = CleanGeminiToolParameters(declaration["parameters"] ?? declaration["parametersJsonSchema"]),
                    ["strict"] = false
                });
            }
        }

        return converted;
    }

    private static JsonObject CleanGeminiToolParameters(JsonNode? parameters)
    {
        var clone = CleanToolParameters(parameters);
        if (!clone.ContainsKey("additionalProperties"))
        {
            clone["additionalProperties"] = false;
        }

        return clone;
    }

    private static JsonNode? ConvertGeminiToolChoice(JsonNode? toolConfigNode, IReadOnlyDictionary<string, string> toolNameMap)
    {
        if (toolConfigNode is not JsonObject toolConfig)
        {
            return null;
        }

        var functionCallingConfig = toolConfig["functionCallingConfig"] ?? toolConfig["function_calling_config"];
        if (functionCallingConfig is not JsonObject config)
        {
            return null;
        }

        var mode = ReadString(config, "mode").Trim().ToUpperInvariant();
        if (mode == "NONE")
        {
            return "none";
        }

        if (mode == "AUTO")
        {
            return "auto";
        }

        if (mode != "ANY")
        {
            return null;
        }

        var allowedNames = config["allowedFunctionNames"] ?? config["allowed_function_names"];
        if (allowedNames is JsonArray names &&
            names.Count == 1 &&
            ReadString(names[0]) is { Length: > 0 } name)
        {
            return new JsonObject
            {
                ["type"] = "function",
                ["name"] = ResolveGeminiToolName(name, toolNameMap)
            };
        }

        return "required";
    }

    private static string? ResolveGeminiReasoningEffort(JsonObject root)
    {
        var generationConfig = root["generationConfig"] ?? root["generation_config"];
        if (generationConfig is not JsonObject config)
        {
            return null;
        }

        var thinkingConfig = config["thinkingConfig"] ?? config["thinking_config"];
        if (thinkingConfig is not JsonObject thinking)
        {
            return null;
        }

        var level = ReadString(thinking, "thinkingLevel");
        if (string.IsNullOrWhiteSpace(level))
        {
            level = ReadString(thinking, "thinking_level");
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            return level.Trim().ToLowerInvariant();
        }

        var budget = ReadLong(thinking["thinkingBudget"]) ?? ReadLong(thinking["thinking_budget"]);
        return budget.HasValue ? ConvertThinkingBudgetToEffort(budget.Value) : null;
    }

    private static void ApplyGeminiResponsesTextFormat(JsonObject root, JsonObject output)
    {
        if (ConvertGeminiGenerationConfigToResponsesTextFormat(root) is not { } format)
        {
            return;
        }

        var text = output["text"] as JsonObject ?? [];
        text["format"] = format;
        output["text"] = text;
    }

    private static JsonObject? ConvertGeminiGenerationConfigToResponsesTextFormat(JsonObject root)
    {
        var generationConfig = root["generationConfig"] ?? root["generation_config"];
        if (generationConfig is not JsonObject config)
        {
            return null;
        }

        var responseSchema =
            config["responseSchema"] ??
            config["response_schema"] ??
            config["responseJsonSchema"] ??
            config["response_json_schema"];
        if (responseSchema is JsonObject schema)
        {
            return new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = ResolveGeminiResponseSchemaName(config, schema),
                ["schema"] = CleanGeminiResponseSchema(schema),
                ["strict"] = false
            };
        }

        var mimeType = ReadString(config, "responseMimeType");
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            mimeType = ReadString(config, "response_mime_type");
        }

        if (!IsJsonMimeType(mimeType))
        {
            return null;
        }

        return new JsonObject
        {
            ["type"] = "json_object"
        };
    }

    private static JsonObject CleanGeminiResponseSchema(JsonObject schema)
    {
        var clone = schema.DeepClone() as JsonObject ?? [];
        RemoveJsonPropertyRecursive(clone, "$schema");
        RemoveJsonPropertyRecursive(clone, "$id");
        RemoveJsonPropertyRecursive(clone, "propertyOrdering");
        NormalizeGeminiSchemaTypes(clone);
        return clone;
    }

    private static void NormalizeGeminiSchemaTypes(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue value &&
                value.TryGetValue<string>(out var type) &&
                !string.IsNullOrWhiteSpace(type))
            {
                obj["type"] = type.Trim().ToLowerInvariant();
            }

            foreach (var child in obj.Select(static item => item.Value).ToArray())
            {
                NormalizeGeminiSchemaTypes(child);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array.ToArray())
            {
                NormalizeGeminiSchemaTypes(child);
            }
        }
    }

    private static string ResolveGeminiResponseSchemaName(JsonObject config, JsonObject schema)
    {
        var name = ReadString(config, "responseSchemaName");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadString(config, "response_schema_name");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadString(schema, "title");
        }

        return SanitizeResponsesTextFormatName(name, "gemini_response_schema");
    }

    private static string SanitizeResponsesTextFormatName(string name, string fallback)
    {
        const int limit = 64;
        var builder = new System.Text.StringBuilder(limit);
        foreach (var ch in name.Trim())
        {
            if ((ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9') ||
                ch is '_' or '-')
            {
                builder.Append(ch);
            }
            else if (ch is ' ' or '.' or ':' or '/')
            {
                builder.Append('_');
            }

            if (builder.Length >= limit)
            {
                break;
            }
        }

        return builder.Length > 0 ? builder.ToString() : fallback;
    }

    private static bool IsJsonMimeType(string mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        var normalized = mimeType.Trim();
        var parameterStart = normalized.IndexOf(';');
        if (parameterStart >= 0)
        {
            normalized = normalized[..parameterStart].Trim();
        }

        return string.Equals(normalized, "application/json", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ConvertThinkingBudgetToEffort(long budget)
        => TransparentProxyThinkingSuffix.ConvertBudgetToEffort(budget);

    private static IReadOnlyDictionary<string, string> BuildGeminiToolNameMap(JsonNode? toolsNode)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        if (toolsNode is not JsonArray tools)
        {
            return map;
        }

        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (var toolNode in tools)
        {
            if (toolNode is not JsonObject tool)
            {
                continue;
            }

            var declarations = tool["functionDeclarations"] ?? tool["function_declarations"];
            if (declarations is not JsonArray functionDeclarations)
            {
                continue;
            }

            foreach (var declarationNode in functionDeclarations)
            {
                if (declarationNode is not JsonObject declaration)
                {
                    continue;
                }

                var name = ReadString(declaration, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var candidate = MakeUniqueToolName(BuildCodexToolNameCandidate(name), used);
                used.Add(candidate);
                map[name] = candidate;
            }
        }

        return map;
    }

    private static string ResolveGeminiToolName(string name, IReadOnlyDictionary<string, string> toolNameMap)
        => !string.IsNullOrWhiteSpace(name) && toolNameMap.TryGetValue(name, out var mapped)
            ? mapped
            : BuildCodexToolNameCandidate(name);

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

    private static string MapGeminiRole(string role)
        => role.Trim().ToLowerInvariant() switch
        {
            "model" or "assistant" => "assistant",
            "system" or "developer" => "developer",
            _ => "user"
        };

    private static string ReadAnthropicSystemInstructions(JsonNode? system)
    {
        if (system is null)
        {
            return string.Empty;
        }

        if (system is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return StripLeadingAnthropicBillingHeader(text ?? string.Empty);
        }

        if (system is not JsonArray parts)
        {
            return StripLeadingAnthropicBillingHeader(ReadContentText(system));
        }

        List<string> instructions = [];
        foreach (var part in parts)
        {
            var textPart = part is JsonObject obj
                ? ReadString(obj, "text")
                : ReadContentText(part);
            if (IsClaudeCodeAttributionSystemText(textPart))
            {
                continue;
            }

            textPart = StripLeadingAnthropicBillingHeader(textPart);
            if (!string.IsNullOrWhiteSpace(textPart))
            {
                instructions.Add(textPart);
            }
        }

        return string.Join("\n\n", instructions);
    }

    private static string StripLeadingAnthropicBillingHeader(string text)
    {
        var headerStart = FindFirstNonWhiteSpaceIndex(text);
        if (!text.AsSpan(headerStart).StartsWith(AnthropicBillingHeaderPrefix, StringComparison.Ordinal))
        {
            return text;
        }

        var lineEnd = text.IndexOfAny(['\n', '\r'], headerStart);
        if (lineEnd < 0)
        {
            return string.Empty;
        }

        var restStart = lineEnd + 1;
        if (text[lineEnd] == '\r' &&
            restStart < text.Length &&
            text[restStart] == '\n')
        {
            restStart++;
        }

        var rest = text[restStart..];
        if (rest.StartsWith("\r\n", StringComparison.Ordinal))
        {
            return rest[2..];
        }

        if (rest.StartsWith('\n') || rest.StartsWith('\r'))
        {
            return rest[1..];
        }

        return rest;
    }

    private static bool IsClaudeCodeAttributionSystemText(string text)
    {
        var headerStart = FindFirstNonWhiteSpaceIndex(text);
        return text.AsSpan(headerStart).StartsWith(AnthropicBillingHeaderPrefix, StringComparison.Ordinal);
    }

    private static int FindFirstNonWhiteSpaceIndex(string text)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static void AddConvertedAnthropicMessage(
        JsonArray input,
        JsonObject message,
        IReadOnlyDictionary<string, string> toolNameMap)
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

        if (message["content"] is JsonValue)
        {
            var text = ReadContentText(message["content"]);
            if (!string.IsNullOrWhiteSpace(text))
            {
                input.Add(BuildMessageItem(role, new JsonArray(BuildTextPart(text, role))));
            }

            return;
        }

        if (message["content"] is not JsonArray parts)
        {
            input.Add(new JsonObject { ["role"] = role });
            return;
        }

        JsonArray messageContent = [];
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
                    messageContent.Add(BuildTextPart(text, role));
                }
            }
            else if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
            {
                if (BuildAnthropicImagePart(obj) is { } imagePart)
                {
                    messageContent.Add(imagePart);
                }
            }
            else if (string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                if (BuildAnthropicReasoningItem(role, obj) is { } reasoningItem)
                {
                    messageContent = FlushMessageContent(input, role, messageContent);
                    input.Add(reasoningItem);
                }
            }
            else if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                messageContent = FlushMessageContent(input, role, messageContent);
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = ShortenCodexCallIdIfNeeded(ReadString(obj, "tool_use_id")),
                    ["output"] = BuildAnthropicToolResultOutput(obj["content"])
                });
            }
            else if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                messageContent = FlushMessageContent(input, role, messageContent);
                input.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = ShortenCodexCallIdIfNeeded(ReadString(obj, "id")),
                    ["name"] = ResolveAnthropicToolName(ReadString(obj, "name"), toolNameMap),
                    ["arguments"] = obj["input"]?.ToJsonString(CompactJsonOptions) ?? "{}"
                });
            }
        }

        FlushMessageContent(input, role, messageContent);
    }

    private static JsonArray FlushMessageContent(JsonArray input, string role, JsonArray content)
    {
        if (content.Count == 0)
        {
            return content;
        }

        input.Add(BuildMessageItem(role, content));
        return [];
    }

    private static JsonObject BuildMessageItem(string role, JsonArray content)
        => new()
        {
            ["role"] = role,
            ["content"] = content
        };

    private static JsonObject? BuildAnthropicImagePart(JsonObject part)
    {
        if (part["source"] is not JsonObject source)
        {
            return null;
        }

        var data = ReadString(source, "data");
        if (string.IsNullOrWhiteSpace(data))
        {
            data = ReadString(source, "base64");
            if (string.IsNullOrWhiteSpace(data))
            {
                return null;
            }
        }

        var mediaType = ReadString(source, "media_type");
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            mediaType = ReadString(source, "mime_type");
        }

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            mediaType = "image/png";
        }

        return new JsonObject
        {
            ["type"] = "input_image",
            ["image_url"] = $"data:{mediaType};base64,{data}"
        };
    }

    private static JsonObject? BuildAnthropicReasoningItem(string role, JsonObject part)
    {
        if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var signature = ReadString(part, "signature");
        if (!IsFernetLikeReasoningSignature(signature))
        {
            return null;
        }

        return new JsonObject
        {
            ["type"] = "reasoning",
            ["summary"] = new JsonArray(),
            ["content"] = null,
            ["encrypted_content"] = signature.Trim()
        };
    }

    private static JsonNode BuildAnthropicToolResultOutput(JsonNode? content)
    {
        if (content is not JsonArray parts)
        {
            return JsonValue.Create(ReadContentText(content))!;
        }

        JsonArray output = [];
        foreach (var partNode in parts)
        {
            if (partNode is JsonValue value &&
                value.TryGetValue<string>(out var textValue) &&
                !string.IsNullOrWhiteSpace(textValue))
            {
                output.Add(BuildTextPart(textValue, "user"));
                continue;
            }

            if (partNode is not JsonObject part)
            {
                continue;
            }

            var type = ReadString(part, "type");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                var partText = ReadString(part, "text");
                if (!string.IsNullOrWhiteSpace(partText))
                {
                    output.Add(BuildTextPart(partText, "user"));
                }
            }
            else if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase) &&
                     BuildAnthropicImagePart(part) is { } imagePart)
            {
                output.Add(imagePart);
            }
        }

        return output.Count > 0
            ? output
            : JsonValue.Create(ReadContentText(content))!;
    }

    private static string ShortenCodexCallIdIfNeeded(string callId)
    {
        const int limit = 64;
        if (callId.Length <= limit)
        {
            return callId;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(callId));
        var suffix = "_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        var prefixLength = limit - suffix.Length;
        return prefixLength <= 0
            ? suffix[^limit..]
            : callId[..prefixLength] + suffix;
    }

    private static bool IsFernetLikeReasoningSignature(string signature)
    {
        const int fernetVersionLength = 1;
        const int fernetTimestampLength = 8;
        const int fernetIvLength = 16;
        const int fernetHmacLength = 32;
        const int aesBlockSize = 16;

        signature = signature.Trim();
        if (!signature.StartsWith("gAAAA", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryDecodeBase64Url(signature, out var decoded))
        {
            return false;
        }

        var minLength = fernetVersionLength + fernetTimestampLength + fernetIvLength + aesBlockSize + fernetHmacLength;
        if (decoded.Length < minLength || decoded[0] != 0x80)
        {
            return false;
        }

        var ciphertextLength = decoded.Length - fernetVersionLength - fernetTimestampLength - fernetIvLength - fernetHmacLength;
        return ciphertextLength > 0 && ciphertextLength % aesBlockSize == 0;
    }

    private static bool TryDecodeBase64Url(string value, out byte[] decoded)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 0:
                break;
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
            default:
                decoded = [];
                return false;
        }

        try
        {
            decoded = Convert.FromBase64String(normalized);
            return true;
        }
        catch
        {
            decoded = [];
            return false;
        }
    }

    private static JsonNode? ConvertAnthropicToolChoice(
        JsonNode? toolChoice,
        IReadOnlySet<string> webSearchToolNames,
        IReadOnlyDictionary<string, string> toolNameMap)
    {
        if (toolChoice is null)
        {
            return null;
        }

        var choiceType = toolChoice is JsonObject obj
            ? ReadString(obj, "type")
            : ReadString(toolChoice);

        if (string.IsNullOrWhiteSpace(choiceType))
        {
            return null;
        }

        if (string.Equals(choiceType, "any", StringComparison.OrdinalIgnoreCase))
        {
            return "required";
        }

        if (string.Equals(choiceType, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(choiceType, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(choiceType, "required", StringComparison.OrdinalIgnoreCase))
        {
            return choiceType.ToLowerInvariant();
        }

        if (string.Equals(choiceType, "tool", StringComparison.OrdinalIgnoreCase) &&
            toolChoice is JsonObject toolObject)
        {
            var name = ReadString(toolObject, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (webSearchToolNames.Contains(name))
                {
                    return new JsonObject
                    {
                        ["type"] = "web_search"
                    };
                }

                return new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = ResolveAnthropicToolName(name, toolNameMap)
                };
            }
        }

        return null;
    }

    private static bool AnthropicToolChoiceDisablesParallelTools(JsonNode? toolChoice)
        => toolChoice is JsonObject obj &&
           obj.TryGetPropertyValue("disable_parallel_tool_use", out var disableParallelToolUse) &&
           ReadBool(disableParallelToolUse);

    private static string? ResolveAnthropicReasoningEffort(JsonObject root)
    {
        if (root["output_config"] is JsonObject outputConfig)
        {
            var effort = ReadString(outputConfig, "effort");
            if (IsAnthropicReasoningEffort(effort))
            {
                return effort.ToLowerInvariant();
            }

            if (string.Equals(effort, "max", StringComparison.OrdinalIgnoreCase))
            {
                return "xhigh";
            }
        }

        if (root["thinking"] is not JsonObject thinking)
        {
            return null;
        }

        var type = ReadString(thinking, "type");
        if (string.Equals(type, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        if (string.Equals(type, "adaptive", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "xhigh";
        }

        if (!string.Equals(type, "enabled", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var budget = ReadLong(thinking["budget_tokens"]);
        if (budget is null)
        {
            return null;
        }

        if (budget < 0)
        {
            return budget == -1
                ? "auto"
                : null;
        }

        if (budget == 0)
        {
            return "none";
        }

        if (budget <= 1024)
        {
            return "low";
        }

        if (budget <= 8192)
        {
            return "medium";
        }

        return budget <= 24576
            ? "high"
            : "xhigh";
    }

    private static bool IsAnthropicReasoningEffort(string effort)
        => string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(effort, "auto", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(effort, "low", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(effort, "medium", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(effort, "high", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(effort, "xhigh", StringComparison.OrdinalIgnoreCase);

    private static JsonObject CleanToolParameters(JsonNode? parameters)
    {
        var clone = parameters?.DeepClone() as JsonObject ?? new JsonObject();
        RemoveJsonPropertyRecursive(clone, "$schema");
        if (!clone.TryGetPropertyValue("type", out var typeNode) ||
            string.IsNullOrWhiteSpace(ReadString(typeNode)))
        {
            clone["type"] = "object";
        }

        if (string.Equals(ReadString(clone["type"]), "object", StringComparison.OrdinalIgnoreCase) &&
            !clone.ContainsKey("properties"))
        {
            clone["properties"] = new JsonObject();
        }

        return clone;
    }

    private static void RemoveJsonPropertyRecursive(JsonNode? node, string propertyName)
    {
        if (node is JsonObject obj)
        {
            obj.Remove(propertyName);
            foreach (var child in obj.Select(static item => item.Value).ToArray())
            {
                RemoveJsonPropertyRecursive(child, propertyName);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array.ToArray())
            {
                RemoveJsonPropertyRecursive(child, propertyName);
            }
        }
    }

    private static JsonObject BuildTextPart(string text, string role)
        => new()
        {
            ["type"] = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "output_text"
                : "input_text",
            ["text"] = text
        };

    public static byte[] EnsureResponsesInstructions(
        byte[] requestBody,
        bool codexOAuthFastMode = false,
        bool forceStreaming = true)
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

            ApplyCodexResponsesCompatibility(root, codexOAuthFastMode, forceStreaming);
            return JsonSerializer.SerializeToUtf8Bytes(root, CompactJsonOptions);
        }
        catch
        {
            return requestBody;
        }
    }

    private static void EnsureInstructions(JsonObject root)
    {
        if (!root.TryGetPropertyValue("instructions", out var node) ||
            node is null ||
            node.GetValueKind() == JsonValueKind.Null)
        {
            root["instructions"] = string.Empty;
        }
    }

    private static void ApplyCodexResponsesCompatibility(JsonObject root, bool codexOAuthFastMode, bool forceStreaming)
    {
        EnsureInstructions(root);
        NormalizeInput(root);
        NormalizeReasoning(root);
        ApplyModelThinkingSuffix(root);
        EnsureInclude(root, "reasoning.encrypted_content");
        EnsureToolsArray(root);
        NormalizeCodexBuiltinTools(root);

        if (forceStreaming)
        {
            root["stream"] = true;
        }
        else
        {
            root.Remove("stream");
        }

        root["store"] = false;
        root["parallel_tool_calls"] = ReadBool(root["parallel_tool_calls"], fallback: true);

        root.Remove("previous_response_id");
        root.Remove("prompt_cache_retention");
        root.Remove("safety_identifier");
        root.Remove("stream_options");
        root.Remove("max_output_tokens");
        root.Remove("max_completion_tokens");
        root.Remove("max_tokens");
        root.Remove("temperature");
        root.Remove("top_p");
        root.Remove("top_k");
        root.Remove("truncation");
        root.Remove("context_management");
        root.Remove("user");

        if (root.TryGetPropertyValue("service_tier", out var serviceTier) &&
            !string.Equals(ReadString(serviceTier), "priority", StringComparison.OrdinalIgnoreCase))
        {
            root.Remove("service_tier");
        }

        if (codexOAuthFastMode)
        {
            root["service_tier"] = "priority";
        }
    }

    private static void EnsureToolsArray(JsonObject root)
    {
        if (!root.TryGetPropertyValue("tools", out var tools) ||
            tools is null ||
            tools.GetValueKind() == JsonValueKind.Null)
        {
            root["tools"] = new JsonArray();
        }
    }

    private static void NormalizeCodexBuiltinTools(JsonObject root)
    {
        if (root["tools"] is JsonArray tools)
        {
            foreach (var item in tools)
            {
                if (item is JsonObject tool)
                {
                    NormalizeCodexBuiltinToolType(tool, "type");
                }
            }
        }

        if (root["tool_choice"] is JsonObject toolChoice)
        {
            NormalizeCodexBuiltinToolType(toolChoice, "type");
            if (toolChoice["tools"] is JsonArray toolChoiceTools)
            {
                foreach (var item in toolChoiceTools)
                {
                    if (item is JsonObject tool)
                    {
                        NormalizeCodexBuiltinToolType(tool, "type");
                    }
                }
            }
        }
    }

    private static void NormalizeCodexBuiltinToolType(JsonObject obj, string propertyName)
    {
        var current = ReadString(obj, propertyName);
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        if (string.Equals(current, "web_search_preview", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(current, "web_search_preview_2025_03_11", StringComparison.OrdinalIgnoreCase))
        {
            obj[propertyName] = "web_search";
        }
    }

    private static void NormalizeInput(JsonObject root)
    {
        if (!root.TryGetPropertyValue("input", out var input) || input is null)
        {
            return;
        }

        if (input is JsonValue && input.GetValueKind() == JsonValueKind.String)
        {
            root["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = ReadString(input)
                        }
                    }
                }
            };
            return;
        }

        if (input is not JsonArray items)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item is JsonObject message &&
                string.Equals(ReadString(message, "role"), "system", StringComparison.OrdinalIgnoreCase))
            {
                message["role"] = "developer";
            }
        }
    }

    private static void NormalizeReasoning(JsonObject root)
    {
        if (root["reasoning"] is not JsonObject reasoning)
        {
            reasoning = [];
            root["reasoning"] = reasoning;
        }

        if (!reasoning.TryGetPropertyValue("effort", out var effort) ||
            effort is null ||
            effort.GetValueKind() == JsonValueKind.Null ||
            string.IsNullOrWhiteSpace(ReadString(effort)))
        {
            reasoning["effort"] = "medium";
        }

        if (!reasoning.TryGetPropertyValue("summary", out var summary) ||
            summary is null ||
            summary.GetValueKind() == JsonValueKind.Null ||
            string.IsNullOrWhiteSpace(ReadString(summary)))
        {
            reasoning["summary"] = "auto";
        }
    }

    private static void ApplyModelThinkingSuffix(JsonObject root)
    {
        var model = ReadString(root, "model");
        if (!TransparentProxyThinkingSuffix.TryParse(model, out var suffix))
        {
            return;
        }

        root["model"] = suffix.ModelName;
        var reasoning = root["reasoning"] as JsonObject ?? [];
        reasoning["effort"] = suffix.Effort;
        root["reasoning"] = reasoning;
    }

    private static void EnsureInclude(JsonObject root, string value)
    {
        if (root["include"] is not JsonArray include)
        {
            root["include"] = new JsonArray(value);
            return;
        }

        if (include.Any(item => string.Equals(ReadString(item), value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        include.Add(value);
    }

    private static JsonArray? ConvertAnthropicTools(
        JsonNode? toolsNode,
        IReadOnlyDictionary<string, string> toolNameMap)
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

            if (IsAnthropicWebSearchTool(ReadString(tool, "type")))
            {
                converted.Add(ConvertAnthropicWebSearchTool(tool));
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
                ["name"] = ResolveAnthropicToolName(name, toolNameMap),
                ["description"] = ReadString(tool, "description"),
                ["parameters"] = CleanToolParameters(tool["input_schema"]),
                ["strict"] = false
            });
        }

        return converted;
    }

    private static IReadOnlyDictionary<string, string> BuildAnthropicToolNameMap(JsonNode? toolsNode)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        if (toolsNode is not JsonArray tools)
        {
            return map;
        }

        HashSet<string> used = new(StringComparer.Ordinal);
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

            var candidate = MakeUniqueToolName(BuildCodexToolNameCandidate(name), used);
            used.Add(candidate);
            map[name] = candidate;
        }

        return map;
    }

    private static string ResolveAnthropicToolName(string name, IReadOnlyDictionary<string, string> toolNameMap)
        => !string.IsNullOrWhiteSpace(name) && toolNameMap.TryGetValue(name, out var mapped)
            ? mapped
            : name;

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

    private static IReadOnlySet<string> BuildAnthropicWebSearchToolNameSet(JsonNode? toolsNode)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if (toolsNode is not JsonArray tools)
        {
            return names;
        }

        foreach (var toolNode in tools)
        {
            if (toolNode is not JsonObject tool ||
                !IsAnthropicWebSearchTool(ReadString(tool, "type")))
            {
                continue;
            }

            var name = ReadString(tool, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static bool IsAnthropicWebSearchTool(string toolType)
        => string.Equals(toolType, "web_search_20250305", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(toolType, "web_search_20260209", StringComparison.OrdinalIgnoreCase);

    private static JsonObject ConvertAnthropicWebSearchTool(JsonObject tool)
    {
        JsonObject converted = new()
        {
            ["type"] = "web_search"
        };

        if (tool["allowed_domains"] is JsonArray allowedDomains)
        {
            converted["filters"] = new JsonObject
            {
                ["allowed_domains"] = allowedDomains.DeepClone()
            };
        }

        if (tool["user_location"] is JsonObject userLocation)
        {
            converted["user_location"] = userLocation.DeepClone();
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

    private static string ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonObject root, string propertyName)
    {
        if (!root.TryGetPropertyValue(propertyName, out var node) ||
            node is not JsonValue value)
        {
            return false;
        }

        return value.TryGetValue<bool>(out var parsed) && parsed;
    }

    private static bool ReadBool(JsonNode? node, bool fallback = false)
        => node is JsonValue value && value.TryGetValue<bool>(out var parsed)
            ? parsed
            : fallback;

    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var parsedLong))
        {
            return parsedLong;
        }

        if (value.TryGetValue<int>(out var parsedInt))
        {
            return parsedInt;
        }

        if (value.TryGetValue<double>(out var parsedDouble) &&
            !double.IsNaN(parsedDouble) &&
            !double.IsInfinity(parsedDouble))
        {
            return (long)Math.Floor(parsedDouble);
        }

        return null;
    }
}
