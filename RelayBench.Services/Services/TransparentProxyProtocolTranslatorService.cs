using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Services;

namespace RelayBench.Services;

internal sealed class TransparentProxyProtocolTranslatorService
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly TransparentProxyWireProtocolRegistry _wireProtocolRegistry;
    private readonly TransparentProxyTranslatorRegistry _translatorRegistry;
    private readonly TransparentProxyPromptSessionCacheService _promptSessionCache;
    private readonly TransparentProxyPayloadRuleService _payloadRuleService;
    private readonly TransparentProxyPromptCacheOptimizer _promptCacheOptimizer;
    private readonly object _modelPoolSyncRoot = new();
    private readonly Dictionary<string, int> _modelPoolOffsets = new(StringComparer.OrdinalIgnoreCase);

    public TransparentProxyProtocolTranslatorService(
        TransparentProxyWireProtocolRegistry? wireProtocolRegistry = null,
        TransparentProxyTranslatorRegistry? translatorRegistry = null,
        TransparentProxyPromptSessionCacheService? promptSessionCache = null,
        TransparentProxyPayloadRuleService? payloadRuleService = null,
        TransparentProxyPromptCacheOptimizer? promptCacheOptimizer = null)
    {
        _wireProtocolRegistry = wireProtocolRegistry ?? new TransparentProxyWireProtocolRegistry();
        _translatorRegistry = translatorRegistry ?? new TransparentProxyTranslatorRegistry();
        _promptSessionCache = promptSessionCache ?? new TransparentProxyPromptSessionCacheService();
        _payloadRuleService = payloadRuleService ?? new TransparentProxyPayloadRuleService();
        _promptCacheOptimizer = promptCacheOptimizer ?? new TransparentProxyPromptCacheOptimizer();
    }

    public IReadOnlyList<TransparentProxyPreparedRequest> BuildPreparedUpstreamRequests(
        string method,
        string pathAndQuery,
        byte[] requestBody,
        TransparentProxyRoute route,
        bool streamRequested,
        string? sessionAffinityKey = null,
        NameValueCollection? sourceHeaders = null)
    {
        var modelBodies = BuildRouteModelSelectedBodies(requestBody, route);
        List<TransparentProxyPreparedRequest> allAttempts = [];
        foreach (var modelBody in modelBodies)
        {
            var directBody = modelBody.Body;
            var clientModel = modelBody.ClientModel;
            var promptSession = _promptSessionCache.Resolve(route, directBody, sessionAffinityKey);
            var pathModel = TryReadGeminiModelFromPath(pathAndQuery);
            var upstreamModel = TryReadRequestModel(directBody) ?? modelBody.UpstreamModel;
            if (string.IsNullOrWhiteSpace(upstreamModel))
            {
                upstreamModel = pathModel ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(clientModel))
            {
                clientModel = pathModel ?? string.Empty;
            }

            var isToolExchange = IsToolExchangeRequest(directBody);
            var preferJsonStreamExtraction = streamRequested && PrefersJsonStreamExtraction(directBody);
            if (route.IsCodexOAuth)
            {
                if (CanCodexOAuthHandleRequest(method, pathAndQuery))
                {
                    allAttempts.Add(BuildCodexOAuthPreparedRequest(
                        method,
                        pathAndQuery,
                        directBody,
                        route,
                        promptSession,
                        clientModel,
                        upstreamModel,
                        streamRequested,
                        isToolExchange,
                        preferJsonStreamExtraction,
                        sourceHeaders));
                }

                continue;
            }

            if (IsOpenAiChatCompletionsRequest(method, pathAndQuery) &&
                TransparentProxyGeminiAdapter.IsGeminiNativeRoute(route))
            {
                allAttempts.Add(TransparentProxyGeminiAdapter.BuildOpenAiChatPreparedRequest(
                    route,
                    directBody,
                    clientModel,
                    upstreamModel,
                    streamRequested,
                    isToolExchange,
                    preferJsonStreamExtraction,
                    BuildUpstreamUrl));
                continue;
            }

            if (IsOpenAiChatCompletionsRequest(method, pathAndQuery))
            {
                var requestText = Encoding.UTF8.GetString(directBody);
                var query = ExtractQuery(pathAndQuery);
                List<TransparentProxyPreparedRequest> attempts = [];
                foreach (var wireApi in _wireProtocolRegistry.BuildWireApiAttempts(route))
                {
                    try
                    {
                        var prepared = _translatorRegistry.PreparePostJson(
                            ExtractRelativePath(pathAndQuery),
                            requestText,
                            wireApi,
                            streamRequested);
                        var body = Encoding.UTF8.GetBytes(prepared.RequestBody);
                        var headers = MergeHeaders(route.Headers, prepared.ExtraHeaders);
                        body = _payloadRuleService.Apply(
                            body,
                            route,
                            prepared.WireApi,
                            clientModel,
                            upstreamModel,
                            pathAndQuery,
                            ProxyWireApiProbeService.ChatCompletionsWireApi,
                            sourceHeaders);
                        if (!ShouldPreserveMappedThinkingSuffix(clientModel, upstreamModel))
                        {
                            body = ApplyModelThinkingSuffix(body, prepared.WireApi);
                        }
                        body = _promptCacheOptimizer.Apply(
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
                            ProxyWireApiProbeService.ChatCompletionsWireApi,
                            BuildUpstreamUrl(route.BaseUrl, "/v1/" + prepared.RelativePath + query),
                            body,
                            headers,
                            !string.Equals(prepared.WireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal),
                            string.IsNullOrWhiteSpace(clientModel) ? preparedModel : clientModel,
                            preparedModel,
                            isToolExchange,
                            preferJsonStreamExtraction,
                            prepared.ToolNameAliases));
                    }
                    catch
                    {
                        if (string.Equals(wireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal))
                        {
                            attempts.Add(BuildDirectPreparedRequest(
                                pathAndQuery,
                                directBody,
                                route,
                                promptSession,
                                clientModel,
                                upstreamModel,
                                isToolExchange,
                                preferJsonStreamExtraction,
                                sourceHeaders));
                        }
                    }
                }

                if (attempts.Count > 0)
                {
                    allAttempts.AddRange(attempts);
                    continue;
                }
            }

            allAttempts.Add(BuildDirectPreparedRequest(
                pathAndQuery,
                directBody,
                route,
                promptSession,
                clientModel,
                upstreamModel,
                isToolExchange,
                preferJsonStreamExtraction,
                sourceHeaders));
        }

        return allAttempts;
    }

    public TransparentProxyPromptSessionCacheStats PromptSessionCacheStats => _promptSessionCache.Stats;

    public void ClearPromptSessionCache() => _promptSessionCache.Clear();

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

    public static string DisplayWireApi(string wireApi)
        => wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic Messages",
            TransparentProxyNativeWireApis.Gemini => "Gemini Native",
            _ => "OpenAI Chat"
        };

    private TransparentProxyPreparedRequest BuildDirectPreparedRequest(
        string pathAndQuery,
        byte[] requestBody,
        TransparentProxyRoute route,
        TransparentProxyPromptSessionMaterial promptSession,
        string clientModel,
        string upstreamModel,
        bool isToolExchange,
        bool preferJsonStreamExtraction,
        NameValueCollection? sourceHeaders)
    {
        var wireApi = InferWireApiFromPath(pathAndQuery);
        var headers = MergeHeaders(route.Headers, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (string.Equals(wireApi, TransparentProxyNativeWireApis.Gemini, StringComparison.Ordinal) &&
            TryApplyGeminiPathThinkingSuffix(pathAndQuery, requestBody, out var cleanGeminiPath, out var geminiBody, out var cleanGeminiModel))
        {
            pathAndQuery = cleanGeminiPath;
            requestBody = geminiBody;
            upstreamModel = cleanGeminiModel;
        }

        requestBody = _payloadRuleService.Apply(
            requestBody,
            route,
            wireApi,
            clientModel,
            upstreamModel,
            pathAndQuery,
            wireApi,
            sourceHeaders);
        if (!ShouldPreserveMappedThinkingSuffix(clientModel, upstreamModel))
        {
            requestBody = ApplyModelThinkingSuffix(requestBody, wireApi);
        }
        var body = _promptCacheOptimizer.Apply(
            requestBody,
            promptSession,
            wireApi,
            headers,
            out var optimizedModel);
        if (string.Equals(wireApi, TransparentProxyNativeWireApis.Gemini, StringComparison.Ordinal))
        {
            body = StripNativeGeminiClientOnlyFields(body, pathAndQuery);
        }

        var preparedModel = string.IsNullOrWhiteSpace(optimizedModel)
            ? TryReadRequestModel(body) ?? upstreamModel
            : optimizedModel;
        return new TransparentProxyPreparedRequest(
            wireApi,
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

    private static byte[] StripNativeGeminiClientOnlyFields(byte[] requestBody, string pathAndQuery)
    {
        if (requestBody.Length == 0)
        {
            return requestBody;
        }

        try
        {
            if (JsonNode.Parse(requestBody) is not JsonObject root)
            {
                return requestBody;
            }

            var changed = root.Remove("session_id");
            changed = BackfillNativeGeminiFunctionResponseNames(root) || changed;
            if (IsGeminiCountTokensRequest(pathAndQuery))
            {
                changed = root.Remove("tools") || changed;
                changed = root.Remove("generationConfig") || changed;
                changed = root.Remove("safetySettings") || changed;
            }
            else
            {
                changed = NormalizeNativeGeminiResponseJsonSchema(root) || changed;
                changed = NormalizeNativeGeminiToolJsonSchemas(root) || changed;
            }

            return changed
                ? JsonSerializer.SerializeToUtf8Bytes(root, CompactJsonOptions)
                : requestBody;
        }
        catch
        {
            return requestBody;
        }
    }

    private static bool TryApplyGeminiPathThinkingSuffix(
        string pathAndQuery,
        byte[] requestBody,
        out string cleanPathAndQuery,
        out byte[] updatedBody,
        out string cleanModel)
    {
        cleanPathAndQuery = pathAndQuery;
        updatedBody = requestBody;
        cleanModel = string.Empty;

        var pathModel = TryReadGeminiModelFromPath(pathAndQuery);
        if (!TransparentProxyThinkingSuffix.TryParse(pathModel ?? string.Empty, out var suffix))
        {
            return false;
        }

        cleanModel = suffix.ModelName;
        cleanPathAndQuery = ReplaceGeminiPathModel(pathAndQuery, suffix.ModelName);
        if (!IsGeminiCountTokensRequest(cleanPathAndQuery))
        {
            updatedBody = ApplyGeminiNativePathThinkingSuffix(requestBody, suffix);
        }

        return true;
    }

    private static byte[] ApplyGeminiNativePathThinkingSuffix(
        byte[] requestBody,
        TransparentProxyThinkingSuffixResult suffix)
    {
        if (requestBody.Length == 0)
        {
            return requestBody;
        }

        try
        {
            if (JsonNode.Parse(requestBody) is not JsonObject root)
            {
                return requestBody;
            }

            JsonObject config = root["generationConfig"] as JsonObject ?? [];
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

            if (thinking.Count == 0)
            {
                return requestBody;
            }

            thinking["includeThoughts"] = suffix.IncludeThoughts;
            config["thinkingConfig"] = thinking;
            root["generationConfig"] = config;
            return JsonSerializer.SerializeToUtf8Bytes(root, CompactJsonOptions);
        }
        catch
        {
            return requestBody;
        }
    }

    private static string ReplaceGeminiPathModel(string pathAndQuery, string cleanModel)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        var pathOnly = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
        var query = queryIndex >= 0 ? pathAndQuery[queryIndex..] : string.Empty;
        var modelsIndex = pathOnly.IndexOf("models/", StringComparison.OrdinalIgnoreCase);
        if (modelsIndex < 0)
        {
            return pathAndQuery;
        }

        var modelStart = modelsIndex + "models/".Length;
        var actionIndex = pathOnly.IndexOf(':', modelStart);
        var modelEnd = actionIndex >= 0 ? actionIndex : pathOnly.Length;
        var escapedModel = Uri.EscapeDataString(cleanModel);
        return pathOnly[..modelStart] + escapedModel + pathOnly[modelEnd..] + query;
    }

    private static byte[] ApplyModelThinkingSuffix(byte[] requestBody, string wireApi)
    {
        if (requestBody.Length == 0)
        {
            return requestBody;
        }

        try
        {
            if (JsonNode.Parse(requestBody) is not JsonObject root)
            {
                return requestBody;
            }

            var model = ReadJsonString(root, "model");
            if (!TransparentProxyThinkingSuffix.TryParse(model, out var suffix))
            {
                return requestBody;
            }

            root["model"] = suffix.ModelName;
            var normalizedWireApi = ProxyWireApiProbeService.NormalizeWireApi(wireApi) ?? wireApi;
            if (string.Equals(normalizedWireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal))
            {
                root["reasoning_effort"] = suffix.Effort;
            }
            else if (string.Equals(normalizedWireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal))
            {
                var reasoning = root["reasoning"] as JsonObject ?? [];
                reasoning["effort"] = suffix.Effort;
                root["reasoning"] = reasoning;
            }
            else if (string.Equals(normalizedWireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal))
            {
                ApplyAnthropicThinkingSuffix(root, suffix);
            }

            return JsonSerializer.SerializeToUtf8Bytes(root, CompactJsonOptions);
        }
        catch
        {
            return requestBody;
        }
    }

    private static bool ShouldPreserveMappedThinkingSuffix(string clientModel, string upstreamModel)
    {
        if (!TransparentProxyThinkingSuffix.TryParse(clientModel, out var clientSuffix) ||
            !TransparentProxyThinkingSuffix.TryParse(upstreamModel, out var upstreamSuffix))
        {
            return false;
        }

        return !string.Equals(clientSuffix.ModelName, upstreamSuffix.ModelName, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyAnthropicThinkingSuffix(
        JsonObject root,
        TransparentProxyThinkingSuffixResult suffix)
    {
        if (suffix.Budget == 0 || string.Equals(suffix.Effort, "none", StringComparison.OrdinalIgnoreCase))
        {
            root["thinking"] = new JsonObject
            {
                ["type"] = "disabled"
            };
            RemoveAnthropicOutputEffort(root);
            return;
        }

        if (suffix.Budget == -1 || string.Equals(suffix.Effort, "auto", StringComparison.OrdinalIgnoreCase))
        {
            root["thinking"] = new JsonObject
            {
                ["type"] = "enabled"
            };
            RemoveAnthropicOutputEffort(root);
            return;
        }

        if (!string.IsNullOrWhiteSpace(suffix.Level) &&
            suffix.Budget is null)
        {
            var effort = MapAnthropicAdaptiveThinkingEffort(suffix);
            if (string.IsNullOrWhiteSpace(effort))
            {
                return;
            }

            root["thinking"] = new JsonObject
            {
                ["type"] = "adaptive"
            };
            var outputConfig = root["output_config"] as JsonObject ?? [];
            outputConfig["effort"] = effort;
            root["output_config"] = outputConfig;
            return;
        }

        if (suffix.Budget is not { } budget || budget <= 0)
        {
            return;
        }

        root["thinking"] = new JsonObject
        {
            ["type"] = "enabled",
            ["budget_tokens"] = budget
        };
        EnsureAnthropicMaxTokensExceedsBudget(root, budget);
        RemoveAnthropicOutputEffort(root);
    }

    private static string? MapAnthropicAdaptiveThinkingEffort(TransparentProxyThinkingSuffixResult suffix)
    {
        var level = !string.IsNullOrWhiteSpace(suffix.RawSuffix)
            ? suffix.RawSuffix.Trim().ToLowerInvariant()
            : suffix.Level?.Trim().ToLowerInvariant();

        return level switch
        {
            "minimal" or "low" => "low",
            "medium" => "medium",
            "high" => "high",
            "xhigh" => "high",
            "max" => "max",
            _ => null
        };
    }

    private static void EnsureAnthropicMaxTokensExceedsBudget(JsonObject root, long budget)
    {
        if (budget >= int.MaxValue)
        {
            return;
        }

        var maxTokens = ReadJsonLong(root, "max_tokens");
        if (!maxTokens.HasValue || maxTokens.Value <= budget)
        {
            root["max_tokens"] = budget + 1;
        }
    }

    private static void RemoveAnthropicOutputEffort(JsonObject root)
    {
        if (root["output_config"] is not JsonObject outputConfig)
        {
            return;
        }

        outputConfig.Remove("effort");
        if (outputConfig.Count == 0)
        {
            root.Remove("output_config");
        }
    }

    private static string ReadJsonString(JsonObject root, string propertyName)
        => root[propertyName] is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text ?? string.Empty
            : string.Empty;

    private static long? ReadJsonLong(JsonObject root, string propertyName)
        => root[propertyName] is JsonValue value &&
           value.TryGetValue<long>(out var number)
            ? number
            : null;

    private static bool BackfillNativeGeminiFunctionResponseNames(JsonObject root)
    {
        if (root["contents"] is not JsonArray contents)
        {
            return false;
        }

        Queue<string> pendingFunctionCallNames = [];
        var changed = false;
        foreach (var contentNode in contents)
        {
            if (contentNode is not JsonObject content ||
                content["parts"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var partNode in parts)
            {
                if (partNode is not JsonObject part)
                {
                    continue;
                }

                if (part["functionCall"] is JsonObject functionCall)
                {
                    var callName = ReadNativeGeminiString(functionCall, "name");
                    if (!string.IsNullOrWhiteSpace(callName))
                    {
                        pendingFunctionCallNames.Enqueue(callName.Trim());
                    }

                    continue;
                }

                if (part["functionResponse"] is not JsonObject functionResponse ||
                    !pendingFunctionCallNames.TryDequeue(out var fallbackName) ||
                    !string.IsNullOrWhiteSpace(ReadNativeGeminiString(functionResponse, "name")))
                {
                    continue;
                }

                functionResponse["name"] = fallbackName;
                changed = true;
            }
        }

        return changed;
    }

    private static bool NormalizeNativeGeminiResponseJsonSchema(JsonObject root)
    {
        if (root["generationConfig"] is not JsonObject config)
        {
            return false;
        }

        var schemaNode = config["responseSchema"] ?? config["response_schema"];
        if (schemaNode is null)
        {
            return false;
        }

        if (!config.ContainsKey("responseJsonSchema") &&
            !config.ContainsKey("response_json_schema"))
        {
            config["responseJsonSchema"] = schemaNode.DeepClone();
        }

        var changed = config.Remove("responseSchema");
        changed = config.Remove("response_schema") || changed;
        return changed;
    }

    private static bool NormalizeNativeGeminiToolJsonSchemas(JsonObject root)
    {
        if (root["tools"] is not JsonArray tools)
        {
            return false;
        }

        var changed = false;
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
                if (declarationNode is not JsonObject declaration ||
                    declaration["parameters"] is not JsonObject parameters ||
                    !RequiresNativeGeminiParametersJsonSchema(parameters))
                {
                    continue;
                }

                if (!declaration.ContainsKey("parametersJsonSchema") &&
                    !declaration.ContainsKey("parameters_json_schema"))
                {
                    declaration["parametersJsonSchema"] = parameters.DeepClone();
                }

                changed = declaration.Remove("parameters") || changed;
            }
        }

        return changed;
    }

    private static bool RequiresNativeGeminiParametersJsonSchema(JsonNode? node)
    {
        if (node is JsonArray)
        {
            return true;
        }

        if (node is not JsonObject obj)
        {
            return false;
        }

        if (obj["type"] is JsonArray)
        {
            return true;
        }

        foreach (var (key, value) in obj)
        {
            if (IsNativeGeminiJsonSchemaOnlyKey(key) ||
                RequiresNativeGeminiParametersJsonSchema(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNativeGeminiJsonSchemaOnlyKey(string key)
        => key is "$schema" or "$id" or "additionalProperties" or "unevaluatedProperties" or
            "patternProperties" or "dependentSchemas" or "anyOf" or "oneOf" or "allOf" or
            "not" or "if" or "then" or "else" or "prefixItems";

    private static string ReadNativeGeminiString(JsonObject source, string propertyName)
        => source[propertyName] is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text ?? string.Empty
            : string.Empty;

    private static bool IsGeminiCountTokensRequest(string pathAndQuery)
        => ExtractRelativePath(pathAndQuery).EndsWith(":countTokens", StringComparison.OrdinalIgnoreCase);

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

    private TransparentProxyPreparedRequest BuildCodexOAuthPreparedRequest(
        string method,
        string pathAndQuery,
        byte[] requestBody,
        TransparentProxyRoute route,
        TransparentProxyPromptSessionMaterial promptSession,
        string clientModel,
        string upstreamModel,
        bool streamRequested,
        bool isToolExchange,
        bool preferJsonStreamExtraction,
        NameValueCollection? sourceHeaders)
    {
        var clientWireApi = InferWireApiFromPath(pathAndQuery);
        var targetWireApi = ProxyWireApiProbeService.ResponsesWireApi;
        var headers = MergeHeaders(route.Headers, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        byte[] body;
        IReadOnlyDictionary<string, string>? toolNameAliases = null;
        var normalizeToChat = string.Equals(clientWireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal);
        var isResponsesCompact = IsCodexResponsesCompactRequest(pathAndQuery);

        if (IsOpenAiChatCompletionsRequest(method, pathAndQuery))
        {
            var prepared = _translatorRegistry.PreparePostJson(
                ExtractRelativePath(pathAndQuery),
                Encoding.UTF8.GetString(requestBody),
                targetWireApi,
                streamRequested);
            body = Encoding.UTF8.GetBytes(prepared.RequestBody);
            foreach (var header in prepared.ExtraHeaders)
            {
                headers[header.Key] = header.Value;
            }

            toolNameAliases = prepared.ToolNameAliases;
        }
        else if (string.Equals(clientWireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal))
        {
            body = CodexBackendProtocolAdapter.ConvertAnthropicMessagesToResponses(requestBody, streamRequested, route.CodexOAuthFastMode);
            normalizeToChat = false;
        }
        else if (string.Equals(clientWireApi, TransparentProxyNativeWireApis.Gemini, StringComparison.Ordinal))
        {
            var geminiToolNameAliases = CodexBackendProtocolAdapter.BuildGeminiToolNameAliases(requestBody);
            toolNameAliases = geminiToolNameAliases.Count == 0 ? null : geminiToolNameAliases;
            body = CodexBackendProtocolAdapter.ConvertGeminiNativeToResponses(
                requestBody,
                ResolveCodexOAuthGeminiTargetModel(upstreamModel, route),
                streamRequested,
                route.CodexOAuthFastMode);
            normalizeToChat = false;
        }
        else
        {
            body = CodexBackendProtocolAdapter.NormalizeResponsesPayload(
                requestBody,
                streamRequested,
                route.CodexOAuthFastMode,
                forceStreaming: !isResponsesCompact);
        }

        body = _payloadRuleService.Apply(
            body,
            route,
            targetWireApi,
            clientModel,
            upstreamModel,
            pathAndQuery,
            clientWireApi,
            sourceHeaders);
        body = _promptCacheOptimizer.Apply(
            body,
            promptSession,
            targetWireApi,
            headers,
            out var optimizedModel);
        body = CodexBackendProtocolAdapter.EnsureResponsesInstructions(
            body,
            route.CodexOAuthFastMode,
            forceStreaming: !isResponsesCompact);
        var preparedModel = string.IsNullOrWhiteSpace(optimizedModel)
            ? TryReadRequestModel(body) ?? upstreamModel
            : optimizedModel;
        return new TransparentProxyPreparedRequest(
            targetWireApi,
            clientWireApi,
            CodexBackendProtocolAdapter.BuildResponsesUrl(route, isResponsesCompact),
            body,
            headers,
            normalizeToChat,
            string.IsNullOrWhiteSpace(clientModel) ? preparedModel : clientModel,
            preparedModel,
            isToolExchange,
            preferJsonStreamExtraction,
            toolNameAliases);
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
            var upstreamModels = TransparentProxyModelAliasResolver.ResolveUpstreamModelCandidates(clientModel, route);
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

    private static string StripRoutePrefix(string model, TransparentProxyRoute route)
        => TransparentProxyModelAliasResolver.StripRoutePrefix(model, route);

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
        if (TransparentProxyGeminiUrlResolver.TryBuildNativeUrl(baseUrl, pathAndQuery, out var geminiNativeUrl))
        {
            return geminiNativeUrl;
        }

        var baseUri = new Uri(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        var path = pathAndQuery;
        var queryIndex = path.IndexOf('?');
        var query = queryIndex >= 0 ? path[queryIndex..] : string.Empty;
        var pathOnly = queryIndex >= 0 ? path[..queryIndex] : path;

        var normalizedBasePath = baseUri.AbsolutePath.TrimEnd('/');
        var normalizedPath = NormalizeRelativePathForUpstream(pathOnly, normalizedBasePath);

        var relativeUriPath = normalizedPath.Contains(':', StringComparison.Ordinal) &&
                              !normalizedPath.StartsWith("./", StringComparison.Ordinal) &&
                              !normalizedPath.StartsWith("/", StringComparison.Ordinal)
            ? "./" + normalizedPath
            : normalizedPath;
        return new Uri(baseUri, relativeUriPath).ToString().TrimEnd('/') + query;
    }

    private static bool IsOpenAiChatCompletionsRequest(string method, string pathAndQuery)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = ExtractRelativePath(pathAndQuery);
        return relativePath.Equals("chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanCodexOAuthHandleRequest(string method, string pathAndQuery)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = ExtractRelativePath(pathAndQuery);
        return relativePath.Equals("chat/completions", StringComparison.OrdinalIgnoreCase) ||
               IsResponsesPath(relativePath) ||
               IsAnthropicMessagesPath(relativePath) ||
               IsGeminiGenerateContentPath(relativePath);
    }

    private static string InferWireApiFromPath(string pathAndQuery)
    {
        var relativePath = ExtractRelativePath(pathAndQuery);
        if (IsGeminiNativePath(relativePath))
        {
            return TransparentProxyNativeWireApis.Gemini;
        }

        if (IsResponsesPath(relativePath))
        {
            return ProxyWireApiProbeService.ResponsesWireApi;
        }

        if (IsAnthropicMessagesPath(relativePath) ||
            IsAnthropicCountTokensPath(relativePath))
        {
            return ProxyWireApiProbeService.AnthropicMessagesWireApi;
        }

        return ProxyWireApiProbeService.ChatCompletionsWireApi;
    }

    private static string ExtractRelativePath(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        var pathOnly = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
        return NormalizeRelativePath(pathOnly);
    }

    private static string ExtractQuery(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        return queryIndex >= 0 ? pathAndQuery[queryIndex..] : string.Empty;
    }

    private static bool IsCodexResponsesCompactRequest(string pathAndQuery)
        => ExtractRelativePath(pathAndQuery).Equals("responses/compact", StringComparison.OrdinalIgnoreCase);

    private static bool IsResponsesPath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        return normalized.Equals("responses", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("responses/compact", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeminiNativePath(string relativePath)
        => IsGeminiCliInternalPath(relativePath) ||
           relativePath.Equals("models", StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("models/", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeminiGenerateContentPath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (!normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalized.Contains(":generateContent", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(":streamGenerateContent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeminiCliInternalPath(string relativePath)
        => relativePath.Equals("v1internal", StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("v1internal:", StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("v1internal/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnthropicMessagesPath(string relativePath)
        => relativePath.Equals("messages", StringComparison.OrdinalIgnoreCase) ||
           relativePath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnthropicCountTokensPath(string relativePath)
        => relativePath.Equals("messages/count_tokens", StringComparison.OrdinalIgnoreCase) ||
           relativePath.EndsWith("/messages/count_tokens", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadGeminiModelFromPath(string pathAndQuery)
    {
        var path = ExtractRelativePath(pathAndQuery);
        if (!path.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var model = path["models/".Length..];
        var actionIndex = model.IndexOf(':');
        if (actionIndex >= 0)
        {
            model = model[..actionIndex];
        }

        model = Uri.UnescapeDataString(model);
        return string.IsNullOrWhiteSpace(model) ? null : model;
    }

    private static string ResolveCodexOAuthGeminiTargetModel(string upstreamModel, TransparentProxyRoute route)
    {
        var requestedModel = StripRoutePrefix(upstreamModel.Trim(), route);
        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            foreach (var mapping in route.ModelMappings)
            {
                if (string.Equals(mapping.Name, requestedModel, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mapping.EffectiveAlias, requestedModel, StringComparison.OrdinalIgnoreCase))
                {
                    return mapping.Name;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(route.Model))
        {
            return route.Model.Trim();
        }

        return string.IsNullOrWhiteSpace(requestedModel) ? "gpt-5.5" : requestedModel;
    }

    private static string NormalizeRelativePathForUpstream(string pathOnly, string normalizedBasePath)
    {
        var normalizedPath = pathOnly.Trim('/').Trim();
        if (normalizedPath.StartsWith("backend-api/codex/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath["backend-api/codex/".Length..];
        }
        else if (normalizedPath.Equals("backend-api/codex", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = string.Empty;
        }
        else if (normalizedPath.StartsWith("codex/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath["codex/".Length..];
        }

        while (normalizedPath.StartsWith("v1/v1/", StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith("v1beta/v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase)
                ? normalizedPath["v1beta/".Length..]
                : normalizedPath[3..];
        }

        if (normalizedBasePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
            normalizedBasePath.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
        {
            var versionSegment = normalizedBasePath.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
                ? "v1beta/"
                : "v1/";
            while (normalizedPath.StartsWith(versionSegment, StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = normalizedPath[versionSegment.Length..];
            }
        }

        return normalizedPath;
    }

    private static string NormalizeRelativePath(string value)
    {
        var path = value.Trim('/').Trim();
        while (path.StartsWith("v1/v1/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("v1beta/v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            path = path.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase)
                ? path["v1beta/".Length..]
                : path[3..];
        }

        if (path.StartsWith("backend-api/codex/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["backend-api/codex/".Length..];
        }
        else if (path.Equals("backend-api/codex", StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
        }
        else if (path.StartsWith("codex/v1/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["codex/v1/".Length..];
        }

        if (path.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["v1beta/".Length..];
        }
        else if (path.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[3..];
        }

        return path;
    }

}

internal sealed record TransparentProxyModelSelectedBody(
    byte[] Body,
    string ClientModel,
    string UpstreamModel);
