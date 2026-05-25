using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.Services;

namespace RelayBench.Services;

internal sealed class TransparentProxyPayloadRuleService
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public byte[] Apply(
        byte[] body,
        TransparentProxyRoute route,
        string wireApi,
        string requestedModel,
        string upstreamModel,
        string pathAndQuery,
        string? sourceWireApi = null,
        NameValueCollection? sourceHeaders = null)
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
                if (!PayloadRuleMatches(
                    rule.Rule,
                    payloadObject,
                    wireApi,
                    requestedModel,
                    upstreamModel,
                    pathAndQuery,
                    sourceWireApi,
                    sourceHeaders))
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

        foreach (var action in new[] { "default", "default-raw", "default_raw", "override", "override-raw", "override_raw", "filter", "filter_private" })
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
        JsonObject payload,
        string wireApi,
        string requestedModel,
        string upstreamModel,
        string pathAndQuery,
        string? sourceWireApi,
        NameValueCollection? sourceHeaders)
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

        if (!PayloadMatchConditionsMatch(payload, rule) ||
            !PayloadNotMatchConditionsMatch(payload, rule) ||
            !PayloadExistConditionsMatch(payload, rule) ||
            !PayloadNotExistConditionsMatch(payload, rule))
        {
            return false;
        }

        var modelRules = ReadPayloadModelRules(rule);
        if (modelRules.Count == 0)
        {
            return true;
        }

        var candidates = new[] { requestedModel, StripModelSuffix(requestedModel), upstreamModel, StripModelSuffix(upstreamModel) }
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.Length > 0 &&
               modelRules.Any(modelRule =>
                   PayloadModelRuleMatches(modelRule, payload, wireApi, sourceWireApi, sourceHeaders, candidates));
    }

    private static bool PayloadModelRuleMatches(
        PayloadModelRule rule,
        JsonObject payload,
        string wireApi,
        string? sourceWireApi,
        NameValueCollection? sourceHeaders,
        IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) ||
            (!string.IsNullOrWhiteSpace(rule.Protocol) && !PayloadProtocolMatches(rule.Protocol, wireApi)) ||
            !PayloadFromProtocolMatches(ReadFromProtocol(rule.Rule), sourceWireApi) ||
            !PayloadHeadersMatch(sourceHeaders, rule.Rule))
        {
            return false;
        }

        return candidates.Any(candidate => WildcardMatch(candidate, rule.Name)) &&
               PayloadMatchConditionsMatch(payload, rule.Rule) &&
               PayloadNotMatchConditionsMatch(payload, rule.Rule) &&
               PayloadExistConditionsMatch(payload, rule.Rule) &&
               PayloadNotExistConditionsMatch(payload, rule.Rule);
    }

    private static IReadOnlyList<PayloadModelRule> ReadPayloadModelRules(JsonObject rule)
    {
        if (!rule.TryGetPropertyValue("models", out var node) || node is null)
        {
            return Array.Empty<PayloadModelRule>();
        }

        List<PayloadModelRule> rules = [];
        if (node is JsonValue)
        {
            foreach (var model in ReadStringArray(rule, "models"))
            {
                rules.Add(new PayloadModelRule(model, string.Empty, []));
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue modelValue)
                {
                    var model = modelValue.GetValue<string>()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        rules.Add(new PayloadModelRule(model, string.Empty, []));
                    }
                }
                else if (item is JsonObject modelRule)
                {
                    var name = ReadString(modelRule, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = ReadString(modelRule, "model");
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        rules.Add(new PayloadModelRule(name, ReadString(modelRule, "protocol"), modelRule));
                    }
                }
            }
        }

        return rules;
    }

    private static bool PayloadMatchConditionsMatch(JsonObject payload, JsonObject rule)
    {
        foreach (var condition in ReadPayloadConditionPairs(rule, "match"))
        {
            if (!TryGetJsonPath(payload, condition.Key, out var actual) ||
                !JsonValuesEqual(actual, condition.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PayloadNotMatchConditionsMatch(JsonObject payload, JsonObject rule)
    {
        foreach (var condition in ReadPayloadConditionPairs(rule, "not-match", "not_match", "notMatch"))
        {
            if (TryGetJsonPath(payload, condition.Key, out var actual) &&
                JsonValuesEqual(actual, condition.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PayloadExistConditionsMatch(JsonObject payload, JsonObject rule)
    {
        foreach (var path in ReadStringArray(rule, "exist").Concat(ReadStringArray(rule, "exists")))
        {
            if (!TryGetJsonPath(payload, path, out var value) || value is null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PayloadNotExistConditionsMatch(JsonObject payload, JsonObject rule)
    {
        foreach (var path in ReadStringArray(rule, "not-exist")
                     .Concat(ReadStringArray(rule, "not_exist"))
                     .Concat(ReadStringArray(rule, "notExists")))
        {
            if (TryGetJsonPath(payload, path, out var value) && value is not null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PayloadFromProtocolMatches(string pattern, string? sourceWireApi)
    {
        var normalizedPattern = NormalizePayloadProtocolName(pattern);
        if (string.IsNullOrWhiteSpace(normalizedPattern))
        {
            return true;
        }

        var normalizedSource = NormalizePayloadProtocolName(sourceWireApi ?? string.Empty);
        return !string.IsNullOrWhiteSpace(normalizedSource) &&
               string.Equals(normalizedPattern, normalizedSource, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PayloadHeadersMatch(NameValueCollection? headers, JsonObject rule)
    {
        if (rule["headers"] is not JsonObject headerRules || headerRules.Count == 0)
        {
            return true;
        }

        foreach (var headerRule in headerRules)
        {
            var key = headerRule.Key.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var pattern = headerRule.Value is JsonValue value && value.TryGetValue<string>(out var headerPattern)
                ? headerPattern?.Trim() ?? string.Empty
                : string.Empty;
            var values = ReadHeaderValues(headers, key);
            if (values.Count == 0 ||
                !values.Any(headerValue => WildcardMatch(headerValue, pattern)))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> ReadPayloadConditionPairs(JsonObject rule, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!rule.TryGetPropertyValue(propertyName, out var node) || node is null)
            {
                continue;
            }

            if (node is JsonObject obj)
            {
                foreach (var pair in obj)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key))
                    {
                        yield return new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value);
                    }
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var item in array.OfType<JsonObject>())
                {
                    foreach (var pair in item)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key))
                        {
                            yield return new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value);
                        }
                    }
                }
            }
        }
    }

    private static bool JsonValuesEqual(JsonNode? actual, JsonNode? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        return string.Equals(
            JsonSerializer.Serialize(actual, CompactJsonOptions),
            JsonSerializer.Serialize(expected, CompactJsonOptions),
            StringComparison.Ordinal);
    }

    private static void ApplyPayloadRule(JsonObject payload, string action, JsonObject rule)
    {
        var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (IsPrivateFieldFilterAction(normalizedAction))
        {
            RemovePrivateFields(payload, ReadPrivateFieldWhitelist(rule));
            return;
        }

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

        var isDefaultAction = IsDefaultPayloadRuleAction(normalizedAction);
        var isRawAction = IsRawPayloadRuleAction(normalizedAction);
        var sourceProperty = isDefaultAction || isRawAction ? "params" : normalizedAction;
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

            if (isDefaultAction && TryGetJsonPath(payload, pair.Key, out _))
            {
                continue;
            }

            if (!TryBuildPayloadRuleValue(pair.Value, isRawAction, out var value))
            {
                continue;
            }

            SetJsonPath(payload, pair.Key, value);
        }
    }

    private static bool IsDefaultPayloadRuleAction(string action)
        => action is "default" or "default-raw" or "default_raw";

    private static bool IsRawPayloadRuleAction(string action)
        => action is "default-raw" or "default_raw" or "override-raw" or "override_raw";

    private sealed record PayloadModelRule(string Name, string Protocol, JsonObject Rule);

    private static bool TryBuildPayloadRuleValue(JsonNode? source, bool isRawAction, out JsonNode? value)
    {
        value = null;
        if (source is null)
        {
            return false;
        }

        if (!isRawAction)
        {
            value = source.DeepClone();
            return true;
        }

        if (source is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var rawText))
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return false;
            }

            try
            {
                value = JsonNode.Parse(rawText);
                return true;
            }
            catch
            {
                return false;
            }
        }

        value = source.DeepClone();
        return true;
    }

    private static bool IsPrivateFieldFilterAction(string action)
        => action is "filter_private" or "filter-private" or "private_filter" or "private-fields";

    private static ISet<string> ReadPrivateFieldWhitelist(JsonObject rule)
        => ReadStringArray(rule, "whitelist")
            .Concat(ReadStringArray(rule, "allowlist"))
            .Concat(ReadStringArray(rule, "allowed"))
            .Concat(ReadStringArray(rule, "preserve"))
            .Where(static item => item.StartsWith('_'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int RemovePrivateFields(JsonNode? node, ISet<string> whitelist)
    {
        switch (node)
        {
            case JsonObject obj:
                var removed = 0;
                foreach (var property in obj.ToArray())
                {
                    if (ShouldRemovePrivateField(property.Key, whitelist))
                    {
                        obj.Remove(property.Key);
                        removed++;
                        continue;
                    }

                    removed += RemovePrivateFields(property.Value, whitelist);
                }

                return removed;
            case JsonArray array:
                var arrayRemoved = 0;
                foreach (var item in array)
                {
                    arrayRemoved += RemovePrivateFields(item, whitelist);
                }

                return arrayRemoved;
            default:
                return 0;
        }
    }

    private static bool ShouldRemovePrivateField(string propertyName, ISet<string> whitelist)
        => propertyName.StartsWith('_') && !whitelist.Contains(propertyName);

    private static bool PayloadProtocolMatches(string protocol, string wireApi)
    {
        var normalized = protocol.Trim();
        var normalizedProtocol = NormalizePayloadProtocolName(normalized);
        var normalizedWireApi = NormalizePayloadProtocolName(wireApi);
        if (!string.IsNullOrWhiteSpace(normalizedProtocol) &&
            string.Equals(normalizedProtocol, normalizedWireApi, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.Equals(wireApi, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(DisplayWireApi(wireApi), StringComparison.OrdinalIgnoreCase) ||
               (normalized.Equals("responses", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(wireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal)) ||
               (normalized.Equals("anthropic", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(wireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal)) ||
               (normalized.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(wireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal)) ||
               (normalized.Equals("openai", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(wireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal));
    }

    private static string ReadFromProtocol(JsonObject rule)
    {
        var protocol = ReadString(rule, "from-protocol");
        if (!string.IsNullOrWhiteSpace(protocol))
        {
            return protocol;
        }

        protocol = ReadString(rule, "from_protocol");
        return string.IsNullOrWhiteSpace(protocol)
            ? ReadString(rule, "fromProtocol")
            : protocol;
    }

    private static IReadOnlyList<string> ReadHeaderValues(NameValueCollection? headers, string key)
    {
        if (headers is null)
        {
            return Array.Empty<string>();
        }

        List<string> values = [];
        foreach (var headerKey in headers.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(headerKey) ||
                !string.Equals(headerKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            values.AddRange(headers.GetValues(headerKey) ?? Array.Empty<string>());
        }

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static string NormalizePayloadProtocolName(string protocol)
    {
        var normalized = (protocol ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => string.Empty,
            "openai-response" or "openai-responses" or "response" or "responses" => "responses",
            "chat" or "chat-completions" or "chat_completions" or "openai-chat" or "openai_chat" or "openai" => "openai",
            "gemini" or "gemini-cli" or "gemini_native" or "gemini-native" => "gemini",
            "claude" or "anthropic" or "anthropic-messages" or "anthropic_messages" => "claude",
            _ => normalized
        };
    }

    private static string DisplayWireApi(string wireApi)
        => wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic Messages",
            _ => "OpenAI Chat"
        };

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

    private static void SetJsonPath(JsonObject root, string path, JsonNode? value)
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
}
