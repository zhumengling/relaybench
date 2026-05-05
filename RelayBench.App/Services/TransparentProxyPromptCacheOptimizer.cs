using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyPromptCacheOptimizer
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public byte[] Apply(
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

            NormalizeAnthropicCacheControlTtl(obj);
            var budget = Math.Max(0, 4 - CountAnthropicCacheControls(obj));
            if (budget > 0 && TryInjectToolsCacheControl(obj))
            {
                budget--;
            }

            if (budget > 0 && TryInjectSystemCacheControl(obj))
            {
                budget--;
            }

            if (budget > 0 && TryInjectLastAssistantCacheControl(obj))
            {
                budget--;
            }

            if (budget > 0 && TryInjectSecondToLastUserCacheControl(obj))
            {
                budget--;
            }

            NormalizeAnthropicCacheControlTtl(obj);
            EnforceAnthropicCacheControlLimit(obj, 4);
            return JsonSerializer.SerializeToUtf8Bytes(obj, CompactJsonOptions);
        }
        catch
        {
            return body;
        }
    }

    private static bool TryInjectToolsCacheControl(JsonObject root)
    {
        if (root["tools"] is not JsonArray tools || tools.Count == 0)
        {
            return false;
        }

        if (tools.LastOrDefault(static tool => tool is JsonObject) is JsonObject lastTool)
        {
            if (lastTool.ContainsKey("cache_control"))
            {
                return false;
            }

            lastTool["cache_control"] = BuildEphemeralCacheControl();
            return true;
        }

        return false;
    }

    private static bool TryInjectSystemCacheControl(JsonObject root)
    {
        var system = root["system"];
        if (system is null)
        {
            return false;
        }

        if (system is JsonArray systemBlocks)
        {
            if (systemBlocks.LastOrDefault(static item => item is JsonObject) is not JsonObject lastSystemBlock ||
                lastSystemBlock.ContainsKey("cache_control"))
            {
                return false;
            }

            lastSystemBlock["cache_control"] = BuildEphemeralCacheControl();
            return true;
        }

        if (system is JsonValue value)
        {
            var text = value.GetValue<string>();
            if (string.IsNullOrEmpty(text))
            {
                return false;
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
            return true;
        }

        return false;
    }

    private static bool TryInjectLastAssistantCacheControl(JsonObject root)
    {
        if (root["messages"] is not JsonArray messages)
        {
            return false;
        }

        var assistant = messages
            .OfType<JsonObject>()
            .Reverse()
            .FirstOrDefault(static message =>
                message.TryGetPropertyValue("role", out var roleNode) &&
                roleNode is JsonValue roleValue &&
                string.Equals(roleValue.GetValue<string>(), "assistant", StringComparison.OrdinalIgnoreCase));
        return assistant is not null && TryInjectLastTextContentBlock(assistant, skipThinkingBlocks: true);
    }

    private static bool TryInjectSecondToLastUserCacheControl(JsonObject root)
    {
        if (root["messages"] is not JsonArray messages)
        {
            return false;
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
            return false;
        }

        var secondToLastUserIndex = userMessageIndexes[^2];
        if (messages[secondToLastUserIndex] is not JsonObject userMessage)
        {
            return false;
        }

        return TryInjectLastTextContentBlock(userMessage, skipThinkingBlocks: false);
    }

    private static bool TryInjectLastTextContentBlock(JsonObject message, bool skipThinkingBlocks)
    {
        var contentNode = message["content"];
        if (contentNode is JsonArray contentBlocks)
        {
            var lastContentBlock = contentBlocks
                .OfType<JsonObject>()
                .Reverse()
                .FirstOrDefault(item =>
                {
                    if (!skipThinkingBlocks)
                    {
                        return true;
                    }

                    var type = item.TryGetPropertyValue("type", out var typeNode) &&
                               typeNode is JsonValue typeValue
                        ? typeValue.GetValue<string>()
                        : string.Empty;
                    return !string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(type, "redacted_thinking", StringComparison.OrdinalIgnoreCase);
                });
            if (lastContentBlock is null || lastContentBlock.ContainsKey("cache_control"))
            {
                return false;
            }

            lastContentBlock["cache_control"] = BuildEphemeralCacheControl();
            return true;
        }

        if (contentNode is JsonValue contentValue)
        {
            var text = contentValue.GetValue<string>();
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            message["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = BuildEphemeralCacheControl()
                }
            };
            return true;
        }

        return false;
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
}
