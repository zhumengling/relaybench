using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RelayBench.Services;

internal static class TransparentProxyGeminiAdapter
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static bool IsGeminiNativeRoute(TransparentProxyRoute route)
    {
        if (Uri.TryCreate(route.BaseUrl, UriKind.Absolute, out var uri) &&
            (uri.Host.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
             uri.AbsolutePath.Contains("v1beta", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public static TransparentProxyPreparedRequest BuildOpenAiChatPreparedRequest(
        TransparentProxyRoute route,
        byte[] requestBody,
        string clientModel,
        string upstreamModel,
        bool streamRequested,
        bool isToolExchange,
        bool preferJsonStreamExtraction,
        Func<string, string, string> buildUpstreamUrl)
    {
        var requestedModel = ResolveModel(route, requestBody, upstreamModel);
        TransparentProxyThinkingSuffixResult? thinkingSuffix =
            TransparentProxyThinkingSuffix.TryParse(requestedModel, out var parsedSuffix)
                ? parsedSuffix
                : null;
        var model = thinkingSuffix.HasValue ? thinkingSuffix.Value.ModelName : requestedModel;
        var converted = ConvertOpenAiChatToGemini(requestBody, model, thinkingSuffix, out var toolNameAliases);
        var action = streamRequested
            ? "streamGenerateContent?alt=sse"
            : "generateContent";
        var relativePath = $"/v1beta/models/{Uri.EscapeDataString(model)}:{action}";
        return new TransparentProxyPreparedRequest(
            TransparentProxyNativeWireApis.Gemini,
            RelayBench.Core.Services.ProxyWireApiProbeService.ChatCompletionsWireApi,
            buildUpstreamUrl(route.BaseUrl, relativePath),
            converted,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            NormalizeToChatCompletions: true,
            string.IsNullOrWhiteSpace(clientModel) ? model : clientModel,
            model,
            isToolExchange,
            preferJsonStreamExtraction,
            toolNameAliases.Count == 0 ? null : toolNameAliases);
    }

    private static byte[] ConvertOpenAiChatToGemini(
        byte[] requestBody,
        string model,
        TransparentProxyThinkingSuffixResult? thinkingSuffix,
        out IReadOnlyDictionary<string, string> toolNameAliases)
    {
        var toolNameMap = new GeminiToolNameMap();
        toolNameAliases = toolNameMap.SanitizedToOriginal;
        if (requestBody.Length == 0)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new { contents = Array.Empty<object>() }, CompactJsonOptions);
        }

        try
        {
            var root = JsonNode.Parse(requestBody) as JsonObject;
            if (root is null)
            {
                return requestBody;
            }

            JsonObject output = [];
            JsonArray contents = [];
            JsonArray systemParts = [];
            Dictionary<string, string> toolCallNamesById = new(StringComparer.Ordinal);

            if (root["messages"] is JsonArray messages)
            {
                foreach (var item in messages)
                {
                    if (item is not JsonObject message)
                    {
                        continue;
                    }

                    var role = ReadString(message, "role").Trim().ToLowerInvariant();
                    if (role is "system" or "developer")
                    {
                        AppendTextParts(message["content"], systemParts);
                        continue;
                    }

                    var geminiRole = role == "assistant" ? "model" : "user";
                    JsonArray parts = [];
                    if (role == "tool")
                    {
                        var functionName = ResolveToolResponseName(message, toolCallNamesById, toolNameMap);
                        parts.Add(BuildFunctionResponsePart(functionName, message["content"]));
                    }
                    else
                    {
                        AppendContentParts(message["content"], parts);
                        AppendFunctionCalls(message["tool_calls"], parts, toolNameMap, toolCallNamesById);
                    }

                    if (parts.Count > 0)
                    {
                        contents.Add(new JsonObject
                        {
                            ["role"] = geminiRole,
                            ["parts"] = parts
                        });
                    }
                }
            }

            if (contents.Count == 0)
            {
                contents.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray(new JsonObject
                    {
                        ["text"] = ReadContentAsString(root["prompt"]) is { Length: > 0 } prompt
                            ? prompt
                            : "Hello"
                    })
                });
            }

            output["contents"] = contents;
            if (systemParts.Count > 0)
            {
                output["systemInstruction"] = new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = systemParts
                };
            }

            if (BuildGenerationConfig(root, thinkingSuffix) is { Count: > 0 } generationConfig)
            {
                output["generationConfig"] = generationConfig;
            }

            if (BuildGeminiTools(root["tools"], toolNameMap) is { Count: > 0 } tools)
            {
                output["tools"] = tools;
            }

            if (BuildGeminiToolConfig(root["tool_choice"], toolNameMap) is { Count: > 0 } toolConfig)
            {
                output["toolConfig"] = toolConfig;
            }

            toolNameAliases = toolNameMap.SanitizedToOriginal;
            return JsonSerializer.SerializeToUtf8Bytes(output, CompactJsonOptions);
        }
        catch
        {
            toolNameAliases = toolNameMap.SanitizedToOriginal;
            return requestBody;
        }
    }

    private static JsonObject BuildGenerationConfig(
        JsonObject root,
        TransparentProxyThinkingSuffixResult? thinkingSuffix)
    {
        JsonObject config = [];
        CopyNumber(root, config, "temperature", "temperature");
        CopyNumber(root, config, "top_p", "topP");
        CopyNumber(root, config, "top_k", "topK");
        CopyNumber(root, config, "max_tokens", "maxOutputTokens");
        CopyNumber(root, config, "max_completion_tokens", "maxOutputTokens");
        CopyNumber(root, config, "n", "candidateCount");

        if (root["response_format"] is JsonObject responseFormat &&
            string.Equals(ReadString(responseFormat, "type"), "json_object", StringComparison.OrdinalIgnoreCase))
        {
            config["responseMimeType"] = "application/json";
        }

        if (BuildThinkingConfig(thinkingSuffix) is { Count: > 0 } thinkingConfig)
        {
            config["thinkingConfig"] = thinkingConfig;
        }

        return config;
    }

    private static JsonObject BuildThinkingConfig(TransparentProxyThinkingSuffixResult? thinkingSuffix)
    {
        if (thinkingSuffix is not { } suffix)
        {
            return [];
        }

        JsonObject thinking = [];
        if (!string.IsNullOrWhiteSpace(suffix.Level) &&
            suffix.Budget is null)
        {
            thinking["thinkingLevel"] = suffix.Level;
        }
        else if (suffix.Budget.HasValue)
        {
            thinking["thinkingBudget"] = suffix.Budget.Value;
        }

        if (thinking.Count > 0)
        {
            thinking["includeThoughts"] = suffix.IncludeThoughts;
        }

        return thinking;
    }

    private static JsonArray BuildGeminiTools(JsonNode? toolsNode, GeminiToolNameMap toolNameMap)
    {
        JsonArray declarations = [];
        JsonArray geminiTools = [];
        if (toolsNode is JsonArray tools)
        {
            foreach (var item in tools)
            {
                if (item is not JsonObject tool)
                {
                    continue;
                }

                if (string.Equals(ReadString(tool, "type"), "function", StringComparison.OrdinalIgnoreCase) &&
                    tool["function"] is JsonObject function)
                {
                    var name = ReadString(function, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    JsonObject declaration = new()
                    {
                        ["name"] = toolNameMap.Resolve(name)
                    };
                    var description = ReadString(function, "description");
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        declaration["description"] = description;
                    }

                    declaration["parametersJsonSchema"] = function["parameters"]?.DeepClone() ?? new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject()
                    };
                    declarations.Add(declaration);
                    continue;
                }

                AppendGeminiBuiltInTool(tool, geminiTools);
            }
        }

        if (declarations.Count > 0)
        {
            geminiTools.Insert(0, new JsonObject { ["functionDeclarations"] = declarations });
        }

        return geminiTools;
    }

    private static void AppendGeminiBuiltInTool(JsonObject tool, JsonArray geminiTools)
    {
        if (ReadBuiltInToolNode(tool, "google_search", "googleSearch") is { } googleSearch)
        {
            geminiTools.Add(new JsonObject { ["googleSearch"] = googleSearch });
            return;
        }

        if (ReadBuiltInToolNode(tool, "code_execution", "codeExecution") is { } codeExecution)
        {
            geminiTools.Add(new JsonObject { ["codeExecution"] = codeExecution });
            return;
        }

        if (ReadBuiltInToolNode(tool, "url_context", "urlContext") is { } urlContext)
        {
            geminiTools.Add(new JsonObject { ["urlContext"] = urlContext });
            return;
        }

        var type = ReadString(tool, "type");
        if (IsOpenAiWebSearchToolType(type))
        {
            geminiTools.Add(new JsonObject { ["googleSearch"] = new JsonObject() });
        }
    }

    private static JsonNode? ReadBuiltInToolNode(JsonObject tool, string snakeName, string camelName)
    {
        var node = tool[snakeName] ?? tool[camelName];
        return node switch
        {
            JsonObject obj => obj.DeepClone(),
            JsonValue => new JsonObject(),
            _ => null
        };
    }

    private static bool IsOpenAiWebSearchToolType(string? type)
        => !string.IsNullOrWhiteSpace(type) &&
           (type.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("web_search_preview", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("web_search_preview_", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("web_search_", StringComparison.OrdinalIgnoreCase));

    private static JsonObject BuildGeminiToolConfig(JsonNode? toolChoiceNode, GeminiToolNameMap toolNameMap)
    {
        if (toolChoiceNode is null)
        {
            return [];
        }

        JsonObject functionCallingConfig = [];
        if (toolChoiceNode is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var mode = MapGeminiToolChoiceMode(text);
            if (string.IsNullOrWhiteSpace(mode))
            {
                return [];
            }

            functionCallingConfig["mode"] = mode;
        }
        else if (toolChoiceNode is JsonObject obj)
        {
            var type = ReadString(obj, "type");
            var mode = MapGeminiToolChoiceMode(type);
            if (string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
            {
                mode = "ANY";
            }

            if (string.IsNullOrWhiteSpace(mode))
            {
                return [];
            }

            functionCallingConfig["mode"] = mode;
            if (string.Equals(mode, "ANY", StringComparison.OrdinalIgnoreCase) &&
                TryReadToolChoiceName(obj) is { Length: > 0 } name)
            {
                functionCallingConfig["allowedFunctionNames"] = new JsonArray(toolNameMap.Resolve(name));
            }
        }
        else
        {
            return [];
        }

        return new JsonObject
        {
            ["functionCallingConfig"] = functionCallingConfig
        };
    }

    private static string MapGeminiToolChoiceMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "auto" => "AUTO",
            "none" => "NONE",
            "required" => "ANY",
            "any" => "ANY",
            "tool" => "ANY",
            _ => string.Empty
        };

    private static string TryReadToolChoiceName(JsonObject toolChoice)
    {
        if (toolChoice["function"] is JsonObject function)
        {
            var functionName = ReadString(function, "name");
            if (!string.IsNullOrWhiteSpace(functionName))
            {
                return functionName;
            }
        }

        return ReadString(toolChoice, "name");
    }

    private static void AppendContentParts(JsonNode? content, JsonArray parts)
    {
        if (content is null)
        {
            return;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            AddTextPart(parts, text);
            return;
        }

        if (content is JsonObject obj)
        {
            AddTextPart(parts, ReadString(obj, "text"));
            return;
        }

        if (content is not JsonArray array)
        {
            return;
        }

        foreach (var item in array)
        {
            if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText))
            {
                AddTextPart(parts, itemText);
                continue;
            }

            if (item is not JsonObject part)
            {
                continue;
            }

            var type = ReadString(part, "type");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                AddTextPart(parts, ReadString(part, "text"));
            }
            else if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase) &&
                     TryParseDataUrl(ReadString(part["image_url"] as JsonObject ?? [], "url"), out var mimeType, out var base64))
            {
                AddInlineDataPart(parts, mimeType, base64);
            }
            else if (string.Equals(type, "input_audio", StringComparison.OrdinalIgnoreCase))
            {
                AppendAudioPart(part, parts);
            }
            else if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                AppendFilePart(part, parts);
            }
        }
    }

    private static void AppendAudioPart(JsonObject part, JsonArray parts)
    {
        var audio = part["input_audio"] as JsonObject;
        var data = ReadString(audio ?? part, "data");
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        var format = ReadString(audio ?? part, "format");
        AddInlineDataPart(parts, ResolveAudioMimeType(format), data);
    }

    private static void AppendFilePart(JsonObject part, JsonArray parts)
    {
        if (part["file"] is not JsonObject file)
        {
            return;
        }

        var fileData = ReadString(file, "file_data");
        if (string.IsNullOrWhiteSpace(fileData))
        {
            fileData = ReadString(file, "data");
        }

        if (string.IsNullOrWhiteSpace(fileData))
        {
            return;
        }

        if (TryParseDataUrl(fileData, out var mimeType, out var base64))
        {
            AddInlineDataPart(parts, mimeType, base64);
            return;
        }

        AddInlineDataPart(parts, ResolveMimeTypeFromFileName(ReadString(file, "filename")), fileData);
    }

    private static JsonObject BuildFunctionResponsePart(string functionName, JsonNode? content)
    {
        var inlineParts = new JsonArray();
        var result = BuildFunctionResponseResult(content, inlineParts);
        var functionResponse = new JsonObject
        {
            ["name"] = functionName,
            ["response"] = new JsonObject
            {
                ["result"] = result
            }
        };

        if (inlineParts.Count > 0)
        {
            functionResponse["parts"] = inlineParts;
        }

        return new JsonObject
        {
            ["functionResponse"] = functionResponse
        };
    }

    private static JsonNode BuildFunctionResponseResult(JsonNode? content, JsonArray inlineParts)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text ?? string.Empty;
        }

        if (content is JsonObject obj)
        {
            if (TryAppendInlineDataContentPart(obj, inlineParts))
            {
                return ReadString(obj, "text");
            }

            var objectText = ReadString(obj, "text");
            return string.IsNullOrWhiteSpace(objectText)
                ? obj.DeepClone()
                : objectText;
        }

        if (content is not JsonArray array)
        {
            return content.DeepClone();
        }

        JsonArray nonMediaParts = [];
        foreach (var item in array)
        {
            if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText))
            {
                nonMediaParts.Add(itemText ?? string.Empty);
                continue;
            }

            if (item is not JsonObject part)
            {
                nonMediaParts.Add(item?.DeepClone());
                continue;
            }

            if (TryAppendInlineDataContentPart(part, inlineParts))
            {
                continue;
            }

            if (string.Equals(ReadString(part, "type"), "text", StringComparison.OrdinalIgnoreCase))
            {
                nonMediaParts.Add(ReadString(part, "text"));
                continue;
            }

            nonMediaParts.Add(part.DeepClone());
        }

        return nonMediaParts.Count switch
        {
            0 => string.Empty,
            1 => nonMediaParts[0]?.DeepClone() ?? string.Empty,
            _ => nonMediaParts
        };
    }

    private static bool TryAppendInlineDataContentPart(JsonObject part, JsonArray inlineParts)
    {
        var before = inlineParts.Count;
        var type = ReadString(part, "type");
        if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase) &&
            TryParseDataUrl(ReadString(part["image_url"] as JsonObject ?? [], "url"), out var mimeType, out var base64))
        {
            AddInlineDataPart(inlineParts, mimeType, base64);
        }
        else if (string.Equals(type, "input_audio", StringComparison.OrdinalIgnoreCase))
        {
            AppendAudioPart(part, inlineParts);
        }
        else if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
        {
            AppendFilePart(part, inlineParts);
        }

        return inlineParts.Count > before;
    }

    private static void AddInlineDataPart(JsonArray parts, string mimeType, string base64)
    {
        if (string.IsNullOrWhiteSpace(mimeType) || string.IsNullOrWhiteSpace(base64))
        {
            return;
        }

        parts.Add(new JsonObject
        {
            ["inlineData"] = new JsonObject
            {
                ["mimeType"] = mimeType,
                ["data"] = base64
            }
        });
    }

    private static string ResolveAudioMimeType(string format)
        => format.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "webm" => "audio/webm",
            "pcm16" => "audio/pcm",
            "g711_ulaw" => "audio/basic",
            "g711_alaw" => "audio/basic",
            { Length: > 0 } value when value.Contains('/') => value,
            { Length: > 0 } value => "audio/" + value,
            _ => "audio/wav"
        };

    private static string ResolveMimeTypeFromFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "txt" => "text/plain",
            "md" => "text/markdown",
            "json" => "application/json",
            "csv" => "text/csv",
            "html" or "htm" => "text/html",
            "xml" => "application/xml",
            "pdf" => "application/pdf",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            "mp4" => "video/mp4",
            "mov" => "video/quicktime",
            "webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }

    private static void AppendTextParts(JsonNode? content, JsonArray parts)
    {
        JsonArray localParts = [];
        AppendContentParts(content, localParts);
        foreach (var part in localParts)
        {
            if (part is JsonObject obj && obj["text"] is not null)
            {
                parts.Add(obj.DeepClone());
            }
        }
    }

    private static void AppendFunctionCalls(
        JsonNode? toolCallsNode,
        JsonArray parts,
        GeminiToolNameMap toolNameMap,
        Dictionary<string, string> toolCallNamesById)
    {
        if (toolCallsNode is not JsonArray toolCalls)
        {
            return;
        }

        foreach (var item in toolCalls)
        {
            if (item is not JsonObject call ||
                !string.Equals(ReadString(call, "type"), "function", StringComparison.OrdinalIgnoreCase) ||
                call["function"] is not JsonObject function)
            {
                continue;
            }

            var name = ReadString(function, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            parts.Add(new JsonObject
            {
                ["functionCall"] = new JsonObject
                {
                    ["name"] = toolNameMap.Resolve(name),
                    ["args"] = TryParseObject(ReadString(function, "arguments")) ?? new JsonObject()
                }
            });
            var callId = ReadString(call, "id");
            if (!string.IsNullOrWhiteSpace(callId))
            {
                toolCallNamesById[callId] = name;
            }
        }
    }

    private static string ResolveToolResponseName(
        JsonObject message,
        IReadOnlyDictionary<string, string> toolCallNamesById,
        GeminiToolNameMap toolNameMap)
    {
        var name = ReadString(message, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            var toolCallId = ReadString(message, "tool_call_id");
            if (!string.IsNullOrWhiteSpace(toolCallId) &&
                toolCallNamesById.TryGetValue(toolCallId, out var mappedName))
            {
                name = mappedName;
            }
        }

        return string.IsNullOrWhiteSpace(name)
            ? "tool"
            : toolNameMap.Resolve(name);
    }

    private static void AddTextPart(JsonArray parts, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(new JsonObject { ["text"] = text });
        }
    }

    private static void CopyNumber(JsonObject source, JsonObject target, string sourceName, string targetName)
    {
        if (source[sourceName] is not JsonValue value)
        {
            return;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            target[targetName] = intValue;
            return;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            target[targetName] = doubleValue;
        }
    }

    private static JsonObject? TryParseObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch
        {
            return new JsonObject { ["value"] = raw };
        }
    }

    private static string ReadContentAsString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text ?? string.Empty;
        }

        return node.ToJsonString(CompactJsonOptions);
    }

    private static string ResolveModel(TransparentProxyRoute route, byte[] requestBody, string upstreamModel)
    {
        if (!string.IsNullOrWhiteSpace(upstreamModel))
        {
            return upstreamModel.Trim();
        }

        try
        {
            var root = JsonNode.Parse(requestBody) as JsonObject;
            var model = root is null ? string.Empty : ReadString(root, "model");
            if (!string.IsNullOrWhiteSpace(model))
            {
                return model.Trim();
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(route.Model) ? "gemini-2.5-pro" : route.Model.Trim();
    }

    private static string ReadString(JsonObject root, string propertyName)
        => root.TryGetPropertyValue(propertyName, out var node) &&
           node is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text ?? string.Empty
            : string.Empty;

    private static bool TryParseDataUrl(string? value, out string mimeType, out string base64)
    {
        mimeType = string.Empty;
        base64 = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = value.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        var metadata = value[5..comma];
        var semicolon = metadata.IndexOf(';');
        mimeType = semicolon >= 0 ? metadata[..semicolon] : metadata;
        base64 = value[(comma + 1)..];
        return !string.IsNullOrWhiteSpace(mimeType) && !string.IsNullOrWhiteSpace(base64);
    }

    private static string SanitizeGeminiFunctionName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':' ? ch : '_');
        }

        var text = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "tool";
        }

        if (text[0] is not '_' && !char.IsLetter(text[0]))
        {
            text = "_" + text;
        }

        return text.Length > 64 ? text[..64] : text;
    }

    private sealed class GeminiToolNameMap
    {
        private readonly Dictionary<string, string> _originalToSanitized = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sanitizedToOriginal = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> SanitizedToOriginal => _aliases;

        public string Resolve(string originalName)
        {
            var trimmed = originalName.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return "tool";
            }

            if (_originalToSanitized.TryGetValue(trimmed, out var existing))
            {
                return existing;
            }

            var sanitized = EnsureUniqueSanitizedName(SanitizeGeminiFunctionName(trimmed));
            _originalToSanitized[trimmed] = sanitized;
            _sanitizedToOriginal[sanitized] = trimmed;
            if (!string.Equals(sanitized, trimmed, StringComparison.Ordinal))
            {
                _aliases[sanitized] = trimmed;
            }

            return sanitized;
        }

        private string EnsureUniqueSanitizedName(string candidate)
        {
            if (!_sanitizedToOriginal.ContainsKey(candidate))
            {
                return candidate;
            }

            for (var index = 2; index < 10000; index++)
            {
                var suffix = "_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var prefixLength = Math.Max(1, 64 - suffix.Length);
                var unique = candidate.Length > prefixLength
                    ? candidate[..prefixLength] + suffix
                    : candidate + suffix;
                if (!_sanitizedToOriginal.ContainsKey(unique))
                {
                    return unique;
                }
            }

            return "tool_" + Guid.NewGuid().ToString("N")[..16];
        }
    }
}
