using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.Services;

internal sealed class TransparentProxyResponseNormalizationService
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public TransparentProxyNormalizedChatResponse? TryBuildNormalizedChatJson(
        byte[] upstreamBytes,
        string responseModel,
        string wireApi,
        IReadOnlyDictionary<string, string>? toolNameAliases = null)
    {
        var upstreamText = Encoding.UTF8.GetString(upstreamBytes);
        var model = ResolveResponseModel(upstreamText, responseModel);
        var usage = TryExtractUsageNode(upstreamText);
        var images = ExtractInlineDataImages(upstreamText);
        var reasoningText = TryExtractReasoningText(upstreamText);
        var assistantText = SuppressResponsesReasoningSummaryAssistantText(
            upstreamText,
            ModelResponseTextExtractor.TryExtractAssistantText(upstreamText),
            reasoningText);
        var hasToolCalls = TryExtractToolCalls(upstreamText, toolNameAliases, out var toolCalls);
        if (hasToolCalls &&
            string.IsNullOrEmpty(assistantText) &&
            images.Count == 0)
        {
            return new TransparentProxyNormalizedChatResponse(
                BuildOpenAiChatToolCallBytes(
                    toolCalls,
                    model,
                    wireApi,
                    usage,
                    reasoningText),
                string.Empty,
                reasoningText ?? string.Empty,
                toolCalls,
                Array.Empty<TransparentProxyNormalizedImage>(),
                usage);
        }

        if (assistantText is not null || images.Count > 0 || !string.IsNullOrWhiteSpace(reasoningText))
        {
            return new TransparentProxyNormalizedChatResponse(
                BuildOpenAiChatCompletionBytes(
                    assistantText ?? string.Empty,
                    model,
                    wireApi,
                    usage,
                    images,
                    reasoningText,
                    hasToolCalls ? toolCalls : null),
                assistantText ?? string.Empty,
                reasoningText ?? string.Empty,
                hasToolCalls ? toolCalls : Array.Empty<TransparentProxyNormalizedToolCall>(),
                images,
                usage);
        }

        return null;
    }

    public string BuildOpenAiChatCompletionTerminalChunk(
        string model,
        string wireApi,
        string streamId,
        string accumulatedText,
        string finishReason = "stop",
        JsonNode? usage = null)
    {
        var normalizedUsage = usage?.DeepClone() ?? new JsonObject
        {
            ["prompt_tokens"] = 0,
            ["completion_tokens"] = Math.Max(0, TokenCountEstimator.EstimateOutputTokens(accumulatedText)),
            ["total_tokens"] = Math.Max(0, TokenCountEstimator.EstimateOutputTokens(accumulatedText))
        };
        var root = new JsonObject
        {
            ["id"] = streamId,
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = finishReason
                }
            },
            ["usage"] = normalizedUsage,
            ["relaybench"] = new JsonObject
            {
                ["upstream_wire_api"] = wireApi
            }
        };

        return root.ToJsonString(CompactJsonOptions);
    }

    internal byte[] BuildOpenAiChatCompletionJson(
        string content,
        string model,
        string wireApi,
        JsonNode? usage,
        IReadOnlyList<TransparentProxyNormalizedImage>? images = null,
        string? reasoningText = null,
        IReadOnlyList<TransparentProxyNormalizedToolCall>? toolCalls = null)
        => BuildOpenAiChatCompletionBytes(content, model, wireApi, usage, images, reasoningText, toolCalls);

    internal byte[] BuildOpenAiChatToolCallJson(
        IReadOnlyList<TransparentProxyNormalizedToolCall> toolCalls,
        string model,
        string wireApi,
        JsonNode? usage,
        string? reasoningText = null)
        => BuildOpenAiChatToolCallBytes(toolCalls, model, wireApi, usage, reasoningText);

    public string BuildOpenAiChatToolCallChunk(
        int toolCallIndex,
        string? id,
        string? name,
        string argumentsDelta,
        string model,
        string wireApi,
        string streamId)
    {
        var function = new JsonObject
        {
            ["arguments"] = argumentsDelta
        };
        if (!string.IsNullOrWhiteSpace(name))
        {
            function["name"] = name;
        }

        var toolCall = new JsonObject
        {
            ["index"] = Math.Max(0, toolCallIndex),
            ["function"] = function
        };
        if (!string.IsNullOrWhiteSpace(id))
        {
            toolCall["id"] = id;
            toolCall["type"] = "function";
        }

        var root = new JsonObject
        {
            ["id"] = streamId,
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["tool_calls"] = new JsonArray { toolCall }
                    },
                    ["finish_reason"] = null
                }
            },
            ["relaybench"] = new JsonObject
            {
                ["upstream_wire_api"] = wireApi
            }
        };

        return root.ToJsonString(CompactJsonOptions);
    }

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

    public string BuildOpenAiChatReasoningChunk(
        string delta,
        string model,
        string wireApi,
        string streamId)
    {
        var root = new JsonObject
        {
            ["id"] = streamId,
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["reasoning_content"] = delta
                    },
                    ["finish_reason"] = null
                }
            },
            ["relaybench"] = new JsonObject
            {
                ["upstream_wire_api"] = wireApi
            }
        };

        return root.ToJsonString(CompactJsonOptions);
    }

    public string BuildOpenAiChatImageChunk(
        int imageIndex,
        TransparentProxyNormalizedImage image,
        string model,
        string wireApi,
        string streamId)
    {
        var root = new JsonObject
        {
            ["id"] = streamId,
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["images"] = new JsonArray
                        {
                            BuildOpenAiChatImagePayload(image, imageIndex)
                        }
                    },
                    ["finish_reason"] = null
                }
            },
            ["relaybench"] = new JsonObject
            {
                ["upstream_wire_api"] = wireApi
            }
        };

        return root.ToJsonString(CompactJsonOptions);
    }

    internal static JsonNode? TryExtractUsageNode(string upstreamText)
    {
        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (document.RootElement.TryGetProperty("usage", out var usage))
            {
                return BuildOpenAiChatUsage(usage);
            }

            if (TryReadObjectPath(document.RootElement, out usage, "response", "usage") ||
                TryReadObjectPath(document.RootElement, out usage, "message", "usage"))
            {
                return BuildOpenAiChatUsage(usage);
            }

            if (document.RootElement.TryGetProperty("usageMetadata", out var usageMetadata) ||
                document.RootElement.TryGetProperty("usage_metadata", out usageMetadata) ||
                TryReadObjectPath(document.RootElement, out usageMetadata, "response", "usageMetadata") ||
                TryReadObjectPath(document.RootElement, out usageMetadata, "response", "usage_metadata"))
            {
                return BuildOpenAiChatUsageFromGemini(usageMetadata);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<TransparentProxyNormalizedImage> ExtractInlineDataImages(string upstreamText)
    {
        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            List<TransparentProxyNormalizedImage> images = [];
            ExtractGeminiInlineDataImages(document.RootElement, images);
            ExtractResponsesImageGenerationImages(document.RootElement, images);
            return images;
        }
        catch
        {
            return Array.Empty<TransparentProxyNormalizedImage>();
        }
    }

    internal static string? TryExtractReasoningText(string upstreamText)
    {
        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            StringBuilder builder = new();
            ExtractReasoningText(document.RootElement, builder);
            return builder.Length > 0 ? builder.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? SuppressResponsesReasoningSummaryAssistantText(
        string upstreamText,
        string? assistantText,
        string? reasoningText)
    {
        if (string.IsNullOrEmpty(assistantText) ||
            string.IsNullOrEmpty(reasoningText) ||
            !string.Equals(assistantText, reasoningText, StringComparison.Ordinal))
        {
            return assistantText;
        }

        return IsResponsesReasoningAndToolOnlyOutput(upstreamText) ? string.Empty : assistantText;
    }

    private static bool IsResponsesReasoningAndToolOnlyOutput(string upstreamText)
    {
        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            if (!document.RootElement.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var sawReasoning = false;
            var sawToolCall = false;
            foreach (var item in output.EnumerateArray())
            {
                var type = TryReadString(item, "type");
                if (string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
                {
                    sawReasoning = true;
                    continue;
                }

                if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    sawToolCall = true;
                    continue;
                }

                return false;
            }

            return sawReasoning && sawToolCall;
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractReasoningText(JsonElement root, StringBuilder builder)
    {
        if (TryReadObjectPath(root, out var response, "response"))
        {
            ExtractReasoningText(response, builder);
        }

        ExtractGeminiThoughtText(root, builder);
        ExtractOpenAiChatReasoningText(root, builder);
        ExtractResponsesReasoningText(root, builder);
    }

    private static void ExtractGeminiThoughtText(JsonElement root, StringBuilder builder)
    {
        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (!IsTruthy(part, "thought"))
                {
                    continue;
                }

                var text = TryReadString(part, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    builder.Append(text);
                }
            }
        }
    }

    private static void ExtractOpenAiChatReasoningText(JsonElement root, StringBuilder builder)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message))
            {
                AppendReasoningString(message, "reasoning_content", builder);
            }

            if (choice.TryGetProperty("delta", out var delta))
            {
                AppendReasoningString(delta, "reasoning_content", builder);
            }
        }
    }

    private static void ExtractResponsesReasoningText(JsonElement root, StringBuilder builder)
    {
        if (root.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            string.Equals(type.GetString(), "response.reasoning_summary_text.delta", StringComparison.OrdinalIgnoreCase))
        {
            AppendReasoningString(root, "delta", builder);
        }

        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in output.EnumerateArray())
        {
            var itemType = TryReadString(item, "type");
            if (!string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppendReasoningString(item, "text", builder);
            AppendReasoningString(item, "summary_text", builder);
            if (item.TryGetProperty("summary", out var summary) &&
                summary.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in summary.EnumerateArray())
                {
                    AppendReasoningString(part, "text", builder);
                }
            }
        }
    }

    private static void AppendReasoningString(JsonElement element, string propertyName, StringBuilder builder)
    {
        var text = TryReadString(element, propertyName);
        if (!string.IsNullOrEmpty(text))
        {
            builder.Append(text);
        }
    }

    private static void ExtractGeminiInlineDataImages(
        JsonElement root,
        List<TransparentProxyNormalizedImage> images)
    {
        if (TryReadObjectPath(root, out var response, "response"))
        {
            ExtractGeminiInlineDataImages(response, images);
        }

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (!TryReadProperty(part, out var inlineData, "inlineData", "inline_data") ||
                    inlineData.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var data = TryReadString(inlineData, "data");
                if (string.IsNullOrWhiteSpace(data))
                {
                    continue;
                }

                var mimeType = TryReadString(inlineData, "mimeType") ??
                               TryReadString(inlineData, "mime_type") ??
                               "image/png";
                if (string.IsNullOrWhiteSpace(mimeType))
                {
                    mimeType = "image/png";
                }

                images.Add(new TransparentProxyNormalizedImage(mimeType, data));
            }
        }
    }

    private static void ExtractResponsesImageGenerationImages(
        JsonElement root,
        List<TransparentProxyNormalizedImage> images)
    {
        if (TryReadObjectPath(root, out var response, "response"))
        {
            ExtractResponsesImageGenerationImages(response, images);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (string.Equals(
                TryReadString(root, "type"),
                "response.image_generation_call.partial_image",
                StringComparison.OrdinalIgnoreCase))
        {
            AddResponsesImageGenerationImage(
                images,
                TryReadString(root, "partial_image_b64"),
                TryReadString(root, "output_format"));
        }

        if (string.Equals(
                TryReadString(root, "type"),
                "response.output_item.done",
                StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("item", out var item))
        {
            AddResponsesImageGenerationItem(item, images);
        }

        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var outputItem in output.EnumerateArray())
        {
            AddResponsesImageGenerationItem(outputItem, images);
        }
    }

    private static void AddResponsesImageGenerationItem(
        JsonElement item,
        List<TransparentProxyNormalizedImage> images)
    {
        if (!string.Equals(
                TryReadString(item, "type"),
                "image_generation_call",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AddResponsesImageGenerationImage(
            images,
            TryReadString(item, "result") ?? TryReadString(item, "partial_image_b64"),
            TryReadString(item, "output_format"));
    }

    private static void AddResponsesImageGenerationImage(
        List<TransparentProxyNormalizedImage> images,
        string? data,
        string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        images.Add(new TransparentProxyNormalizedImage(
            ResolveResponsesImageGenerationMimeType(outputFormat, data),
            StripDataUrlPrefix(data)));
    }

    private static string ResolveResponsesImageGenerationMimeType(string? outputFormat, string data)
    {
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separatorIndex = data.IndexOf(';', StringComparison.Ordinal);
            if (separatorIndex > "data:".Length)
            {
                return data["data:".Length..separatorIndex];
            }
        }

        return outputFormat?.Trim().ToLowerInvariant() switch
        {
            "image/png" or "png" => "image/png",
            "image/jpeg" or "image/jpg" or "jpeg" or "jpg" => "image/jpeg",
            "image/webp" or "webp" => "image/webp",
            "image/gif" or "gif" => "image/gif",
            _ => "image/png"
        };
    }

    private static string StripDataUrlPrefix(string data)
    {
        if (!data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return data;
        }

        var commaIndex = data.IndexOf(',', StringComparison.Ordinal);
        return commaIndex >= 0 && commaIndex + 1 < data.Length
            ? data[(commaIndex + 1)..]
            : data;
    }

    private static bool TryReadObjectPath(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryReadProperty(JsonElement element, out JsonElement value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsTruthy(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind is JsonValueKind.True;

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
        var reasoningTokens = Math.Max(
            Math.Max(
                TryReadIntPath(usage, "completion_tokens_details", "reasoning_tokens"),
                TryReadIntPath(usage, "output_tokens_details", "reasoning_tokens")),
            TryReadInt(usage, "reasoning_tokens"));
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

        if (reasoningTokens > 0)
        {
            normalized["completion_tokens_details"] = new JsonObject
            {
                ["reasoning_tokens"] = reasoningTokens
            };
        }

        return normalized;
    }

    private static JsonObject BuildOpenAiChatUsageFromGemini(JsonElement usage)
    {
        var promptTokens = TryReadInt(usage, "promptTokenCount");
        var candidateTokens = TryReadInt(usage, "candidatesTokenCount");
        var reasoningTokens = TryReadInt(usage, "thoughtsTokenCount");
        var totalTokens = TryReadInt(usage, "totalTokenCount");
        var completionTokens = BuildGeminiCompletionTokenCount(promptTokens, candidateTokens, reasoningTokens, totalTokens);
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

        var cachedTokens = TryReadInt(usage, "cachedContentTokenCount");
        if (cachedTokens > 0)
        {
            normalized["prompt_tokens_details"] = new JsonObject
            {
                ["cached_tokens"] = cachedTokens
            };
        }

        if (reasoningTokens > 0)
        {
            normalized["completion_tokens_details"] = new JsonObject
            {
                ["reasoning_tokens"] = reasoningTokens
            };
        }

        return normalized;
    }

    private static int BuildGeminiCompletionTokenCount(
        int promptTokens,
        int candidateTokens,
        int reasoningTokens,
        int totalTokens)
    {
        var visibleAndReasoningTokens = Math.Min(1_000_000, candidateTokens + reasoningTokens);
        if (totalTokens > 0 && promptTokens > 0)
        {
            return Math.Max(visibleAndReasoningTokens, Math.Max(0, totalTokens - promptTokens));
        }

        return visibleAndReasoningTokens > 0
            ? visibleAndReasoningTokens
            : Math.Max(0, totalTokens - promptTokens);
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

    private static bool TryExtractToolCalls(
        string upstreamText,
        IReadOnlyDictionary<string, string>? toolNameAliases,
        out IReadOnlyList<TransparentProxyNormalizedToolCall> toolCalls)
    {
        toolCalls = Array.Empty<TransparentProxyNormalizedToolCall>();
        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            List<TransparentProxyNormalizedToolCall> calls = [];
            ExtractOpenAiChatToolCalls(document.RootElement, calls, toolNameAliases);
            ExtractResponsesToolCalls(document.RootElement, calls, toolNameAliases);
            ExtractAnthropicToolCalls(document.RootElement, calls, toolNameAliases);
            ExtractGeminiToolCalls(document.RootElement, calls, toolNameAliases);
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

    private static void ExtractOpenAiChatToolCalls(
        JsonElement root,
        List<TransparentProxyNormalizedToolCall> calls,
        IReadOnlyDictionary<string, string>? toolNameAliases)
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

            var name = ResolveToolNameAlias(TryReadString(function, "name"), toolNameAliases);
            var arguments = TryReadString(function, "arguments") ?? "{}";
            if (!string.IsNullOrWhiteSpace(name))
            {
                calls.Add(new TransparentProxyNormalizedToolCall(id, name, arguments));
            }
        }
    }

    private static void ExtractResponsesToolCalls(
        JsonElement root,
        List<TransparentProxyNormalizedToolCall> calls,
        IReadOnlyDictionary<string, string>? toolNameAliases)
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
            var name = ResolveToolNameAlias(TryReadString(item, "name"), toolNameAliases);
            var arguments = TryReadString(item, "arguments") ?? "{}";
            if (!string.IsNullOrWhiteSpace(name))
            {
                calls.Add(new TransparentProxyNormalizedToolCall(id, name, arguments));
            }
        }
    }

    private static void ExtractAnthropicToolCalls(
        JsonElement root,
        List<TransparentProxyNormalizedToolCall> calls,
        IReadOnlyDictionary<string, string>? toolNameAliases)
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
            var name = ResolveToolNameAlias(TryReadString(item, "name"), toolNameAliases);
            var arguments = item.TryGetProperty("input", out var input) ? input.GetRawText() : "{}";
            if (!string.IsNullOrWhiteSpace(name))
            {
                calls.Add(new TransparentProxyNormalizedToolCall(id, name, arguments));
            }
        }
    }

    private static void ExtractGeminiToolCalls(
        JsonElement root,
        List<TransparentProxyNormalizedToolCall> calls,
        IReadOnlyDictionary<string, string>? toolNameAliases)
    {
        if (TryReadObjectPath(root, out var response, "response"))
        {
            ExtractGeminiToolCalls(response, calls, toolNameAliases);
            return;
        }

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("functionCall", out var functionCall))
                {
                    continue;
                }

                var name = ResolveToolNameAlias(TryReadString(functionCall, "name"), toolNameAliases);
                var arguments = functionCall.TryGetProperty("args", out var args)
                    ? args.GetRawText()
                    : "{}";
                if (!string.IsNullOrWhiteSpace(name))
                {
                    calls.Add(new TransparentProxyNormalizedToolCall($"call_{calls.Count + 1}", name, arguments));
                }
            }
        }
    }

    private static string? TryReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ResolveToolNameAlias(
        string? name,
        IReadOnlyDictionary<string, string>? toolNameAliases)
        => !string.IsNullOrWhiteSpace(name) &&
           toolNameAliases is not null &&
           toolNameAliases.TryGetValue(name, out var original)
            ? original
            : name;

    private static byte[] BuildOpenAiChatCompletionBytes(
        string content,
        string model,
        string wireApi,
        JsonNode? usage,
        IReadOnlyList<TransparentProxyNormalizedImage>? images = null,
        string? reasoningText = null,
        IReadOnlyList<TransparentProxyNormalizedToolCall>? toolCalls = null)
    {
        var root = BuildOpenAiChatRoot(model, wireApi, usage);
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content
        };
        if (!string.IsNullOrWhiteSpace(reasoningText))
        {
            message["reasoning_content"] = reasoningText;
        }

        if (images is { Count: > 0 })
        {
            message["images"] = BuildOpenAiChatImageArray(images);
        }

        var hasToolCalls = toolCalls is { Count: > 0 };
        if (toolCalls is { Count: > 0 } normalizedToolCalls)
        {
            message["tool_calls"] = BuildOpenAiChatToolCallArray(normalizedToolCalls);
        }

        root["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = hasToolCalls ? "tool_calls" : "stop"
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(root, CompactJsonOptions);
    }

    private static JsonArray BuildOpenAiChatToolCallArray(IReadOnlyList<TransparentProxyNormalizedToolCall> toolCalls)
    {
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

        return toolCallArray;
    }

    private static JsonArray BuildOpenAiChatImageArray(IReadOnlyList<TransparentProxyNormalizedImage> images)
    {
        JsonArray array = [];
        for (var index = 0; index < images.Count; index++)
        {
            array.Add(BuildOpenAiChatImagePayload(images[index], index));
        }

        return array;
    }

    private static JsonObject BuildOpenAiChatImagePayload(TransparentProxyNormalizedImage image, int index)
        => new()
        {
            ["type"] = "image_url",
            ["index"] = Math.Max(0, index),
            ["image_url"] = new JsonObject
            {
                ["url"] = $"data:{image.MimeType};base64,{image.Base64Data}"
            }
        };

    private static byte[] BuildOpenAiChatToolCallBytes(
        IReadOnlyList<TransparentProxyNormalizedToolCall> toolCalls,
        string model,
        string wireApi,
        JsonNode? usage,
        string? reasoningText = null)
    {
        var root = BuildOpenAiChatRoot(model, wireApi, usage);
        var toolCallArray = BuildOpenAiChatToolCallArray(toolCalls);

        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = toolCallArray
        };
        if (!string.IsNullOrWhiteSpace(reasoningText))
        {
            message["reasoning_content"] = reasoningText;
        }

        root["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["index"] = 0,
                ["message"] = message,
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
            return TryReadStringProperty(document.RootElement, "model") ??
                   TryReadStringPath(document.RootElement, "response", "model") ??
                   TryReadStringProperty(document.RootElement, "modelVersion") ??
                   TryReadStringPath(document.RootElement, "response", "modelVersion") ??
                   "relaybench-proxy";
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

    private static string? TryReadStringPath(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : null;
    }

    internal sealed record TransparentProxyNormalizedToolCall(string Id, string Name, string Arguments);

    internal sealed record TransparentProxyNormalizedImage(string MimeType, string Base64Data);
}

internal sealed record TransparentProxyNormalizedChatResponse(
    byte[] Body,
    string AssistantText,
    string ReasoningText,
    IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedToolCall> ToolCalls,
    IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedImage> Images,
    JsonNode? Usage);
