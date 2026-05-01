using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;
using RelayBench.Core.Support;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static HttpClient CreateClient(Uri baseUri, ProxyEndpointSettings settings)
    {
        HttpClientHandler handler = new();
        if (settings.IgnoreTlsErrors)
        {
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.2");
        return client;
    }

    private static Uri EnsureTrailingSlash(Uri baseUri)
    {
        if (baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
        {
            return baseUri;
        }

        return new Uri($"{baseUri.AbsoluteUri}/", UriKind.Absolute);
    }

    private static string BuildApiPath(Uri baseUri, string endpoint)
    {
        var normalizedPath = baseUri.AbsolutePath.TrimEnd('/');
        if (normalizedPath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        return $"v1/{endpoint}";
    }

    private static IReadOnlyList<string> ParseModelIds(string json)
        => ParseModelCatalogItems(json)
            .Select(static item => item.Id)
            .ToArray();

    private static IReadOnlyList<ProxyModelCatalogItem> ParseModelCatalogItems(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProxyModelCatalogItem>();
        }

        return data.EnumerateArray()
            .Select(element =>
            {
                if (element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                {
                    var id = idElement.GetString();
                    return string.IsNullOrWhiteSpace(id)
                        ? null
                        : new ProxyModelCatalogItem(id, TryExtractModelContextWindow(element));
                }

                return null;
            })
            .Where(static item => item is not null)
            .DistinctBy(static item => item!.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item!.Id, StringComparer.OrdinalIgnoreCase)
            .Cast<ProxyModelCatalogItem>()
            .ToArray();
    }

    private static int? TryExtractModelContextWindow(JsonElement element)
    {
        string[] contextFieldNames =
        [
            "context_window",
            "context_length",
            "max_context_window",
            "max_context_length",
            "max_model_len",
            "max_sequence_length",
            "max_seq_len",
            "max_position_embeddings",
            "max_total_tokens",
            "input_token_limit",
            "max_input_tokens",
            "n_ctx",
            "ctx_size"
        ];

        return TryExtractModelContextWindow(element, contextFieldNames);
    }

    private static int? TryExtractModelContextWindow(JsonElement element, IReadOnlyCollection<string> contextFieldNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (contextFieldNames.Contains(property.Name) &&
                TryReadPositiveInt(property.Value, out var value))
            {
                return value;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object)
            {
                var nested = TryExtractModelContextWindow(property.Value, contextFieldNames);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool TryReadPositiveInt(JsonElement element, out int value)
    {
        value = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var longValue))
                {
                    return false;
                }

                return TryNormalizeContextWindow(longValue, out value);
            case JsonValueKind.String:
                var raw = element.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return false;
                }

                var normalized = raw.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
                if (normalized.EndsWith('k') || normalized.EndsWith('K'))
                {
                    return double.TryParse(normalized[..^1], out var thousands) &&
                           TryNormalizeContextWindow((long)Math.Round(thousands * 1000d), out value);
                }

                if (normalized.EndsWith('m') || normalized.EndsWith('M'))
                {
                    return double.TryParse(normalized[..^1], out var millions) &&
                           TryNormalizeContextWindow((long)Math.Round(millions * 1_000_000d), out value);
                }

                normalized = normalized.Replace(",", string.Empty, StringComparison.Ordinal);
                return long.TryParse(normalized, out var parsed) &&
                       TryNormalizeContextWindow(parsed, out value);
            default:
                return false;
        }
    }

    private static bool TryNormalizeContextWindow(long rawValue, out int value)
    {
        value = 0;
        if (rawValue is < 1024 or > 20_000_000)
        {
            return false;
        }

        value = (int)rawValue;
        return true;
    }

    private const int GlobalChatProbeMaxTokens = 128;
    private const int GlobalResponsesProbeMaxOutputTokens = 128;

    private static string BuildChatPayload(string model, bool stream)
        => ProxyProbePayloadFactory.BuildChatPayload(model, stream);

    private static string BuildResponsesPayload(string model)
        => ProxyProbePayloadFactory.BuildResponsesPayload(model);

    private static string BuildStructuredOutputPayload(string model)
        => ProxyProbePayloadFactory.BuildStructuredOutputPayload(model);

    private static string BuildAnthropicMessagesPayload(string model, bool stream = false)
        => ProxyProbePayloadFactory.BuildAnthropicMessagesPayload(model, stream);

    private static int GetChatProbeMaxTokens(string model)
        => GlobalChatProbeMaxTokens;

    private static string BuildLongStreamingPayload(string model, int segmentCount)
        => ProxyProbePayloadFactory.BuildLongStreamingPayload(model, segmentCount);

    private static string BuildSystemPromptPayload(string model)
        => ProxyProbePayloadFactory.BuildSystemPromptPayload(model);

    private static string BuildInstructionFollowingPayload(string model)
        => ProxyProbePayloadFactory.BuildInstructionFollowingPayload(model);

    private static string BuildDataExtractionPayload(string model)
        => ProxyProbePayloadFactory.BuildDataExtractionPayload(model);

    private static string BuildStructuredOutputEdgePayload(string model, string scenarioId)
        => ProxyProbePayloadFactory.BuildStructuredOutputEdgePayload(model, scenarioId);

    private static string BuildToolCallDeepPayload(string model, string scenarioId)
        => ProxyProbePayloadFactory.BuildToolCallDeepPayload(model, scenarioId);

    private static string BuildReasonMathConsistencyPayload(string model, string scenarioId)
        => ProxyProbePayloadFactory.BuildReasonMathConsistencyPayload(model, scenarioId);

    private static string BuildCodeBlockDisciplinePayload(string model, string scenarioId)
        => ProxyProbePayloadFactory.BuildCodeBlockDisciplinePayload(model, scenarioId);

    private static string BuildFunctionCallingProbePayload(string model)
        => ProxyProbePayloadFactory.BuildFunctionCallingProbePayload(model);

    private static string BuildFunctionCallingFollowUpPayload(
        string model,
        string toolCallId,
        string functionName,
        string argumentsJson)
        => ProxyProbePayloadFactory.BuildFunctionCallingFollowUpPayload(
            model,
            toolCallId,
            functionName,
            argumentsJson);

    private static string BuildFunctionCallingFollowUpPayload(
        string wireApi,
        string model,
        string? previousResponseId,
        string toolCallId,
        string functionName,
        string argumentsJson)
    {
        if (string.Equals(
                NormalizeWireApiName(wireApi),
                ProxyWireApiProbeService.ResponsesWireApi,
                StringComparison.Ordinal))
        {
            return ProxyProbePayloadFactory.BuildResponsesFunctionCallingFollowUpPayload(
                model,
                previousResponseId,
                toolCallId);
        }

        return BuildConversationWirePayload(
            wireApi,
            BuildFunctionCallingFollowUpPayload(model, toolCallId, functionName, argumentsJson));
    }

    private static string BuildErrorTransparencyPayload(string model)
        => BuildErrorTransparencyPayload(model, "chat");

    private static string BuildErrorTransparencyPayload(string model, string wireApi)
        => ProxyProbePayloadFactory.BuildErrorTransparencyPayload(model, wireApi);

    private static string BuildStreamingIntegrityPayload(string model, bool stream)
        => ProxyProbePayloadFactory.BuildStreamingIntegrityPayload(model, stream);

    private static string BuildOfficialReferenceIntegrityPayload(string model)
        => ProxyProbePayloadFactory.BuildOfficialReferenceIntegrityPayload(model);

    private static string BuildMultiModalPayload(string model)
        => ProxyProbePayloadFactory.BuildMultiModalPayload(model);

    private static string BuildCacheProbePayload(string model)
        => ProxyProbePayloadFactory.BuildCacheProbePayload(model);

    private static string BuildCacheIsolationPayload(string model, string expectedOutput)
        => ProxyProbePayloadFactory.BuildCacheIsolationPayload(model, expectedOutput);

    private const string RedProbeImageDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGP4z8AARwzEcQCukw/x0F8jngAAAABJRU5ErkJggg==";

    private const string BlueProbeImageDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGNgYPiPhIjiAACOsw/xs6MvMwAAAABJRU5ErkJggg==";

    private static string GetCacheIsolationUserPrompt()
        => "Return the isolation state in one line and nothing else.";

    private static string BuildCacheIsolationExpectedOutput(string owner, string secret)
        => $"cache-isolation-owner={owner}; secret={secret}";

    private static string GetStreamingIntegrityExpectedOutput()
        => string.Join(
            "\n",
            [
                "[01] alpha=proxy",
                "[02] unicode=数据流检查",
                "[03] json={\"ok\":true,\"tag\":\"proxy-ok\"}",
                "[04] symbols=[]{}<>",
                "[05] quote=\"line-break-check\"",
                "[06] end=proxy-ok"
            ]);

    private static string GetOfficialReferenceIntegrityExpectedOutput()
        => string.Join(
            "\n",
            [
                "[01] relay=official-reference",
                "[02] zh=请保持这一整行完全不变",
                "[03] json={\"ok\":true,\"tag\":\"relay-compare\"}",
                "[04] math=1+1=2;2*3=6",
                "[05] symbols=[]{}<>|/\\@#",
                "[06] quote=\"preserve-this-line\"",
                "[07] mix=Alpha-接口-123",
                "[08] end=official-compare-ok"
            ]);

    private static string? ParseChatPreview(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message))
            {
                continue;
            }

            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            if (message.TryGetProperty("content", out content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        return item.GetString();
                    }

                    if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                    {
                        return textElement.GetString();
                    }
                }
            }
        }

        return null;
    }

    private static string? TryParseChatStreamContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta))
            {
                continue;
            }

            if (TryGetNonEmptyString(delta, "content") is { } textContent)
            {
                return textContent;
            }

            if (TryGetNonEmptyString(delta, "reasoning_content") is { } reasoningContent)
            {
                return reasoningContent;
            }

            if (TryGetNonEmptyString(delta, "reasoning") is { } reasoning)
            {
                return reasoning;
            }
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                StringBuilder builder = new();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(item.GetString());
                        continue;
                    }

                    if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(textElement.GetString());
                    }
                }

                if (builder.Length > 0)
                {
                    return builder.ToString();
                }
            }
        }

        return null;
    }

    private static string? TryParseAnthropicStreamContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            string.Equals(typeElement.GetString(), "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("delta", out var delta))
        {
            if (TryGetNonEmptyString(delta, "text") is { } textDelta)
            {
                return textDelta;
            }
        }

        if (root.TryGetProperty("content_block", out var contentBlock) &&
            TryGetNonEmptyString(contentBlock, "text") is { } contentBlockText)
        {
            return contentBlockText;
        }

        if (root.TryGetProperty("completion", out var completion) &&
            completion.ValueKind == JsonValueKind.String)
        {
            return completion.GetString();
        }

        if (root.TryGetProperty("error", out var error))
        {
            return TryExtractJsonErrorMessage(error);
        }

        return null;
    }

    private static bool IsAnthropicStreamDone(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                var type = typeElement.GetString();
                return string.Equals(type, "message_stop", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(type, "done", StringComparison.OrdinalIgnoreCase);
            }

            if (root.TryGetProperty("stop_reason", out var stopReason) &&
                stopReason.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(stopReason.GetString()))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string? TryGetNonEmptyString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;
    }

    private static string? ParseResponsesPreview(string json)
    {
        using var document = JsonDocument.Parse(json);
        return TryExtractResponsesText(document.RootElement);
    }

    private static string? ParseAnthropicMessagesPreview(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                return textElement.GetString();
            }

            if (item.ValueKind == JsonValueKind.String)
            {
                return item.GetString();
            }
        }

        return null;
    }

    private static string? TryExtractJsonErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        return error.GetRawText();
    }

    private static string? ParseStructuredOutputPreview(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rawText = TryExtractResponsesText(document.RootElement);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        return ParseStructuredOutputText(rawText!);
    }

    private static string? TryExtractResponsesText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var outputItem in output.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var contentItems) || contentItems.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in contentItems.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    return textElement.GetString();
                }
            }
        }

        return null;
    }

    private static string? ParseStructuredOutputText(string rawText)
    {
        using var document = JsonDocument.Parse(rawText);
        var root = document.RootElement;

        if (!root.TryGetProperty("ok", out var okElement) ||
            (okElement.ValueKind != JsonValueKind.True && okElement.ValueKind != JsonValueKind.False))
        {
            return null;
        }

        if (!root.TryGetProperty("source", out var sourceElement) || sourceElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var source = sourceElement.GetString();
        return okElement.GetBoolean() ? source : null;
    }

    private static int? TryExtractOutputTokenCount(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryExtractOutputTokenCount(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryExtractOutputTokenCount(JsonElement root)
    {
        if (TryFindUsageElement(root, out var usageElement))
        {
            foreach (var propertyName in new[] { "completion_tokens", "output_tokens", "generated_tokens" })
            {
                if (usageElement.TryGetProperty(propertyName, out var tokenElement) &&
                    tokenElement.ValueKind == JsonValueKind.Number &&
                    tokenElement.TryGetInt32(out var parsedTokenCount) &&
                    parsedTokenCount > 0)
                {
                    return parsedTokenCount;
                }
            }
        }

        return null;
    }

    private static bool TryFindUsageElement(JsonElement root, out JsonElement usageElement)
    {
        if (root.TryGetProperty("usage", out usageElement) &&
            usageElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        usageElement = default;
        return false;
    }

    private static OutputMetrics BuildOutputMetrics(
        string? outputText,
        int? usageTokenCount,
        TimeSpan elapsed,
        TimeSpan? generationDuration = null)
    {
        var normalizedText = string.IsNullOrWhiteSpace(outputText) ? null : outputText.Trim();
        var estimatedTokenCount = normalizedText is null
            ? 0
            : TokenCountEstimator.EstimateOutputTokens(normalizedText);
        var effectiveTokenCount = usageTokenCount ?? (estimatedTokenCount > 0 ? estimatedTokenCount : null);
        var effectiveGenerationDuration = ResolveThroughputWindow(elapsed, generationDuration, effectiveTokenCount);

        var outputTokensPerSecond = effectiveTokenCount is > 0 &&
                                    effectiveGenerationDuration > TimeSpan.Zero
            ? (double?) (effectiveTokenCount.Value / effectiveGenerationDuration.TotalSeconds)
            : null;
        var endToEndTokensPerSecond = effectiveTokenCount is > 0 &&
                                      elapsed > TimeSpan.Zero
            ? (double?) (effectiveTokenCount.Value / elapsed.TotalSeconds)
            : null;

        return new OutputMetrics(
            effectiveTokenCount,
            effectiveTokenCount.HasValue && !usageTokenCount.HasValue,
            normalizedText?.Length,
            effectiveGenerationDuration,
            outputTokensPerSecond,
            endToEndTokensPerSecond);
    }

    private static TimeSpan ResolveThroughputWindow(
        TimeSpan elapsed,
        TimeSpan? generationDuration,
        int? outputTokenCount)
    {
        var candidate = generationDuration ?? elapsed;
        if (candidate <= TimeSpan.Zero || elapsed <= TimeSpan.Zero || elapsed <= candidate)
        {
            return candidate;
        }

        // 某些“流式”返回会把全部内容与 [DONE] 一起快速冲进缓冲区，
        // 这会把首 token 后窗口压到几毫秒甚至更低，进而算出离谱 tok/s。
        if (candidate < TimeSpan.FromMilliseconds(20))
        {
            return elapsed;
        }

        // 短回复样本本来就不适合用极短生成窗口算吞吐，退回端到端窗口会更稳定。
        if (outputTokenCount is > 0 and < 8 &&
            candidate < TimeSpan.FromMilliseconds(120))
        {
            return elapsed;
        }

        return candidate;
    }
}
