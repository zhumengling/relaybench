using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

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
                            pathAndQuery);
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
        bool preferJsonStreamExtraction)
    {
        var wireApi = InferWireApiFromPath(pathAndQuery);
        var headers = MergeHeaders(route.Headers, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        requestBody = _payloadRuleService.Apply(
            requestBody,
            route,
            wireApi,
            clientModel,
            upstreamModel,
            pathAndQuery);
        var body = _promptCacheOptimizer.Apply(
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

}

internal sealed record TransparentProxyModelSelectedBody(
    byte[] Body,
    string ClientModel,
    string UpstreamModel);
