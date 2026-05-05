using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyResponseNormalizationService
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

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

    private static JsonNode? TryExtractUsageNode(string upstreamText)
    {
        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("usage", out var usage)
                ? BuildOpenAiChatUsage(usage)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject BuildOpenAiChatUsage(JsonElement usage)
    {
        var promptTokens = Math.Max(
            TryReadInt(usage, "prompt_tokens"),
            TryReadInt(usage, "input_tokens"));
        var completionTokens = Math.Max(
            TryReadInt(usage, "completion_tokens"),
            TryReadInt(usage, "output_tokens"));
        var cachedTokens = Math.Max(
            Math.Max(
                TryReadIntPath(usage, "prompt_tokens_details", "cached_tokens"),
                TryReadIntPath(usage, "input_tokens_details", "cached_tokens")),
            TryReadInt(usage, "cache_read_input_tokens"));
        var cacheCreationTokens = TryReadInt(usage, "cache_creation_input_tokens");
        if (TryReadInt(usage, "prompt_tokens") == 0 &&
            TryReadIntPath(usage, "input_tokens_details", "cached_tokens") == 0)
        {
            promptTokens += cachedTokens + cacheCreationTokens;
        }

        var totalTokens = TryReadInt(usage, "total_tokens");
        if (totalTokens <= 0)
        {
            totalTokens = promptTokens + completionTokens;
        }

        var normalized = new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = totalTokens
        };
        if (cachedTokens > 0)
        {
            normalized["prompt_tokens_details"] = new JsonObject
            {
                ["cached_tokens"] = cachedTokens
            };
        }

        return normalized;
    }

    private static int TryReadIntPath(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return 0;
            }
        }

        return TryReadInt(current);
    }

    private static int TryReadInt(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property)
            ? TryReadInt(property)
            : 0;

    private static int TryReadInt(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var value) => Math.Max(0, value),
            JsonValueKind.String when int.TryParse(element.GetString(), out var value) => Math.Max(0, value),
            _ => 0
        };

    private static bool TryExtractToolCalls(string upstreamText, out IReadOnlyList<TransparentProxyNormalizedToolCall> toolCalls)
    {
        toolCalls = Array.Empty<TransparentProxyNormalizedToolCall>();
        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            List<TransparentProxyNormalizedToolCall> calls = [];
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

    private static void ExtractOpenAiChatToolCalls(JsonElement root, List<TransparentProxyNormalizedToolCall> calls)
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
                calls.Add(new TransparentProxyNormalizedToolCall(id, name, arguments));
            }
        }
    }

    private static void ExtractResponsesToolCalls(JsonElement root, List<TransparentProxyNormalizedToolCall> calls)
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
                calls.Add(new TransparentProxyNormalizedToolCall(id, name, arguments));
            }
        }
    }

    private static void ExtractAnthropicToolCalls(JsonElement root, List<TransparentProxyNormalizedToolCall> calls)
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
                calls.Add(new TransparentProxyNormalizedToolCall(id, name, arguments));
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
        IReadOnlyList<TransparentProxyNormalizedToolCall> toolCalls,
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

    private static string? TryReadStringProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed record TransparentProxyNormalizedToolCall(string Id, string Name, string Arguments);
}

internal sealed record TransparentProxyNormalizedChatResponse(byte[] Body, string AssistantText);
