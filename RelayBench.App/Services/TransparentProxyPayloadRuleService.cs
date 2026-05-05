using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

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
                if (!PayloadRuleMatches(rule.Rule, wireApi, requestedModel, upstreamModel, pathAndQuery))
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
}
