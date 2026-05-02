using System.Text;
using System.Text.Json;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.Services;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class UsageAccountingTestCase : AdvancedTestCaseBase
{
    public UsageAccountingTestCase()
        : base(new AdvancedTestCaseDefinition(
            "usage_accounting",
            "Usage 与结束原因",
            AdvancedTestCategory.BasicCompatibility,
            0.9d,
            "检查非流式 Chat 响应是否稳定返回 usage、finish_reason 和可提取正文。"))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = BuildChatPayload(
                context.Endpoint.Model,
                "You are a usage accounting probe. Answer only USAGE_OK.",
                "Return exactly USAGE_OK.",
                stream: false);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var contentOk = text.Contains("USAGE_OK", StringComparison.OrdinalIgnoreCase);
            var hasFinishReason = TryExtractFinishReason(exchange.ResponseBody, out var finishReason);
            var hasUsage = TryExtractUsage(exchange.ResponseBody, out var usageSummary, out var usageLooksConsistent);

            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求未成功。"),
                new AdvancedCheckResult("Content", contentOk, "USAGE_OK", string.IsNullOrWhiteSpace(text) ? "-" : text, contentOk ? "正文可提取。" : "正文缺失或不符合预期。"),
                new AdvancedCheckResult("FinishReason", hasFinishReason, "finish_reason", string.IsNullOrWhiteSpace(finishReason) ? "-" : finishReason, hasFinishReason ? "返回了结束原因。" : "缺少 finish_reason，客户端难以判断生成是否自然结束。"),
                new AdvancedCheckResult("Usage", hasUsage, "usage object", usageSummary, hasUsage ? "返回了 token usage。" : "缺少 usage，成本透明度较低。"),
                new AdvancedCheckResult("UsageConsistency", usageLooksConsistent, "non-negative token counts", usageSummary, usageLooksConsistent ? "usage 数值基本合理。" : "usage 数值缺失或存在明显异常。")
            };

            if (exchange.IsSuccessStatusCode && contentOk && hasFinishReason && hasUsage && usageLooksConsistent)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions usage", "Usage 和结束原因完整。", checks);
            }

            if (exchange.IsSuccessStatusCode && contentOk)
            {
                var score = hasFinishReason ? 78 : 64;
                return BuildResult(
                    exchange,
                    redactor,
                    AdvancedTestStatus.Partial,
                    score,
                    "POST /chat/completions usage",
                    "Chat 可用，但 usage 或 finish_reason 不完整。",
                    checks,
                    hasUsage ? AdvancedErrorKind.UsageSuspicious : AdvancedErrorKind.UsageMissing);
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions usage", "Usage 探测请求失败。", checks, ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    internal static bool TryExtractFinishReason(string? responseBody, out string finishReason)
    {
        finishReason = string.Empty;
        if (!TryParseJson(responseBody, out var document) || document is null)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("finish_reason", out var finish) ||
                finish.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            finishReason = finish.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(finishReason);
        }
    }

    internal static bool TryExtractUsage(string? responseBody, out string summary, out bool looksConsistent)
    {
        summary = "-";
        looksConsistent = false;
        if (!TryParseJson(responseBody, out var document) || document is null)
        {
            return false;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var prompt = TryReadInt(usage, "prompt_tokens") ?? TryReadInt(usage, "input_tokens");
            var completion = TryReadInt(usage, "completion_tokens") ?? TryReadInt(usage, "output_tokens");
            var total = TryReadInt(usage, "total_tokens");
            summary = $"prompt={prompt?.ToString() ?? "-"}, completion={completion?.ToString() ?? "-"}, total={total?.ToString() ?? "-"}";
            looksConsistent =
                (prompt is null || prompt >= 0) &&
                (completion is null || completion >= 0) &&
                (total is null || total >= 0) &&
                (total is null || prompt is null || completion is null || total >= prompt + completion);
            return true;
        }
    }

    private static int? TryReadInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}

public sealed class StreamingTerminalMetadataTestCase : AdvancedTestCaseBase
{
    public StreamingTerminalMetadataTestCase()
        : base(new AdvancedTestCaseDefinition(
            "streaming_terminal_metadata",
            "流式结束元数据",
            AdvancedTestCategory.BasicCompatibility,
            1.0d,
            "检查 stream_options.include_usage、finish_reason、[DONE] 在流式响应末尾是否完整。",
            IsEnabledByDefault: false))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = JsonSerializer.Serialize(new
            {
                model = context.Endpoint.Model,
                stream = true,
                temperature = 0,
                max_tokens = 128,
                stream_options = new { include_usage = true },
                messages = new object[]
                {
                    new { role = "system", content = "You are a streaming metadata probe. Answer only STREAM_META_OK." },
                    new { role = "user", content = "Return exactly STREAM_META_OK." }
                }
            });
            var exchange = await client.PostJsonStreamAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var inspection = InspectStreamingMetadata(exchange.StreamDataLines);
            var clearStreamOptionsRejection = exchange.StatusCode is 400 or 422 &&
                (exchange.ResponseBody ?? string.Empty).Contains("stream_options", StringComparison.OrdinalIgnoreCase);

            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode || clearStreamOptionsRejection, "2xx or clear stream_options rejection", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "流式请求成功。" : clearStreamOptionsRejection ? "服务端明确不支持 stream_options。" : "流式元数据请求失败。"),
                new AdvancedCheckResult("ContentDelta", inspection.ContentOk, "STREAM_META_OK", string.IsNullOrWhiteSpace(inspection.Text) ? "-" : inspection.Text, inspection.ContentOk ? "delta 正文完整。" : "delta 正文缺失或不完整。"),
                new AdvancedCheckResult("FinishReason", inspection.HasFinishReason, "finish_reason", string.IsNullOrWhiteSpace(inspection.FinishReason) ? "-" : inspection.FinishReason, inspection.HasFinishReason ? "观察到 finish_reason。" : "未观察到 finish_reason。"),
                new AdvancedCheckResult("Usage", inspection.HasUsage, "usage in final chunk", inspection.UsageSummary, inspection.HasUsage ? "观察到流式 usage。" : "未观察到流式 usage。"),
                new AdvancedCheckResult("Done", inspection.SawDone, "[DONE]", inspection.SawDone ? "[DONE]" : "-", inspection.SawDone ? "观察到标准结束事件。" : "未观察到标准 [DONE]。")
            };

            if (exchange.IsSuccessStatusCode && inspection.ContentOk && inspection.HasFinishReason && inspection.SawDone)
            {
                return BuildResult(
                    exchange,
                    redactor,
                    inspection.HasUsage ? AdvancedTestStatus.Passed : AdvancedTestStatus.Partial,
                    inspection.HasUsage ? 100 : 86,
                    "POST /chat/completions stream metadata",
                    inspection.HasUsage ? "流式结束元数据完整。" : "流式结束正常，但 usage 未透传。",
                    checks,
                    inspection.HasUsage ? AdvancedErrorKind.None : AdvancedErrorKind.UsageMissing);
            }

            if (clearStreamOptionsRejection)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Partial, 68, "POST /chat/completions stream metadata", "入口明确不支持 stream_options.include_usage。", checks, AdvancedErrorKind.InvalidRequest);
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions stream metadata", "流式结束元数据异常。", checks, exchange.IsSuccessStatusCode ? AdvancedErrorKind.StreamMalformed : ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    internal static StreamingMetadataInspection InspectStreamingMetadata(IReadOnlyList<string> dataLines)
    {
        StringBuilder textBuilder = new();
        var sawDone = false;
        var finishReason = string.Empty;
        var hasUsage = false;
        var usageSummary = "-";

        foreach (var data in dataLines)
        {
            if (ChatSseParser.IsDone(data))
            {
                sawDone = true;
                continue;
            }

            if (!TryParseJson(data, out var document) || document is null)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    hasUsage = true;
                    usageSummary = usage.ToString();
                }

                var eventDelta = ChatSseParser.TryExtractDelta(data);
                if (!string.IsNullOrEmpty(eventDelta))
                {
                    textBuilder.Append(eventDelta);
                }

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish) &&
                    finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString() ?? string.Empty;
                }
            }
        }

        var text = textBuilder.ToString();
        return new StreamingMetadataInspection(
            text,
            text.Contains("STREAM_META_OK", StringComparison.OrdinalIgnoreCase),
            !string.IsNullOrWhiteSpace(finishReason),
            finishReason,
            hasUsage,
            usageSummary,
            sawDone);
    }

    internal sealed record StreamingMetadataInspection(
        string Text,
        bool ContentOk,
        bool HasFinishReason,
        string FinishReason,
        bool HasUsage,
        string UsageSummary,
        bool SawDone);
}

public sealed class JsonMarkdownFenceTestCase : AdvancedTestCaseBase
{
    public JsonMarkdownFenceTestCase()
        : base(new AdvancedTestCaseDefinition(
            "json_markdown_fence",
            "Markdown JSON 容错",
            AdvancedTestCategory.StructuredOutput,
            1.0d,
            "检查常见 Markdown 代码块包裹下的 JSON 是否仍可提取和解析。"))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = BuildChatPayload(
                context.Endpoint.Model,
                "You are a markdown JSON probe.",
                "Return a fenced ```json code block containing exactly this object: {\"status\":\"ok\",\"source\":\"markdown\",\"count\":3}.",
                stream: false);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var normalized = StripMarkdownFence(text);
            var parseOk = TryParseJson(normalized, out var document) && document is not null;
            var fieldsOk = false;
            if (document is not null)
            {
                using (document)
                {
                    var root = document.RootElement;
                    fieldsOk = root.TryGetProperty("status", out var status) &&
                               string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase) &&
                               root.TryGetProperty("source", out var source) &&
                               string.Equals(source.GetString(), "markdown", StringComparison.OrdinalIgnoreCase) &&
                               root.TryGetProperty("count", out var count) &&
                               count.TryGetInt32(out var countValue) &&
                               countValue == 3;
                }
            }

            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求失败。"),
                new AdvancedCheckResult("JsonParse", parseOk, "valid JSON after fence strip", string.IsNullOrWhiteSpace(normalized) ? "-" : normalized, parseOk ? "代码块内 JSON 可解析。" : "去除代码块后仍不是合法 JSON。"),
                new AdvancedCheckResult("Fields", fieldsOk, "status/source/count", fieldsOk ? "ok" : "-", fieldsOk ? "字段完整。" : "字段缺失或值不正确。")
            };

            if (exchange.IsSuccessStatusCode && parseOk && fieldsOk)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions markdown JSON", "Markdown JSON 容错通过。", checks);
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions markdown JSON", "Markdown JSON 容错失败。", checks, parseOk ? AdvancedErrorKind.InvalidRequest : AdvancedErrorKind.JsonMalformed);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    internal static string StripMarkdownFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd
            ? trimmed[(firstLineEnd + 1)..lastFence].Trim()
            : trimmed;
    }
}

public sealed class JsonEscapeUnicodeTestCase : AdvancedTestCaseBase
{
    public JsonEscapeUnicodeTestCase()
        : base(new AdvancedTestCaseDefinition(
            "json_escape_unicode",
            "JSON 转义与中文",
            AdvancedTestCategory.StructuredOutput,
            1.1d,
            "检查引号、反斜杠、换行和中文字段在 JSON 输出中是否保持合法。",
            IsEnabledByDefault: false))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = BuildChatPayload(
                context.Endpoint.Model,
                "You are a strict JSON escaping probe. Return raw JSON only.",
                "Return JSON with fields: quote = He said \"hello\", path = C:\\relaybench\\logs, newline = line1 newline line2, chinese = 稳定. No markdown.",
                stream: false);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var normalized = JsonMarkdownFenceTestCase.StripMarkdownFence(text);
            var parseOk = TryParseJson(normalized, out var document) && document is not null;
            var quoteOk = false;
            var pathOk = false;
            var newlineOk = false;
            var chineseOk = false;
            if (document is not null)
            {
                using (document)
                {
                    var root = document.RootElement;
                    quoteOk = root.TryGetProperty("quote", out var quote) &&
                              quote.GetString()?.Contains("\"hello\"", StringComparison.Ordinal) == true;
                    pathOk = root.TryGetProperty("path", out var path) &&
                             path.GetString()?.Contains("relaybench", StringComparison.OrdinalIgnoreCase) == true;
                    newlineOk = root.TryGetProperty("newline", out var newline) &&
                                newline.GetString()?.Contains("line1", StringComparison.OrdinalIgnoreCase) == true &&
                                newline.GetString()?.Contains("line2", StringComparison.OrdinalIgnoreCase) == true;
                    chineseOk = root.TryGetProperty("chinese", out var chinese) &&
                                chinese.GetString() == "稳定";
                }
            }

            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求失败。"),
                new AdvancedCheckResult("JsonParse", parseOk, "valid JSON", string.IsNullOrWhiteSpace(normalized) ? "-" : normalized, parseOk ? "JSON 可解析。" : "JSON 转义失败，无法解析。"),
                new AdvancedCheckResult("Quote", quoteOk, "He said \"hello\"", quoteOk ? "ok" : "-", quoteOk ? "引号保留正确。" : "引号字段异常。"),
                new AdvancedCheckResult("Path", pathOk, "C:\\relaybench\\logs", pathOk ? "ok" : "-", pathOk ? "反斜杠路径可解析。" : "路径字段异常。"),
                new AdvancedCheckResult("Newline", newlineOk, "line1/line2", newlineOk ? "ok" : "-", newlineOk ? "换行内容保留。" : "换行字段异常。"),
                new AdvancedCheckResult("Chinese", chineseOk, "稳定", chineseOk ? "稳定" : "-", chineseOk ? "中文字段正确。" : "中文字段异常。")
            };

            var passed = exchange.IsSuccessStatusCode && parseOk && quoteOk && pathOk && newlineOk && chineseOk;
            if (passed)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions JSON escaping", "JSON 转义与中文输出通过。", checks);
            }

            var partial = exchange.IsSuccessStatusCode && parseOk;
            return BuildResult(exchange, redactor, partial ? AdvancedTestStatus.Partial : AdvancedTestStatus.Failed, partial ? 58 : 0, "POST /chat/completions JSON escaping", partial ? "JSON 可解析，但转义细节有偏差。" : "JSON 转义输出失败。", checks, parseOk ? AdvancedErrorKind.InvalidRequest : AdvancedErrorKind.JsonMalformed);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

public sealed class MultiTurnFormatRetentionTestCase : AdvancedTestCaseBase
{
    public MultiTurnFormatRetentionTestCase()
        : base(new AdvancedTestCaseDefinition(
            "multi_turn_format_retention",
            "多轮格式保持",
            AdvancedTestCategory.AgentCompatibility,
            1.2d,
            "检查多轮对话后模型是否仍能保持 JSON 输出契约。",
            IsEnabledByDefault: false))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = JsonSerializer.Serialize(new
            {
                model = context.Endpoint.Model,
                temperature = 0,
                max_tokens = 160,
                messages = new object[]
                {
                    new { role = "system", content = "You are a multi-turn format probe. Every assistant answer must be raw JSON only." },
                    new { role = "user", content = "Remember code 73921 and answer JSON {\"phase\":\"remembered\"}." },
                    new { role = "assistant", content = "{\"phase\":\"remembered\"}" },
                    new { role = "user", content = "Now return JSON with phase final and code 73921. No markdown." }
                }
            });
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var normalized = JsonMarkdownFenceTestCase.StripMarkdownFence(text);
            var parseOk = TryParseJson(normalized, out var document) && document is not null;
            var fieldsOk = false;
            if (document is not null)
            {
                using (document)
                {
                    var root = document.RootElement;
                    fieldsOk = root.TryGetProperty("phase", out var phase) &&
                               string.Equals(phase.GetString(), "final", StringComparison.OrdinalIgnoreCase) &&
                               root.TryGetProperty("code", out var code) &&
                               (code.ToString().Contains("73921", StringComparison.Ordinal));
                }
            }

            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求失败。"),
                new AdvancedCheckResult("JsonParse", parseOk, "valid JSON", string.IsNullOrWhiteSpace(normalized) ? "-" : normalized, parseOk ? "多轮后仍输出可解析 JSON。" : "多轮后输出不再是合法 JSON。"),
                new AdvancedCheckResult("FormatMemory", fieldsOk, "phase=final, code=73921", fieldsOk ? "ok" : "-", fieldsOk ? "格式和变量都保持正确。" : "格式或变量引用失败。")
            };

            if (exchange.IsSuccessStatusCode && parseOk && fieldsOk)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions multi-turn JSON", "多轮格式保持通过。", checks);
            }

            var partial = exchange.IsSuccessStatusCode && parseOk;
            return BuildResult(exchange, redactor, partial ? AdvancedTestStatus.Partial : AdvancedTestStatus.Failed, partial ? 58 : 0, "POST /chat/completions multi-turn JSON", partial ? "多轮后 JSON 可解析，但字段或记忆有偏差。" : "多轮格式保持失败。", checks, parseOk ? AdvancedErrorKind.InvalidRequest : AdvancedErrorKind.JsonMalformed);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

public sealed class ReasoningContentReplayTestCase : AdvancedTestCaseBase
{
    public ReasoningContentReplayTestCase()
        : base(new AdvancedTestCaseDefinition(
            "reasoning_content_replay",
            "Reasoning 回传风险",
            AdvancedTestCategory.ReasoningCompatibility,
            1.2d,
            "检查多轮 Chat 中服务端是否错误要求客户端回传 reasoning_content。",
            IsEnabledByDefault: false))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = JsonSerializer.Serialize(new
            {
                model = context.Endpoint.Model,
                temperature = 0,
                max_tokens = 96,
                messages = new object[]
                {
                    new { role = "system", content = "You are a reasoning replay compatibility probe. Do not expose hidden reasoning. Answer only the final number." },
                    new { role = "user", content = "The anchor number is 42." },
                    new { role = "assistant", content = "42" },
                    new { role = "user", content = "Double the anchor number. Return only the number." }
                }
            });
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var responseBody = exchange.ResponseBody ?? string.Empty;
            var requiresReasoningReplay = responseBody.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase) &&
                                          (responseBody.Contains("must", StringComparison.OrdinalIgnoreCase) ||
                                           responseBody.Contains("required", StringComparison.OrdinalIgnoreCase) ||
                                           responseBody.Contains("缺", StringComparison.OrdinalIgnoreCase));
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var contentOk = text.Contains("84", StringComparison.Ordinal);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求失败。"),
                new AdvancedCheckResult("ReasoningReplay", !requiresReasoningReplay, "must not require reasoning_content replay", requiresReasoningReplay ? "requires reasoning_content" : "ok", requiresReasoningReplay ? "服务端要求客户端回传 reasoning_content。" : "未观察到 reasoning_content 回传要求。"),
                new AdvancedCheckResult("Content", contentOk, "84", string.IsNullOrWhiteSpace(text) ? "-" : text, contentOk ? "多轮答案正确。" : "多轮答案不符合预期。")
            };

            if (exchange.IsSuccessStatusCode && !requiresReasoningReplay && contentOk)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions reasoning replay", "未发现 reasoning_content 回传风险。", checks);
            }

            if (requiresReasoningReplay)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions reasoning replay", "服务端要求回传 reasoning_content，常见 SDK 兼容风险较高。", checks, AdvancedErrorKind.ReasoningProtocolIncompatible);
            }

            var partial = exchange.IsSuccessStatusCode;
            return BuildResult(exchange, redactor, partial ? AdvancedTestStatus.Partial : AdvancedTestStatus.Failed, partial ? 62 : 0, "POST /chat/completions reasoning replay", partial ? "协议可用，但多轮答案有偏差。" : "Reasoning 回传风险探测失败。", checks, partial ? AdvancedErrorKind.None : ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

public sealed class EmbeddingsEmptyInputTestCase : AdvancedTestCaseBase
{
    public EmbeddingsEmptyInputTestCase()
        : base(new AdvancedTestCaseDefinition(
            "embeddings_empty_input",
            "Embeddings 空输入",
            AdvancedTestCategory.Rag,
            0.8d,
            "检查 /embeddings 遇到空字符串时是可控返回向量还是清晰拒绝。"))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = JsonSerializer.Serialize(new
            {
                model = context.Endpoint.Model,
                input = string.Empty
            });
            var exchange = await client.PostJsonAsync("embeddings", body, cancellationToken).ConfigureAwait(false);
            var vectors = EmbeddingsVectorReader.ExtractVectors(exchange.ResponseBody);
            var vectorOk = vectors.Count > 0 && vectors[0].Length > 0;
            var clearClientError = exchange.StatusCode is 400 or 422 &&
                                   ((exchange.ResponseBody ?? string.Empty).Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                    (exchange.ResponseBody ?? string.Empty).Contains("input", StringComparison.OrdinalIgnoreCase));
            var serverError = exchange.StatusCode is >= 500;
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode || clearClientError, "2xx or clear 4xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "空输入返回成功。" : clearClientError ? "空输入被清晰拒绝。" : "空输入处理异常。"),
                new AdvancedCheckResult("Vector", vectorOk || clearClientError, "vector or clear error", vectorOk ? $"{vectors[0].Length} dims" : clearClientError ? "clear error" : "-", vectorOk ? "返回了可用向量。" : clearClientError ? "拒绝原因可读，属于可控行为。" : "没有向量，也没有清晰错误。"),
                new AdvancedCheckResult("NoServerError", !serverError, "no 5xx", serverError ? exchange.StatusCode?.ToString() ?? "5xx" : "ok", serverError ? "空输入导致服务端异常。" : "未观察到服务端异常。")
            };

            if (exchange.IsSuccessStatusCode && vectorOk)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /embeddings empty input", "Embeddings 空输入可返回向量。", checks);
            }

            if (clearClientError)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Partial, 74, "POST /embeddings empty input", "Embeddings 空输入被清晰拒绝。", checks, AdvancedErrorKind.InvalidRequest);
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /embeddings empty input", "Embeddings 空输入处理异常。", checks, ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

public sealed class EmbeddingsLongTextTestCase : AdvancedTestCaseBase
{
    public EmbeddingsLongTextTestCase()
        : base(new AdvancedTestCaseDefinition(
            "embeddings_long_text",
            "Embeddings 长文本",
            AdvancedTestCategory.Rag,
            1.2d,
            "检查 /embeddings 对较长中文/英文混合文本的处理能力。",
            IsEnabledByDefault: false))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var longText = string.Join("\n", Enumerable.Range(0, 180).Select(static i => $"RelayBench RAG long text segment {i:000}: 接口稳定性、向量检索、上下文召回和中转站兼容性验证。"));
            var body = JsonSerializer.Serialize(new
            {
                model = context.Endpoint.Model,
                input = longText
            });
            var exchange = await client.PostJsonAsync("embeddings", body, cancellationToken).ConfigureAwait(false);
            var vectors = EmbeddingsVectorReader.ExtractVectors(exchange.ResponseBody);
            var vectorOk = vectors.Count > 0 && vectors[0].Length > 0;
            var clearOverflow = exchange.StatusCode is 400 or 413 or 422 &&
                                ((exchange.ResponseBody ?? string.Empty).Contains("token", StringComparison.OrdinalIgnoreCase) ||
                                 (exchange.ResponseBody ?? string.Empty).Contains("length", StringComparison.OrdinalIgnoreCase) ||
                                 (exchange.ResponseBody ?? string.Empty).Contains("too long", StringComparison.OrdinalIgnoreCase));
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode || clearOverflow, "2xx or clear length error", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "长文本请求成功。" : clearOverflow ? "服务端清晰提示长度限制。" : "长文本请求异常。"),
                new AdvancedCheckResult("Vector", vectorOk, "embedding vector", vectorOk ? $"{vectors[0].Length} dims" : "-", vectorOk ? "返回了长文本向量。" : "没有返回可用长文本向量。"),
                new AdvancedCheckResult("LimitClarity", exchange.IsSuccessStatusCode || clearOverflow, "success or clear overflow", clearOverflow ? "clear overflow" : exchange.IsSuccessStatusCode ? "success" : "-", clearOverflow ? "长度限制可读。" : exchange.IsSuccessStatusCode ? "未触发长度限制。" : "长度限制不清晰。")
            };

            if (exchange.IsSuccessStatusCode && vectorOk)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /embeddings long text", "Embeddings 长文本通过。", checks);
            }

            if (clearOverflow)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Partial, 66, "POST /embeddings long text", "Embeddings 长文本触发清晰长度限制。", checks, AdvancedErrorKind.ContextOverflow);
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /embeddings long text", "Embeddings 长文本失败。", checks, ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

internal static class EmbeddingsVectorReader
{
    public static IReadOnlyList<double[]> ExtractVectors(string? responseBody)
    {
        List<double[]> vectors = [];
        if (!AdvancedTestCaseReflection.TryParseJson(responseBody, out var document) || document is null)
        {
            return vectors;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return vectors;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("embedding", out var embedding) ||
                    embedding.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                vectors.Add(embedding.EnumerateArray()
                    .Where(static value => value.ValueKind == JsonValueKind.Number)
                    .Select(static value => value.GetDouble())
                    .ToArray());
            }
        }

        return vectors;
    }
}

internal static class AdvancedTestCaseReflection
{
    public static bool TryParseJson(string? value, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
