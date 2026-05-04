using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyProtocolTranslatorService
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly TransparentProxyWireProtocolRegistry _wireProtocolRegistry;
    private readonly TransparentProxyPromptSessionCacheService _promptSessionCache;
    private readonly object _modelPoolSyncRoot = new();
    private readonly Dictionary<string, int> _modelPoolOffsets = new(StringComparer.OrdinalIgnoreCase);

    public TransparentProxyProtocolTranslatorService(
        TransparentProxyWireProtocolRegistry? wireProtocolRegistry = null,
        TransparentProxyPromptSessionCacheService? promptSessionCache = null)
    {
        _wireProtocolRegistry = wireProtocolRegistry ?? new TransparentProxyWireProtocolRegistry();
        _promptSessionCache = promptSessionCache ?? new TransparentProxyPromptSessionCacheService();
    }

    public IReadOnlyList<TransparentProxyPreparedRequest> BuildPreparedUpstreamRequests(
        string method,
        string pathAndQuery,
        byte[] requestBody,
        TransparentProxyRoute route,
        bool streamRequested)
    {
        var modelBodies = BuildRouteModelSelectedBodies(requestBody, route);
        List<TransparentProxyPreparedRequest> allAttempts = [];
        foreach (var modelBody in modelBodies)
        {
            var directBody = modelBody.Body;
            var clientModel = modelBody.ClientModel;
            var promptSession = _promptSessionCache.Resolve(route, directBody);
            var upstreamModel = TryReadRequestModel(directBody) ?? modelBody.UpstreamModel;
            var isToolExchange = IsToolExchangeRequest(directBody);
            var preferJsonStreamExtraction = streamRequested && PrefersJsonStreamExtraction(directBody);
            if (IsOpenAiChatCompletionsRequest(method, pathAndQuery))
            {
                var requestText = Encoding.UTF8.GetString(directBody);
                var query = ExtractQuery(pathAndQuery);
                List<TransparentProxyPreparedRequest> attempts = [];
                foreach (var wireApi in _wireProtocolRegistry.BuildWireApiAttempts(route))
                {
                    try
                    {
                        var prepared = AdvancedWireRequestBuilder.PreparePostJson(
                            ExtractRelativePath(pathAndQuery),
                            requestText,
                            wireApi,
                            streamRequested);
                        var body = Encoding.UTF8.GetBytes(prepared.RequestBody);
                        var headers = MergeHeaders(route.Headers, prepared.ExtraHeaders);
                        body = ApplyPayloadRules(
                            body,
                            route,
                            prepared.WireApi,
                            clientModel,
                            upstreamModel,
                            pathAndQuery);
                        body = ApplyPromptCacheOptimizations(
                            body,
                            promptSession,
                            prepared.WireApi,
                            headers,
                            out var optimizedModel);
                        var preparedModel = string.IsNullOrWhiteSpace(optimizedModel)
                            ? TryReadRequestModel(body) ?? upstreamModel
                            : optimizedModel;
                        attempts.Add(new TransparentProxyPreparedRequest(
                            prepared.WireApi,
                            BuildUpstreamUrl(route.BaseUrl, "/v1/" + prepared.RelativePath + query),
                            body,
                            headers,
                            !string.Equals(prepared.WireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal),
                            string.IsNullOrWhiteSpace(clientModel) ? preparedModel : clientModel,
                            preparedModel,
                            isToolExchange,
                            preferJsonStreamExtraction));
                    }
                    catch
                    {
                        if (string.Equals(wireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal))
                        {
                            attempts.Add(BuildDirectPreparedRequest(pathAndQuery, directBody, route, promptSession, clientModel, upstreamModel, isToolExchange, preferJsonStreamExtraction));
                        }
                    }
                }

                if (attempts.Count > 0)
                {
                    allAttempts.AddRange(attempts);
                    continue;
                }
            }

            allAttempts.Add(BuildDirectPreparedRequest(pathAndQuery, directBody, route, promptSession, clientModel, upstreamModel, isToolExchange, preferJsonStreamExtraction));
        }

        return allAttempts;
    }

    public TransparentProxyPromptSessionCacheStats PromptSessionCacheStats => _promptSessionCache.Stats;

    public void ClearPromptSessionCache() => _promptSessionCache.Clear();

    public TransparentProxyNormalizedChatResponse? TryBuildNormalizedChatJson(
        byte[] upstreamBytes,
        string responseModel,
        string wireApi)
    {
        var upstreamText = Encoding.UTF8.GetString(upstreamBytes);
        var assistantText = ModelResponseTextExtractor.TryExtractAssistantText(upstreamText);
        var model = ResolveResponseModel(upstreamText, responseModel);
        var usage = TryExtractUsageNode(upstreamText);
        if (assistantText is not null)
        {
            return new TransparentProxyNormalizedChatResponse(
                BuildOpenAiChatCompletionBytes(
                    assistantText,
                    model,
                    wireApi,
                    usage),
                assistantText);
        }

        if (TryExtractToolCalls(upstreamText, out var toolCalls))
        {
            return new TransparentProxyNormalizedChatResponse(
                BuildOpenAiChatToolCallBytes(
                    toolCalls,
                    model,
                    wireApi,
                    usage),
                string.Empty);
        }

        return null;
    }

    private static bool IsToolExchangeRequest(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("tools", out var tools) &&
                tools.ValueKind == JsonValueKind.Array &&
                tools.GetArrayLength() > 0)
            {
                return true;
            }

            if (!root.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("role", out var role) &&
                    role.ValueKind == JsonValueKind.String &&
                    string.Equals(role.GetString(), "tool", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls) &&
                    toolCalls.ValueKind == JsonValueKind.Array &&
                    toolCalls.GetArrayLength() > 0)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool PrefersJsonStreamExtraction(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("response_format", out var responseFormat) &&
                responseFormat.ValueKind == JsonValueKind.Object &&
                responseFormat.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString()?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            if (!root.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var message in messages.EnumerateArray())
            {
                var content = ReadMessageContentText(message);
                if (content.Contains("raw JSON", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("JSON object", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("Return exactly this JSON", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("No markdown", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string ReadMessageContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                builder.Append(item.GetString()).Append('\n');
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString()).Append('\n');
            }
        }

        return builder.ToString();
    }

    public string BuildOpenAiChatCompletionTerminalChunk(
        string model,
        string wireApi,
        string streamId,
        string accumulatedText)
        => JsonSerializer.Serialize(new
        {
            id = streamId,
            @object = "chat.completion.chunk",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = 0,
                completion_tokens = Math.Max(0, TokenCountEstimator.EstimateOutputTokens(accumulatedText)),
                total_tokens = Math.Max(0, TokenCountEstimator.EstimateOutputTokens(accumulatedText))
            },
            relaybench = new
            {
                upstream_wire_api = wireApi
            }
        }, CompactJsonOptions);

    public string BuildOpenAiChatCompletionChunk(
        string delta,
        string model,
        string wireApi,
        string streamId)
        => JsonSerializer.Serialize(new
        {
            id = streamId,
            @object = "chat.completion.chunk",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        content = delta
                    },
                    finish_reason = (string?)null
                }
            },
            relaybench = new
            {
                upstream_wire_api = wireApi
            }
        }, CompactJsonOptions);

    public static string DisplayWireApi(string wireApi)
        => wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic Messages",
            _ => "OpenAI Chat"
        };

    private static TransparentProxyPreparedRequest BuildDirectPreparedRequest(
        string pathAndQuery,
        byte[] requestBody,
        TransparentProxyRoute route,
        TransparentProxyPromptSessionMaterial promptSession,
        string clientModel,
        string upstreamModel,
        bool isToolExchange,
        bool preferJsonStreamExtraction)
    {
        var wireApi = InferWireApiFromPath(pathAndQuery);
        var headers = MergeHeaders(route.Headers, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        requestBody = ApplyPayloadRules(
            requestBody,
            route,
            wireApi,
            clientModel,
            upstreamModel,
            pathAndQuery);
        var body = ApplyPromptCacheOptimizations(
            requestBody,
            promptSession,
            wireApi,
            headers,
            out var optimizedModel);
        var preparedModel = string.IsNullOrWhiteSpace(optimizedModel)
            ? TryReadRequestModel(body) ?? upstreamModel
            : optimizedModel;
        return new TransparentProxyPreparedRequest(
            wireApi,
            BuildUpstreamUrl(route.BaseUrl, pathAndQuery),
            body,
            headers,
            false,
            string.IsNullOrWhiteSpace(clientModel) ? preparedModel : clientModel,
            preparedModel,
            isToolExchange,
            preferJsonStreamExtraction);
    }

    private static Dictionary<string, string> MergeHeaders(
        IReadOnlyDictionary<string, string> routeHeaders,
        IReadOnlyDictionary<string, string> requestHeaders)
    {
        Dictionary<string, string> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (var header in routeHeaders)
        {
            merged[header.Key] = header.Value;
        }

        foreach (var header in requestHeaders)
        {
            merged[header.Key] = header.Value;
        }

        return merged;
    }

    private static byte[] ApplyPromptCacheOptimizations(
        byte[] body,
        TransparentProxyPromptSessionMaterial promptSession,
        string wireApi,
        IDictionary<string, string> extraHeaders,
        out string model)
    {
        model = TryReadRequestModel(body) ?? string.Empty;
        if (body.Length == 0)
        {
            return body;
        }

        if (string.Equals(wireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal))
        {
            return EnsureResponsesPromptCacheKey(body, promptSession, extraHeaders, ref model);
        }

        if (string.Equals(wireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal))
        {
            return EnsureAnthropicCacheControl(body);
        }

        return body;
    }

    private static byte[] EnsureResponsesPromptCacheKey(
        byte[] body,
        TransparentProxyPromptSessionMaterial promptSession,
        IDictionary<string, string> extraHeaders,
        ref string model)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is not JsonObject obj)
            {
                return body;
            }

            if (string.IsNullOrWhiteSpace(model) &&
                obj.TryGetPropertyValue("model", out var modelNode) &&
                modelNode is JsonValue)
            {
                model = modelNode.GetValue<string>()?.Trim() ?? string.Empty;
            }

            string promptCacheKey;
            if (obj.TryGetPropertyValue("prompt_cache_key", out var existingKeyNode) &&
                existingKeyNode is JsonValue existingKeyValue)
            {
                promptCacheKey = existingKeyValue.GetValue<string>()?.Trim() ?? string.Empty;
            }
            else
            {
                promptCacheKey = promptSession.PromptCacheKey;
                obj["prompt_cache_key"] = promptCacheKey;
            }

            if (!string.IsNullOrWhiteSpace(promptCacheKey))
            {
                extraHeaders["Session_id"] = string.IsNullOrWhiteSpace(promptSession.SessionId)
                    ? promptCacheKey
                    : promptSession.SessionId;
            }

            return JsonSerializer.SerializeToUtf8Bytes(obj, CompactJsonOptions);
        }
        catch
        {
            return body;
        }
    }

    private static byte[] EnsureAnthropicCacheControl(byte[] body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is not JsonObject obj)
            {
                return body;
            }

            InjectToolsCacheControl(obj);
            InjectSystemCacheControl(obj);
            InjectMessagesCacheControl(obj);
            NormalizeAnthropicCacheControlTtl(obj);
            EnforceAnthropicCacheControlLimit(obj, 4);
            return JsonSerializer.SerializeToUtf8Bytes(obj, CompactJsonOptions);
        }
        catch
        {
            return body;
        }
    }

    private static void InjectToolsCacheControl(JsonObject root)
    {
        if (root["tools"] is not JsonArray tools || tools.Count == 0)
        {
            return;
        }

        if (tools.OfType<JsonObject>().Any(static tool => tool.ContainsKey("cache_control")))
        {
            return;
        }

        if (tools.LastOrDefault(static tool => tool is JsonObject) is JsonObject lastTool)
        {
            lastTool["cache_control"] = BuildEphemeralCacheControl();
        }
    }

    private static void InjectSystemCacheControl(JsonObject root)
    {
        var system = root["system"];
        if (system is null)
        {
            return;
        }

        if (system is JsonArray systemBlocks)
        {
            if (systemBlocks.OfType<JsonObject>().Any(static item => item.ContainsKey("cache_control")))
            {
                return;
            }

            if (systemBlocks.LastOrDefault(static item => item is JsonObject) is JsonObject lastSystemBlock)
            {
                lastSystemBlock["cache_control"] = BuildEphemeralCacheControl();
            }

            return;
        }

        if (system is JsonValue value)
        {
            var text = value.GetValue<string>();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            root["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = BuildEphemeralCacheControl()
                }
            };
        }
    }

    private static void InjectMessagesCacheControl(JsonObject root)
    {
        if (root["messages"] is not JsonArray messages)
        {
            return;
        }

        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["content"] is JsonArray content &&
                content.OfType<JsonObject>().Any(static item => item.ContainsKey("cache_control")))
            {
                return;
            }
        }

        var userMessageIndexes = messages
            .Select((node, index) => (Node: node as JsonObject, Index: index))
            .Where(static item =>
                item.Node is not null &&
                item.Node.TryGetPropertyValue("role", out var roleNode) &&
                roleNode is JsonValue roleValue &&
                string.Equals(roleValue.GetValue<string>(), "user", StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.Index)
            .ToArray();
        if (userMessageIndexes.Length < 2)
        {
            return;
        }

        var secondToLastUserIndex = userMessageIndexes[^2];
        if (messages[secondToLastUserIndex] is not JsonObject userMessage)
        {
            return;
        }

        var contentNode = userMessage["content"];
        if (contentNode is JsonArray contentBlocks)
        {
            if (contentBlocks.LastOrDefault(static item => item is JsonObject) is JsonObject lastContentBlock)
            {
                lastContentBlock["cache_control"] = BuildEphemeralCacheControl();
            }

            return;
        }

        if (contentNode is JsonValue contentValue)
        {
            var text = contentValue.GetValue<string>();
            userMessage["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = BuildEphemeralCacheControl()
                }
            };
        }
    }

    private static JsonObject BuildEphemeralCacheControl()
        => new()
        {
            ["type"] = "ephemeral"
        };

    private static void NormalizeAnthropicCacheControlTtl(JsonObject root)
    {
        var seenDefaultTtl = false;
        foreach (var cacheControlOwner in EnumerateAnthropicCacheControlOwners(root))
        {
            if (cacheControlOwner["cache_control"] is not JsonObject cacheControl)
            {
                seenDefaultTtl = true;
                continue;
            }

            var ttl = cacheControl.TryGetPropertyValue("ttl", out var ttlNode) &&
                      ttlNode is JsonValue ttlValue
                ? ttlValue.GetValue<string>()
                : string.Empty;
            if (string.Equals(ttl, "1h", StringComparison.Ordinal))
            {
                if (seenDefaultTtl)
                {
                    cacheControl.Remove("ttl");
                }

                continue;
            }

            seenDefaultTtl = true;
        }
    }

    private static void EnforceAnthropicCacheControlLimit(JsonObject root, int maxBlocks)
    {
        if (maxBlocks <= 0)
        {
            return;
        }

        while (CountAnthropicCacheControls(root) > maxBlocks)
        {
            var removed = false;
            foreach (var owner in EnumerateAnthropicCacheControlRemovalCandidates(root))
            {
                if (!owner.ContainsKey("cache_control"))
                {
                    continue;
                }

                owner.Remove("cache_control");
                removed = true;
                break;
            }

            if (!removed)
            {
                return;
            }
        }
    }

    private static int CountAnthropicCacheControls(JsonObject root)
        => EnumerateAnthropicCacheControlOwners(root).Count(static owner => owner.ContainsKey("cache_control"));

    private static IEnumerable<JsonObject> EnumerateAnthropicCacheControlOwners(JsonObject root)
    {
        if (root["tools"] is JsonArray tools)
        {
            foreach (var tool in tools.OfType<JsonObject>())
            {
                yield return tool;
            }
        }

        if (root["system"] is JsonArray system)
        {
            foreach (var item in system.OfType<JsonObject>())
            {
                yield return item;
            }
        }

        if (root["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
            {
                if (message["content"] is not JsonArray content)
                {
                    continue;
                }

                foreach (var item in content.OfType<JsonObject>())
                {
                    yield return item;
                }
            }
        }
    }

    private static IEnumerable<JsonObject> EnumerateAnthropicCacheControlRemovalCandidates(JsonObject root)
    {
        if (root["system"] is JsonArray system)
        {
            var lastSystemCacheIndex = LastCacheControlIndex(system);
            for (var index = 0; index < system.Count; index++)
            {
                if (index != lastSystemCacheIndex && system[index] is JsonObject item)
                {
                    yield return item;
                }
            }
        }

        if (root["tools"] is JsonArray tools)
        {
            var lastToolCacheIndex = LastCacheControlIndex(tools);
            for (var index = 0; index < tools.Count; index++)
            {
                if (index != lastToolCacheIndex && tools[index] is JsonObject item)
                {
                    yield return item;
                }
            }
        }

        if (root["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
            {
                if (message["content"] is not JsonArray content)
                {
                    continue;
                }

                foreach (var item in content.OfType<JsonObject>())
                {
                    yield return item;
                }
            }
        }

        if (root["system"] is JsonArray remainingSystem)
        {
            foreach (var item in remainingSystem.OfType<JsonObject>())
            {
                yield return item;
            }
        }

        if (root["tools"] is JsonArray remainingTools)
        {
            foreach (var item in remainingTools.OfType<JsonObject>())
            {
                yield return item;
            }
        }
    }

    private static int LastCacheControlIndex(JsonArray array)
    {
        for (var index = array.Count - 1; index >= 0; index--)
        {
            if (array[index] is JsonObject obj && obj.ContainsKey("cache_control"))
            {
                return index;
            }
        }

        return -1;
    }

    private IReadOnlyList<TransparentProxyModelSelectedBody> BuildRouteModelSelectedBodies(
        byte[] requestBody,
        TransparentProxyRoute route)
    {
        if (requestBody.Length == 0)
        {
            return [new TransparentProxyModelSelectedBody(requestBody, string.Empty, string.Empty)];
        }

        try
        {
            var node = JsonNode.Parse(requestBody);
            if (node is not JsonObject obj)
            {
                return [new TransparentProxyModelSelectedBody(requestBody, string.Empty, string.Empty)];
            }

            var model = obj.TryGetPropertyValue("model", out var modelNode)
                ? modelNode?.GetValue<string>()
                : null;
            if (string.IsNullOrWhiteSpace(model))
            {
                return [new TransparentProxyModelSelectedBody(requestBody, string.Empty, string.Empty)];
            }

            var clientModel = model.Trim();
            var upstreamModels = ResolveUpstreamModelCandidates(clientModel, route);
            if (upstreamModels.Count == 0)
            {
                return [new TransparentProxyModelSelectedBody(requestBody, clientModel, clientModel)];
            }

            var offset = upstreamModels.Count <= 1
                ? 0
                : NextModelPoolOffset($"{route.Id}|{StripRoutePrefix(clientModel, route)}", upstreamModels.Count);
            var rotated = Rotate(upstreamModels, offset);
            List<TransparentProxyModelSelectedBody> bodies = [];
            foreach (var upstreamModel in rotated)
            {
                if (string.Equals(upstreamModel, clientModel, StringComparison.Ordinal))
                {
                    bodies.Add(new TransparentProxyModelSelectedBody(requestBody, clientModel, upstreamModel));
                    continue;
                }

                var clone = obj.DeepClone();
                if (clone is not JsonObject clonedObject)
                {
                    continue;
                }

                clonedObject["model"] = upstreamModel;
                bodies.Add(new TransparentProxyModelSelectedBody(
                    JsonSerializer.SerializeToUtf8Bytes(clonedObject, CompactJsonOptions),
                    clientModel,
                    upstreamModel));
            }

            return bodies.Count == 0
                ? [new TransparentProxyModelSelectedBody(requestBody, clientModel, clientModel)]
                : bodies;
        }
        catch
        {
            return [new TransparentProxyModelSelectedBody(requestBody, string.Empty, string.Empty)];
        }
    }

    private int NextModelPoolOffset(string key, int count)
    {
        if (count <= 1)
        {
            return 0;
        }

        lock (_modelPoolSyncRoot)
        {
            _modelPoolOffsets.TryGetValue(key, out var offset);
            _modelPoolOffsets[key] = offset + 1;
            if (_modelPoolOffsets.Count > 4096)
            {
                _modelPoolOffsets.Clear();
            }

            return Math.Abs(offset % count);
        }
    }

    private static IReadOnlyList<string> Rotate(IReadOnlyList<string> values, int offset)
    {
        if (values.Count <= 1 || offset <= 0)
        {
            return values;
        }

        return values.Skip(offset).Concat(values.Take(offset)).ToArray();
    }

    private static IReadOnlyList<string> ResolveUpstreamModelCandidates(string clientModel, TransparentProxyRoute route)
    {
        var model = StripRoutePrefix(clientModel.Trim(), route);
        var matches = route.ModelMappings
            .Where(mapping =>
                string.Equals(mapping.Name, model, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mapping.EffectiveAlias, model, StringComparison.OrdinalIgnoreCase))
            .Select(static mapping => mapping.Name.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matches.Length > 0 ? matches : [model];
    }

    private static byte[] ApplyRouteModelSelection(byte[] requestBody, TransparentProxyRoute route, out string clientModel)
    {
        clientModel = string.Empty;
        if (requestBody.Length == 0)
        {
            return requestBody;
        }

        try
        {
            var node = JsonNode.Parse(requestBody);
            if (node is not JsonObject obj)
            {
                return requestBody;
            }

            var model = obj.TryGetPropertyValue("model", out var modelNode)
                ? modelNode?.GetValue<string>()
                : null;
            if (string.IsNullOrWhiteSpace(model))
            {
                return requestBody;
            }

            clientModel = model.Trim();
            var upstreamModel = ResolveUpstreamModel(clientModel, route);
            if (string.Equals(upstreamModel, clientModel, StringComparison.Ordinal))
            {
                return requestBody;
            }

            obj["model"] = upstreamModel;
            return JsonSerializer.SerializeToUtf8Bytes(obj, CompactJsonOptions);
        }
        catch
        {
            return requestBody;
        }
    }

    private static string ResolveUpstreamModel(string clientModel, TransparentProxyRoute route)
    {
        var model = StripRoutePrefix(clientModel.Trim(), route);
        foreach (var mapping in route.ModelMappings)
        {
            if (string.Equals(mapping.Name, model, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mapping.EffectiveAlias, model, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.Name;
            }
        }

        return model;
    }

    private static string StripRoutePrefix(string model, TransparentProxyRoute route)
    {
        var prefix = route.Prefix.Trim().Trim('/');
        return !string.IsNullOrWhiteSpace(prefix) &&
               model.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(prefix.Length + 1)..]
            : model;
    }

    private static byte[] ApplyPayloadRules(
        byte[] body,
        TransparentProxyRoute route,
        string wireApi,
        string requestedModel,
        string upstreamModel,
        string pathAndQuery)
    {
        if (body.Length == 0 || string.IsNullOrWhiteSpace(route.PayloadRulesText))
        {
            return body;
        }

        try
        {
            var payload = JsonNode.Parse(body);
            var rulesRoot = JsonNode.Parse(route.PayloadRulesText);
            if (payload is not JsonObject payloadObject || rulesRoot is null)
            {
                return body;
            }

            foreach (var rule in EnumeratePayloadRules(rulesRoot))
            {
                if (!PayloadRuleMatches(rule.Rule, rule.Action, wireApi, requestedModel, upstreamModel, pathAndQuery))
                {
                    continue;
                }

                ApplyPayloadRule(payloadObject, rule.Action, rule.Rule);
            }

            return JsonSerializer.SerializeToUtf8Bytes(payloadObject, CompactJsonOptions);
        }
        catch
        {
            return body;
        }
    }

    private static IEnumerable<(string Action, JsonObject Rule)> EnumeratePayloadRules(JsonNode root)
    {
        if (root is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                var action = ReadString(item, "action");
                if (string.IsNullOrWhiteSpace(action))
                {
                    action = item.ContainsKey("filter") ? "filter" : "override";
                }

                yield return (action, item);
            }

            yield break;
        }

        if (root is not JsonObject obj)
        {
            yield break;
        }

        foreach (var action in new[] { "default", "override", "filter" })
        {
            if (obj[action] is JsonArray actionRules)
            {
                foreach (var item in actionRules.OfType<JsonObject>())
                {
                    yield return (action, item);
                }
            }
            else if (obj[action] is JsonObject actionRule)
            {
                yield return (action, actionRule);
            }
        }

        if (obj.TryGetPropertyValue("action", out _))
        {
            yield return (ReadString(obj, "action"), obj);
        }
    }

    private static bool PayloadRuleMatches(
        JsonObject rule,
        string action,
        string wireApi,
        string requestedModel,
        string upstreamModel,
        string pathAndQuery)
    {
        var protocol = ReadString(rule, "protocol");
        if (!string.IsNullOrWhiteSpace(protocol) &&
            !PayloadProtocolMatches(protocol, wireApi))
        {
            return false;
        }

        var path = ReadString(rule, "path");
        if (!string.IsNullOrWhiteSpace(path) &&
            !pathAndQuery.Contains(path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var models = ReadStringArray(rule, "models");
        if (models.Count == 0)
        {
            return true;
        }

        var candidates = new[] { requestedModel, StripModelSuffix(requestedModel), upstreamModel, StripModelSuffix(upstreamModel) }
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.Length > 0 &&
               models.Any(pattern => candidates.Any(candidate => WildcardMatch(candidate, pattern)));
    }

    private static void ApplyPayloadRule(JsonObject payload, string action, JsonObject rule)
    {
        var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedAction == "filter")
        {
            foreach (var path in ReadStringArray(rule, "params").Concat(ReadStringArray(rule, "paths")))
            {
                RemoveJsonPath(payload, path);
            }

            if (rule["filter"] is JsonArray filters)
            {
                foreach (var path in filters.OfType<JsonValue>().Select(static item => item.GetValue<string>()))
                {
                    RemoveJsonPath(payload, path);
                }
            }

            return;
        }

        var sourceProperty = normalizedAction == "default" ? "params" : normalizedAction;
        if (rule[sourceProperty] is not JsonObject values)
        {
            values = rule["params"] as JsonObject ?? rule["set"] as JsonObject ?? [];
        }

        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            {
                continue;
            }

            if (normalizedAction == "default" && TryGetJsonPath(payload, pair.Key, out _))
            {
                continue;
            }

            SetJsonPath(payload, pair.Key, pair.Value.DeepClone());
        }
    }

    private static bool PayloadProtocolMatches(string protocol, string wireApi)
    {
        var normalized = protocol.Trim();
        return normalized.Equals(wireApi, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(DisplayWireApi(wireApi), StringComparison.OrdinalIgnoreCase) ||
               (normalized.Equals("responses", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(wireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal)) ||
               (normalized.Equals("anthropic", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(wireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal)) ||
               (normalized.Equals("openai", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(wireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal));
    }

    private static string StripModelSuffix(string model)
    {
        var normalized = (model ?? string.Empty).Trim();
        var colon = normalized.IndexOf(':');
        return colon > 0 ? normalized[..colon] : normalized;
    }

    private static string ReadString(JsonObject obj, string propertyName)
        => obj.TryGetPropertyValue(propertyName, out var node) && node is JsonValue value
            ? value.GetValue<string>()?.Trim() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadStringArray(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return Array.Empty<string>();
        }

        if (node is JsonValue value)
        {
            var text = value.GetValue<string>()?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(text)
                ? Array.Empty<string>()
                : text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (node is JsonArray array)
        {
            return array
                .OfType<JsonValue>()
                .Select(static item => item.GetValue<string>()?.Trim() ?? string.Empty)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static bool TryGetJsonPath(JsonObject root, string path, out JsonNode? value)
    {
        value = null;
        var current = (JsonNode?)root;
        foreach (var part in SplitJsonPath(path))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current))
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    private static void SetJsonPath(JsonObject root, string path, JsonNode value)
    {
        var parts = SplitJsonPath(path);
        if (parts.Length == 0)
        {
            return;
        }

        var current = root;
        foreach (var part in parts.Take(parts.Length - 1))
        {
            if (current[part] is not JsonObject child)
            {
                child = [];
                current[part] = child;
            }

            current = child;
        }

        current[parts[^1]] = value;
    }

    private static void RemoveJsonPath(JsonObject root, string path)
    {
        var parts = SplitJsonPath(path);
        if (parts.Length == 0)
        {
            return;
        }

        var current = root;
        foreach (var part in parts.Take(parts.Length - 1))
        {
            if (current[part] is not JsonObject child)
            {
                return;
            }

            current = child;
        }

        current.Remove(parts[^1]);
    }

    private static string[] SplitJsonPath(string path)
        => (path ?? string.Empty)
            .Trim()
            .Trim('.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool WildcardMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = "^" + string.Concat(pattern.Trim().Select(static character => character switch
        {
            '*' => ".*",
            '?' => ".",
            _ => System.Text.RegularExpressions.Regex.Escape(character.ToString())
        })) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            value,
            regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static string? TryReadRequestModel(byte[] requestBody)
    {
        if (requestBody.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return TryReadStringProperty(document.RootElement, "model");
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadStringProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string ResolveResponseModel(string upstreamText, string fallbackModel)
    {
        if (!string.IsNullOrWhiteSpace(fallbackModel))
        {
            return fallbackModel.Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            return TryReadStringProperty(document.RootElement, "model") ?? "relaybench-proxy";
        }
        catch
        {
            return "relaybench-proxy";
        }
    }

    private static string BuildUpstreamUrl(string baseUrl, string pathAndQuery)
    {
        var baseUri = new Uri(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        var path = pathAndQuery;
        var queryIndex = path.IndexOf('?');
        var query = queryIndex >= 0 ? path[queryIndex..] : string.Empty;
        var pathOnly = queryIndex >= 0 ? path[..queryIndex] : path;

        var normalizedBasePath = baseUri.AbsolutePath.TrimEnd('/');
        var normalizedPath = pathOnly.TrimStart('/');
        if (normalizedBasePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
            normalizedPath.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[3..];
        }

        return new Uri(baseUri, normalizedPath).ToString().TrimEnd('/') + query;
    }

    private static bool IsOpenAiChatCompletionsRequest(string method, string pathAndQuery)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = ExtractRelativePath(pathAndQuery);
        return relativePath.Equals("chat/completions", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals("v1/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferWireApiFromPath(string pathAndQuery)
    {
        var relativePath = ExtractRelativePath(pathAndQuery);
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

    private static string ExtractRelativePath(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        var pathOnly = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
        return pathOnly.Trim().TrimStart('/');
    }

    private static string ExtractQuery(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        return queryIndex >= 0 ? pathAndQuery[queryIndex..] : string.Empty;
    }

    private static JsonNode? TryExtractUsageNode(string upstreamText)
    {
        try
        {
            var node = JsonNode.Parse(upstreamText);
            return node is JsonObject obj && obj["usage"] is { } usage
                ? usage.DeepClone()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryExtractToolCalls(string upstreamText, out IReadOnlyList<TransparentProxyToolCall> toolCalls)
    {
        toolCalls = Array.Empty<TransparentProxyToolCall>();
        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            List<TransparentProxyToolCall> calls = [];
            ExtractOpenAiChatToolCalls(document.RootElement, calls);
            ExtractResponsesToolCalls(document.RootElement, calls);
            ExtractAnthropicToolCalls(document.RootElement, calls);
            toolCalls = calls
                .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
                .ToArray();
            return toolCalls.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractOpenAiChatToolCalls(JsonElement root, List<TransparentProxyToolCall> calls)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            var id = TryReadString(toolCall, "id") ?? $"call_{calls.Count + 1}";
            if (!toolCall.TryGetProperty("function", out var function))
            {
                continue;
            }

            var name = TryReadString(function, "name");
            var arguments = TryReadString(function, "arguments") ?? "{}";
            if (!string.IsNullOrWhiteSpace(name))
            {
                calls.Add(new TransparentProxyToolCall(id, name, arguments));
            }
        }
    }

    private static void ExtractResponsesToolCalls(JsonElement root, List<TransparentProxyToolCall> calls)
    {
        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = TryReadString(item, "call_id") ?? TryReadString(item, "id") ?? $"call_{calls.Count + 1}";
            var name = TryReadString(item, "name");
            var arguments = TryReadString(item, "arguments") ?? "{}";
            if (!string.IsNullOrWhiteSpace(name))
            {
                calls.Add(new TransparentProxyToolCall(id, name, arguments));
            }
        }
    }

    private static void ExtractAnthropicToolCalls(JsonElement root, List<TransparentProxyToolCall> calls)
    {
        if (!root.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = TryReadString(item, "id") ?? $"call_{calls.Count + 1}";
            var name = TryReadString(item, "name");
            var arguments = item.TryGetProperty("input", out var input) ? input.GetRawText() : "{}";
            if (!string.IsNullOrWhiteSpace(name))
            {
                calls.Add(new TransparentProxyToolCall(id, name, arguments));
            }
        }
    }

    private static string? TryReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static byte[] BuildOpenAiChatCompletionBytes(
        string content,
        string model,
        string wireApi,
        JsonNode? usage)
    {
        var root = BuildOpenAiChatRoot(model, wireApi, usage);
        root["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = content
                },
                ["finish_reason"] = "stop"
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(root, CompactJsonOptions);
    }

    private static byte[] BuildOpenAiChatToolCallBytes(
        IReadOnlyList<TransparentProxyToolCall> toolCalls,
        string model,
        string wireApi,
        JsonNode? usage)
    {
        var root = BuildOpenAiChatRoot(model, wireApi, usage);
        JsonArray toolCallArray = [];
        foreach (var toolCall in toolCalls)
        {
            toolCallArray.Add(new JsonObject
            {
                ["id"] = string.IsNullOrWhiteSpace(toolCall.Id) ? $"call_{toolCallArray.Count + 1}" : toolCall.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolCall.Name,
                    ["arguments"] = string.IsNullOrWhiteSpace(toolCall.Arguments) ? "{}" : toolCall.Arguments
                }
            });
        }

        root["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = toolCallArray
                },
                ["finish_reason"] = "tool_calls"
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(root, CompactJsonOptions);
    }

    private static JsonObject BuildOpenAiChatRoot(string model, string wireApi, JsonNode? usage)
    {
        var root = new JsonObject
        {
            ["id"] = $"chatcmpl-relaybench-{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = string.IsNullOrWhiteSpace(model) ? "relaybench-proxy" : model.Trim(),
            ["relaybench"] = new JsonObject
            {
                ["upstream_wire_api"] = wireApi
            }
        };

        if (usage is not null)
        {
            root["usage"] = usage;
        }

        return root;
    }
}

internal sealed record TransparentProxyNormalizedChatResponse(byte[] Body, string AssistantText);

internal sealed record TransparentProxyModelSelectedBody(
    byte[] Body,
    string ClientModel,
    string UpstreamModel);

internal sealed record TransparentProxyToolCall(string Id, string Name, string Arguments);
