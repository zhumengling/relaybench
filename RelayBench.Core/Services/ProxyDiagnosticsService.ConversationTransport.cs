using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private const int AnthropicConversationMinMaxTokens = 512;

    private sealed record ConversationProbeTransport(
        string WireApi,
        string Path,
        Action<HttpRequestMessage>? RequestConfigurer,
        Func<string, string?> JsonPreviewParser,
        Func<string, string?> StreamContentParser,
        Func<string, bool>? StreamDoneDetector);

    private static async Task<ConversationProbeTransport> ResolveConversationProbeTransportAsync(
        HttpClient client,
        Uri baseUri,
        string model,
        ProxyDiagnosticsResult? baselineResult,
        CancellationToken cancellationToken)
    {
        var baselineWireApi = ResolveConversationWireApiFromBaseline(baselineResult);
        if (!string.IsNullOrWhiteSpace(baselineWireApi))
        {
            return CreateConversationProbeTransport(client, baseUri, baselineWireApi);
        }

        var protocolOutcome = await ProbeEndpointWireApiAsync(
            client,
            baseUri,
            model,
            [model],
            cancellationToken);

        return CreateConversationProbeTransport(
            client,
            baseUri,
            protocolOutcome?.PreferredWireApi ?? ProxyWireApiProbeService.ChatCompletionsWireApi);
    }

    private static string? ResolveConversationWireApiFromBaseline(ProxyDiagnosticsResult? baselineResult)
    {
        if (baselineResult is null)
        {
            return null;
        }

        var scenarioResults = baselineResult.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>();
        var anthropicSupported = scenarioResults
            .FirstOrDefault(static result => result.Scenario == ProxyProbeScenarioKind.AnthropicMessages)?
            .Success == true;
        var responsesSupported = scenarioResults
            .FirstOrDefault(static result => result.Scenario == ProxyProbeScenarioKind.Responses)?
            .Success == true;
        var chatSupported = !anthropicSupported &&
                            scenarioResults
                                .FirstOrDefault(static result => result.Scenario == ProxyProbeScenarioKind.ChatCompletions)?
                                .Success == true;

        return ProxyWireApiProbeService.ResolvePreferredWireApi(chatSupported, responsesSupported, anthropicSupported);
    }

    private static ConversationProbeTransport CreateConversationProbeTransport(
        HttpClient client,
        Uri baseUri,
        string? wireApi)
    {
        return NormalizeWireApiName(wireApi) switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => new ConversationProbeTransport(
                ProxyWireApiProbeService.ResponsesWireApi,
                BuildApiPath(baseUri, "responses"),
                null,
                ParseResponsesPreview,
                TryParseResponsesStreamContent,
                IsResponsesStreamDone),
            ProxyWireApiProbeService.AnthropicMessagesWireApi => new ConversationProbeTransport(
                ProxyWireApiProbeService.AnthropicMessagesWireApi,
                BuildApiPath(baseUri, "messages"),
                request => ConfigureAnthropicMessagesRequest(request, client),
                ParseAnthropicMessagesPreview,
                TryParseAnthropicStreamContent,
                IsAnthropicStreamDone),
            _ => new ConversationProbeTransport(
                ProxyWireApiProbeService.ChatCompletionsWireApi,
                BuildApiPath(baseUri, "chat/completions"),
                null,
                ParseChatPreview,
                TryParseChatStreamContent,
                null)
        };
    }

    private static string NormalizeWireApiName(string? wireApi)
        => ProxyWireApiProbeService.NormalizeWireApiOrChat(wireApi);

    private static Task<JsonProbeOutcome> ProbeJsonConversationScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string chatPayload,
        ProxyProbeScenarioKind scenario,
        string displayName,
        CancellationToken cancellationToken,
        Func<string, string?>? previewParser = null)
        => ProbeJsonScenarioAsync(
            client,
            transport.Path,
            BuildConversationWirePayload(transport.WireApi, chatPayload),
            scenario,
            displayName,
            previewParser ?? transport.JsonPreviewParser,
            cancellationToken,
            transport.RequestConfigurer);

    private static Task<ProxyProbeScenarioResult> ProbeStreamingConversationScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string chatPayload,
        ProxyProbeScenarioKind scenario,
        string displayName,
        Func<string?, bool>? semanticMatcher,
        CancellationToken cancellationToken)
        => ProbeStreamingScenarioAsync(
            client,
            transport.Path,
            BuildConversationWirePayload(transport.WireApi, chatPayload),
            scenario,
            displayName,
            transport.StreamContentParser,
            semanticMatcher,
            cancellationToken,
            transport.RequestConfigurer,
            transport.StreamDoneDetector);

    private static string BuildConversationWirePayload(string wireApi, string chatPayload)
    {
        return NormalizeWireApiName(wireApi) switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => ConvertChatPayloadToResponsesPayload(chatPayload),
            ProxyWireApiProbeService.AnthropicMessagesWireApi => ConvertChatPayloadToAnthropicMessagesPayload(chatPayload),
            _ => chatPayload
        };
    }

    private static string ConvertChatPayloadToResponsesPayload(string chatPayload)
    {
        var root = JsonNode.Parse(chatPayload)?.AsObject() ??
                   throw new InvalidOperationException("Chat payload JSON is not an object.");
        var output = new JsonObject
        {
            ["model"] = CloneNode(root["model"]),
            ["max_output_tokens"] = CloneNode(root["max_output_tokens"]) ??
                                    CloneNode(root["max_tokens"]) ??
                                    GlobalResponsesProbeMaxOutputTokens
        };

        if (CloneNode(root["temperature"]) is { } temperature)
        {
            output["temperature"] = temperature;
        }

        if (TryReadBool(root, "stream", out var stream))
        {
            output["stream"] = stream;
        }

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

    private static string ConvertChatPayloadToAnthropicMessagesPayload(string chatPayload)
    {
        var root = JsonNode.Parse(chatPayload)?.AsObject() ??
                   throw new InvalidOperationException("Chat payload JSON is not an object.");
        var output = new JsonObject
        {
            ["model"] = CloneNode(root["model"]),
            ["max_tokens"] = ResolveAnthropicConversationMaxTokens(root)
        };

        if (CloneNode(root["temperature"]) is { } temperature)
        {
            output["temperature"] = temperature;
        }

        output["thinking"] = CloneNode(root["thinking"]) ?? new JsonObject
        {
            ["type"] = "disabled"
        };

        if (TryReadBool(root, "stream", out var stream))
        {
            output["stream"] = stream;
        }

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

    private static int ResolveAnthropicConversationMaxTokens(JsonObject root)
    {
        var requested =
            TryReadPositiveJsonInt(root["max_tokens"]) ??
            TryReadPositiveJsonInt(root["max_output_tokens"]) ??
            GlobalChatProbeMaxTokens;

        return Math.Max(requested, AnthropicConversationMinMaxTokens);
    }

    private static JsonArray BuildResponsesInput(JsonArray? messages)
    {
        JsonArray input = new();
        if (messages is null)
        {
            return input;
        }

        var nonSystemMessages = messages
            .OfType<JsonObject>()
            .Where(static message => !string.Equals(TryReadString(message, "role"), "system", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (nonSystemMessages.Length == 1 &&
            string.Equals(TryReadString(nonSystemMessages[0], "role"), "user", StringComparison.OrdinalIgnoreCase) &&
            nonSystemMessages[0]["content"] is JsonValue contentValue &&
            contentValue.TryGetValue<string>(out var text))
        {
            input.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = text
                    }
                }
            });
            return input;
        }

        foreach (var message in nonSystemMessages)
        {
            var role = NormalizeResponsesRole(TryReadString(message, "role"));
            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
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
                ["role"] = role,
                ["content"] = ConvertChatContentToResponsesContent(message["content"], role)
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
        var metadataParts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (metadataParts.Length > 0 && metadataParts[0].Contains('/', StringComparison.Ordinal))
        {
            mediaType = metadataParts[0];
        }

        data = url[(commaIndex + 1)..];
        return !string.IsNullOrWhiteSpace(data);
    }

    private static string? TryReadJsonStringProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var element) &&
                   element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryParseResponsesStreamContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String)
        {
            var type = typeElement.GetString();
            if (string.Equals(type, "response.output_text.delta", StringComparison.OrdinalIgnoreCase) &&
                TryGetNonEmptyString(root, "delta") is { } delta)
            {
                return delta;
            }

            if ((string.Equals(type, "response.content_part.added", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "response.output_item.done", StringComparison.OrdinalIgnoreCase)) &&
                TryExtractResponsesText(root) is { } text)
            {
                return text;
            }
        }

        if (TryGetNonEmptyString(root, "delta") is { } fallbackDelta)
        {
            return fallbackDelta;
        }

        return TryExtractResponsesText(root);
    }

    private static bool IsResponsesStreamDone(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                var type = typeElement.GetString();
                return string.Equals(type, "response.completed", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(type, "response.done", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(type, "done", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
        }

        return false;
    }
}
