using System.Text;
using System.Text.Json;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.Services;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class ToolCallingStreamTestCase : AdvancedTestCaseBase
{
    public ToolCallingStreamTestCase()
        : base(new AdvancedTestCaseDefinition(
            "tool_calling_stream",
            "流式 Tool Calls",
            AdvancedTestCategory.AgentCompatibility,
            1.8d,
            "检查 stream=true 下 tool_calls 片段、函数名和 arguments 拼接完整性。",
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
            var body = BuildToolPayload(context.Endpoint.Model, stream: true, toolChoice: "auto");
            var exchange = await client.PostJsonStreamAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var inspection = InspectStreamToolCall(exchange.StreamDataLines);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "流式工具请求成功。" : "流式工具请求失败。"),
                new AdvancedCheckResult("ToolDelta", inspection.HasToolDelta, "delta.tool_calls[]", inspection.HasToolDelta ? "present" : "-", inspection.HasToolDelta ? "观察到 tool_calls 流式片段。" : "没有观察到 tool_calls 流式片段。"),
                new AdvancedCheckResult("ToolName", inspection.NameMatches, "search_docs", inspection.Name, inspection.NameMatches ? "流式函数名正确。" : "流式函数名缺失或不匹配。"),
                new AdvancedCheckResult("Arguments", inspection.ArgumentsUsable, "query relay cache isolation, limit 5", inspection.Arguments, inspection.ArgumentsUsable ? "arguments 可拼接并识别关键参数。" : "arguments 拼接后不可解析或缺少关键参数。"),
                new AdvancedCheckResult("Done", inspection.SawDone, "[DONE]", inspection.SawDone ? "[DONE]" : "-", inspection.SawDone ? "观察到标准结束事件。" : "没有观察到标准 [DONE]。")
            };

            if (exchange.IsSuccessStatusCode && inspection.HasToolDelta && inspection.ArgumentsUsable)
            {
                return BuildResult(
                    exchange,
                    redactor,
                    inspection.SawDone ? AdvancedTestStatus.Passed : AdvancedTestStatus.Partial,
                    inspection.SawDone ? 100 : 84,
                    "POST /chat/completions stream=true tools",
                    inspection.SawDone ? "流式 Tool Calling 完整。" : "流式 Tool Calling 有效，但结束事件不标准。",
                    checks,
                    inspection.SawDone ? AdvancedErrorKind.None : AdvancedErrorKind.StreamMalformed,
                    suggestions: new[] { "Codex、Roo Code、Cline 等 Agent 客户端高度依赖该能力，建议优先选择通过该项的入口。" });
            }

            return BuildResult(
                exchange,
                redactor,
                AdvancedTestStatus.Failed,
                0,
                "POST /chat/completions stream=true tools",
                "流式 Tool Calling 失败。",
                checks,
                exchange.IsSuccessStatusCode ? AdvancedErrorKind.ToolCallMalformed : ClassifyExchange(exchange),
                suggestions: new[] { "如果非流式 tools 通过但流式失败，Agent 客户端可能出现 arguments 截断或工具调用挂起。" });
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    internal static string BuildToolPayload(string model, bool stream, object toolChoice)
        => JsonSerializer.Serialize(new
        {
            model,
            stream,
            temperature = 0,
            max_tokens = 256,
            tool_choice = toolChoice,
            tools = new object[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "search_docs",
                        description = "Search internal documentation.",
                        parameters = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                query = new { type = "string" },
                                limit = new { type = "integer" }
                            },
                            required = new[] { "query", "limit" }
                        }
                    }
                }
            },
            messages = new object[]
            {
                new { role = "system", content = "You are a tool-calling compatibility probe. Use tools when the user asks for document search." },
                new { role = "user", content = "Find the internal document about relay cache isolation. Call search_docs with query exactly relay cache isolation and limit exactly 5. Do not answer in text." }
            }
        });

    internal static StreamToolInspection InspectStreamToolCall(IReadOnlyList<string> dataLines)
    {
        var nameBuilder = new StringBuilder();
        var argsBuilder = new StringBuilder();
        var hasToolDelta = false;
        var sawDone = false;

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
                if (root.TryGetProperty("type", out var eventTypeElement) &&
                    eventTypeElement.ValueKind == JsonValueKind.String)
                {
                    var eventType = eventTypeElement.GetString();
                    if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase) &&
                        root.TryGetProperty("item", out var responseItem) &&
                        responseItem.TryGetProperty("type", out var responseItemType) &&
                        responseItemType.ValueKind == JsonValueKind.String &&
                        string.Equals(responseItemType.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
                    {
                        hasToolDelta = true;
                        if (responseItem.TryGetProperty("name", out var responseName) &&
                            responseName.ValueKind == JsonValueKind.String)
                        {
                            nameBuilder.Append(responseName.GetString());
                        }

                        if (responseItem.TryGetProperty("arguments", out var responseArguments) &&
                            responseArguments.ValueKind == JsonValueKind.String &&
                            argsBuilder.Length == 0)
                        {
                            argsBuilder.Append(responseArguments.GetString());
                        }
                    }

                    if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase) &&
                        root.TryGetProperty("delta", out var responseDelta) &&
                        responseDelta.ValueKind == JsonValueKind.String)
                    {
                        hasToolDelta = true;
                        argsBuilder.Append(responseDelta.GetString());
                    }

                    if (string.Equals(eventType, "response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase) &&
                        root.TryGetProperty("arguments", out var responseDoneArguments) &&
                        responseDoneArguments.ValueKind == JsonValueKind.String &&
                        argsBuilder.Length == 0)
                    {
                        hasToolDelta = true;
                        argsBuilder.Append(responseDoneArguments.GetString());
                    }

                    if (string.Equals(eventType, "content_block_start", StringComparison.OrdinalIgnoreCase) &&
                        root.TryGetProperty("content_block", out var contentBlock) &&
                        contentBlock.TryGetProperty("type", out var contentBlockType) &&
                        contentBlockType.ValueKind == JsonValueKind.String &&
                        string.Equals(contentBlockType.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        hasToolDelta = true;
                        if (contentBlock.TryGetProperty("name", out var anthropicName) &&
                            anthropicName.ValueKind == JsonValueKind.String)
                        {
                            nameBuilder.Append(anthropicName.GetString());
                        }
                    }

                    if (string.Equals(eventType, "content_block_delta", StringComparison.OrdinalIgnoreCase) &&
                        root.TryGetProperty("delta", out var anthropicDelta) &&
                        anthropicDelta.TryGetProperty("partial_json", out var partialJson) &&
                        partialJson.ValueKind == JsonValueKind.String)
                    {
                        hasToolDelta = true;
                        argsBuilder.Append(partialJson.GetString());
                    }
                }

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0 ||
                    !choices[0].TryGetProperty("delta", out var delta) ||
                    !delta.TryGetProperty("tool_calls", out var toolCalls) ||
                    toolCalls.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                hasToolDelta = true;
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    if (!toolCall.TryGetProperty("function", out var function))
                    {
                        continue;
                    }

                    if (function.TryGetProperty("name", out var name) &&
                        name.ValueKind == JsonValueKind.String)
                    {
                        nameBuilder.Append(name.GetString());
                    }

                    if (function.TryGetProperty("arguments", out var arguments) &&
                        arguments.ValueKind == JsonValueKind.String)
                    {
                        argsBuilder.Append(arguments.GetString());
                    }
                }
            }
        }

        var nameText = nameBuilder.ToString();
        var argsText = argsBuilder.ToString();
        var nameOk = string.Equals(nameText, "search_docs", StringComparison.OrdinalIgnoreCase) ||
                     nameText.Contains("search_docs", StringComparison.OrdinalIgnoreCase);
        var argsUsable = ArgumentsContainExpectedValues(argsText);
        return new StreamToolInspection(hasToolDelta, nameOk, argsUsable, sawDone, string.IsNullOrWhiteSpace(nameText) ? "-" : nameText, string.IsNullOrWhiteSpace(argsText) ? "-" : argsText);
    }

    private static bool ArgumentsContainExpectedValues(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        if (TryParseJson(arguments, out var document) && document is not null)
        {
            using (document)
            {
                var root = document.RootElement;
                return root.TryGetProperty("query", out var query) &&
                       query.ValueKind == JsonValueKind.String &&
                       string.Equals(query.GetString(), "relay cache isolation", StringComparison.OrdinalIgnoreCase) &&
                       root.TryGetProperty("limit", out var limit) &&
                       limit.TryGetInt32(out var limitValue) &&
                       limitValue == 5;
            }
        }

        return arguments.Contains("relay cache isolation", StringComparison.OrdinalIgnoreCase) &&
               arguments.Contains("5", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record StreamToolInspection(
        bool HasToolDelta,
        bool NameMatches,
        bool ArgumentsUsable,
        bool SawDone,
        string Name,
        string Arguments);
}

public sealed class ToolResultRoundtripTestCase : AdvancedTestCaseBase
{
    public ToolResultRoundtripTestCase()
        : base(new AdvancedTestCaseDefinition(
            "tool_result_roundtrip",
            "工具结果回传",
            AdvancedTestCategory.AgentCompatibility,
            1.7d,
            "检查 assistant tool_calls 与 tool role 结果回传后的多轮回答是否可用。",
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
            var firstBody = ToolCallingBasicTestCase.ToolPayload(context.Endpoint.Model, "auto");
            var firstExchange = await client.PostJsonAsync("chat/completions", firstBody, cancellationToken).ConfigureAwait(false);
            if (!TryExtractToolCallDetails(firstExchange.ResponseBody, out var details))
            {
                var checks = new[]
                {
                    new AdvancedCheckResult("FirstToolCall", false, "assistant tool_calls", "-", "第一轮没有返回可回传的 tool_calls。")
                };
                return BuildResult(firstExchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions tools", "无法进入工具结果回传阶段。", checks, firstExchange.IsSuccessStatusCode ? AdvancedErrorKind.ToolCallMalformed : ClassifyExchange(firstExchange));
            }

            var secondBody = BuildToolResultPayload(context.Endpoint.Model, details);
            var secondExchange = await client.PostJsonAsync("chat/completions", secondBody, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(secondExchange.ResponseBody).Trim();
            var outputOk = text.Contains("Relay Cache Isolation", StringComparison.OrdinalIgnoreCase);
            var checks2 = new[]
            {
                new AdvancedCheckResult("FirstToolCall", true, "assistant tool_calls", details.Name, "第一轮返回了可回传 tool_calls。"),
                new AdvancedCheckResult("HttpStatus", secondExchange.IsSuccessStatusCode, "2xx", secondExchange.StatusCode?.ToString() ?? "-", secondExchange.IsSuccessStatusCode ? "工具结果回传请求成功。" : "工具结果回传请求失败。"),
                new AdvancedCheckResult("ToolResultUsed", outputOk, "Relay Cache Isolation", string.IsNullOrWhiteSpace(text) ? "-" : text, outputOk ? "模型正确消费了 tool 结果。" : "模型没有正确消费 tool 结果。")
            };

            if (secondExchange.IsSuccessStatusCode && outputOk)
            {
                return BuildResult(secondExchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions tool role roundtrip", "工具结果回传通过。", checks2, suggestions: new[] { "多轮工具调用链路可用，适合 Agent 类应用继续深测。" });
            }

            return BuildResult(secondExchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions tool role roundtrip", "工具结果回传失败。", checks2, secondExchange.IsSuccessStatusCode ? AdvancedErrorKind.ToolCallMalformed : ClassifyExchange(secondExchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    private static string BuildToolResultPayload(string model, ToolCallDetails details)
        => JsonSerializer.Serialize(new
        {
            model,
            temperature = 0,
            max_tokens = 128,
            messages = new object[]
            {
                new { role = "system", content = "You are a tool result roundtrip probe. Use the tool result and answer only the title." },
                new { role = "user", content = "Find the internal document about relay cache isolation." },
                new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new object[]
                    {
                        new
                        {
                            id = details.Id,
                            type = "function",
                            function = new
                            {
                                name = details.Name,
                                arguments = details.Arguments
                            }
                        }
                    }
                },
                new
                {
                    role = "tool",
                    tool_call_id = details.Id,
                    content = "{\"title\":\"Relay Cache Isolation\",\"status\":\"found\"}"
                },
                new { role = "user", content = "Return the found title only." }
            }
        });

    private static bool TryExtractToolCallDetails(string? responseBody, out ToolCallDetails details)
    {
        details = new ToolCallDetails("call_relaybench", "search_docs", "{\"query\":\"relay cache isolation\",\"limit\":5}");
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
                !choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("tool_calls", out var toolCalls) ||
                toolCalls.ValueKind != JsonValueKind.Array ||
                toolCalls.GetArrayLength() == 0)
            {
                return false;
            }

            var toolCall = toolCalls[0];
            var id = toolCall.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? "call_relaybench"
                : "call_relaybench";
            if (!toolCall.TryGetProperty("function", out var function))
            {
                return false;
            }

            var name = function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var arguments = function.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.String
                ? argsElement.GetString() ?? "{}"
                : "{}";
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            details = new ToolCallDetails(id, name, arguments);
            return true;
        }
    }

    private sealed record ToolCallDetails(string Id, string Name, string Arguments);
}

public sealed class JsonStreamingIntegrityTestCase : AdvancedTestCaseBase
{
    public JsonStreamingIntegrityTestCase()
        : base(new AdvancedTestCaseDefinition(
            "json_streaming_integrity",
            "流式 JSON 拼接",
            AdvancedTestCategory.StructuredOutput,
            1.4d,
            "检查 stream=true 下 JSON 文本能否完整拼接并解析。",
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
                "You are a streaming JSON probe. Return raw JSON only. No markdown.",
                "Return exactly this JSON object with no extra text: {\"status\":\"ok\",\"items\":[{\"name\":\"alpha\"},{\"name\":\"beta\"}],\"cn\":\"stable\"}",
                stream: true);
            var exchange = await client.PostJsonStreamAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractTextFromStream(exchange.StreamDataLines).Trim();
            var normalized = StripFence(text);
            var parseOk = TryParseJson(normalized, out var document) && document is not null;
            var fieldsOk = false;
            if (document is not null)
            {
                using (document)
                {
                    var root = document.RootElement;
                    fieldsOk = root.TryGetProperty("status", out var status) &&
                               string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase) &&
                               root.TryGetProperty("items", out var items) &&
                               items.ValueKind == JsonValueKind.Array &&
                               items.GetArrayLength() >= 2 &&
                               root.TryGetProperty("cn", out var cn) &&
                               string.Equals(cn.GetString(), "stable", StringComparison.OrdinalIgnoreCase);
                }
            }

            var sawDone = exchange.StreamDataLines.Any(ChatSseParser.IsDone);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "流式 JSON 请求成功。" : "流式 JSON 请求失败。"),
                new AdvancedCheckResult("TextDelta", !string.IsNullOrWhiteSpace(text), "delta text", string.IsNullOrWhiteSpace(text) ? "-" : text, "记录流式拼接后的文本。"),
                new AdvancedCheckResult("JsonParse", parseOk, "valid JSON", string.IsNullOrWhiteSpace(normalized) ? "-" : normalized, parseOk ? "拼接结果可以解析为 JSON。" : "拼接结果不是合法 JSON。"),
                new AdvancedCheckResult("Fields", fieldsOk, "status/items/cn", fieldsOk ? "ok" : "-", fieldsOk ? "字段完整。" : "字段缺失或值错误。"),
                new AdvancedCheckResult("Done", sawDone, "[DONE]", sawDone ? "[DONE]" : "-", sawDone ? "观察到标准结束事件。" : "未观察到标准结束事件。")
            };

            if (exchange.IsSuccessStatusCode && parseOk && fieldsOk)
            {
                return BuildResult(exchange, redactor, sawDone ? AdvancedTestStatus.Passed : AdvancedTestStatus.Partial, sawDone ? 100 : 82, "POST /chat/completions stream JSON", "流式 JSON 拼接通过。", checks, sawDone ? AdvancedErrorKind.None : AdvancedErrorKind.StreamMalformed);
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions stream JSON", "流式 JSON 拼接失败。", checks, parseOk ? AdvancedErrorKind.InvalidRequest : AdvancedErrorKind.JsonMalformed);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    private static string ExtractTextFromStream(IReadOnlyList<string> dataLines)
    {
        StringBuilder builder = new();
        foreach (var data in dataLines)
        {
            if (ChatSseParser.IsDone(data))
            {
                continue;
            }

            var delta = ChatSseParser.TryExtractDelta(data);
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            builder.Append(delta);
        }

        return builder.ToString();
    }

    private static string StripFence(string value)
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

public sealed class ReasoningChatParameterTestCase : AdvancedTestCaseBase
{
    public ReasoningChatParameterTestCase()
        : base(new AdvancedTestCaseDefinition(
            "reasoning_chat_parameter",
            "Chat Reasoning 参数",
            AdvancedTestCategory.ReasoningCompatibility,
            1.0d,
            "检查 /chat/completions 对 reasoning_effort 参数的接受、忽略或清晰拒绝行为。",
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
                reasoning_effort = "low",
                messages = new object[]
                {
                    new { role = "system", content = "You are a reasoning parameter compatibility probe. Answer only READY." },
                    new { role = "user", content = "Return READY." }
                }
            });
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody);
            var bodyText = exchange.ResponseBody ?? string.Empty;
            var outputOk = text.Contains("READY", StringComparison.OrdinalIgnoreCase);
            var clearRejection = exchange.StatusCode is 400 or 422 &&
                                 (bodyText.Contains("reasoning", StringComparison.OrdinalIgnoreCase) ||
                                  bodyText.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                                  bodyText.Contains("unknown", StringComparison.OrdinalIgnoreCase));
            var requiresReasoningReplay = bodyText.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase) &&
                                          bodyText.Contains("required", StringComparison.OrdinalIgnoreCase);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode || clearRejection, "2xx or clear 4xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "Chat reasoning 请求成功。" : clearRejection ? "服务端清晰拒绝 reasoning 参数。" : "服务端未清晰处理 reasoning 参数。"),
                new AdvancedCheckResult("Output", outputOk || clearRejection, "READY or clear rejection", outputOk ? text.Trim() : clearRejection ? "clear rejection" : "-", outputOk ? "模型正常输出。" : clearRejection ? "非 reasoning 模型清晰拒绝，属于可处理行为。" : "没有正常输出，也没有清晰错误。"),
                new AdvancedCheckResult("ReasoningReplay", !requiresReasoningReplay, "must not require client replay", requiresReasoningReplay ? "requires reasoning_content" : "ok", requiresReasoningReplay ? "服务端要求客户端回传 reasoning_content，常见 SDK 兼容风险高。" : "没有观察到 reasoning_content 回传要求。")
            };

            if (exchange.IsSuccessStatusCode && outputOk && !requiresReasoningReplay)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions reasoning_effort", "Chat reasoning 参数可用或可忽略。", checks);
            }

            if (clearRejection && !requiresReasoningReplay)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Partial, 74, "POST /chat/completions reasoning_effort", "Chat reasoning 参数被清晰拒绝，可由客户端降级处理。", checks, AdvancedErrorKind.ReasoningProtocolIncompatible);
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions reasoning_effort", "Chat reasoning 参数行为异常。", checks, AdvancedErrorKind.ReasoningProtocolIncompatible);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

public sealed class LongContext16KNeedleTestCase : AdvancedTestCaseBase
{
    public LongContext16KNeedleTestCase()
        : base(new AdvancedTestCaseDefinition(
            "long_context_16k_needle",
            "16K Needle 召回",
            AdvancedTestCategory.LongContext,
            1.5d,
            "构造约 16K 级上下文，检查中间位置 needle 召回。",
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
            var filler = string.Join("\n", Enumerable.Range(0, 420).Select(i => $"Segment {i:0000}: RelayBench long context filler with stable repeated technical wording for needle recall measurement."));
            var needle = "NEEDLE_CODE: RB-16K-58213";
            var prompt = $"{filler}\n{needle}\n{filler}\n\nWhat is the NEEDLE_CODE? Return only the code.";
            var body = BuildChatPayload(context.Endpoint.Model, "You are a long-context recall probe. Answer only the requested code.", prompt, stream: false);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var ok = text.Contains("RB-16K-58213", StringComparison.OrdinalIgnoreCase);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "16K 请求成功。" : "16K 请求失败。"),
                new AdvancedCheckResult("NeedleRecall", ok, "RB-16K-58213", string.IsNullOrWhiteSpace(text) ? "-" : text, ok ? "16K needle 召回正确。" : "16K needle 召回失败或上下文被截断。")
            };

            if (exchange.IsSuccessStatusCode && ok)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions 16K needle", "16K Needle 召回通过。", checks);
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions 16K needle", "16K Needle 召回失败。", checks, exchange.StatusCode is 400 or 413 ? AdvancedErrorKind.ContextOverflow : ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

public sealed class ConcurrencyStaircaseTestCase : AdvancedTestCaseBase
{
    public ConcurrencyStaircaseTestCase()
        : base(new AdvancedTestCaseDefinition(
            "concurrency_staircase",
            "并发阶梯 1/2/4/8",
            AdvancedTestCategory.Concurrency,
            1.8d,
            "按 1、2、4、8 并发观察成功率、P95 延迟、429 与 5xx。",
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
            var body = BuildChatPayload(context.Endpoint.Model, "You are a concurrency probe. Answer only OK.", "Return OK.", stream: false);
            List<AdvancedModelExchange> exchanges = [];
            List<string> stageSummaries = [];
            foreach (var concurrency in new[] { 1, 2, 4, 8 })
            {
                var tasks = Enumerable.Range(0, concurrency)
                    .Select(_ => client.PostJsonAsync("chat/completions", body, cancellationToken))
                    .ToArray();
                var stage = await Task.WhenAll(tasks).ConfigureAwait(false);
                exchanges.AddRange(stage);
                var stageSuccess = stage.Count(static item => item.IsSuccessStatusCode);
                var stage429 = stage.Count(static item => item.StatusCode == 429);
                stageSummaries.Add($"{concurrency}并发: {stageSuccess}/{stage.Length} 成功, 429={stage429}");
            }

            var total = exchanges.Count;
            var success = exchanges.Count(static item => item.IsSuccessStatusCode);
            var rateLimited = exchanges.Count(static item => item.StatusCode == 429);
            var serverErrors = exchanges.Count(static item => item.StatusCode is >= 500);
            var successRate = total == 0 ? 0 : (double)success / total;
            var latencies = exchanges.Select(static item => item.Duration.TotalMilliseconds).OrderBy(static item => item).ToArray();
            var p95 = Percentile(latencies, 0.95d);
            var representative = exchanges.LastOrDefault() ?? new AdvancedModelExchange(null, string.Empty, "POST", context.Endpoint.BaseUrl, new Dictionary<string, string>(), body, new Dictionary<string, string>(), "No exchange.", TimeSpan.Zero, null, Array.Empty<string>());
            var checks = new[]
            {
                new AdvancedCheckResult("SuccessRate", successRate >= 0.9d, ">=90%", $"{success}/{total}", successRate >= 0.9d ? "并发阶梯成功率达标。" : "并发阶梯成功率偏低。"),
                new AdvancedCheckResult("RateLimit", rateLimited <= 1, "<=1", rateLimited.ToString(), rateLimited <= 1 ? "限流数量可接受。" : "限流较明显。"),
                new AdvancedCheckResult("ServerErrors", serverErrors == 0, "0", serverErrors.ToString(), serverErrors == 0 ? "未观察到 5xx。" : "并发下出现 5xx。"),
                new AdvancedCheckResult("P95", p95 > 0, ">0 ms", $"{p95:0} ms", $"阶段摘要：{string.Join("；", stageSummaries)}")
            };

            if (successRate >= 0.9d && rateLimited <= 1 && serverErrors == 0)
            {
                return BuildResult(representative, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions 1/2/4/8 concurrency", $"并发阶梯通过，P95 {p95:0} ms。", checks);
            }

            var partial = successRate >= 0.7d;
            return BuildResult(
                representative,
                redactor,
                partial ? AdvancedTestStatus.Partial : AdvancedTestStatus.Failed,
                partial ? 58 : 0,
                "POST /chat/completions 1/2/4/8 concurrency",
                $"并发阶梯成功率 {success}/{total}，429={rateLimited}，5xx={serverErrors}，P95 {p95:0} ms。",
                checks,
                rateLimited > 1 ? AdvancedErrorKind.RateLimited : AdvancedErrorKind.ServerError);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(values.Count * percentile) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }
}

public sealed class EmbeddingsSimilarityTestCase : AdvancedTestCaseBase
{
    public EmbeddingsSimilarityTestCase()
        : base(new AdvancedTestCaseDefinition(
            "embeddings_similarity",
            "Embeddings 相似度",
            AdvancedTestCategory.Rag,
            1.5d,
            "检查 batch embeddings 与基础 cosine similarity 排序是否合理。",
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
                input = new[]
                {
                    "RelayBench evaluates API endpoint reliability and stability.",
                    "RelayBench tests proxy interface uptime and request stability.",
                    "A banana bread recipe needs ripe bananas and flour."
                }
            });
            var exchange = await client.PostJsonAsync("embeddings", body, cancellationToken).ConfigureAwait(false);
            var vectors = ExtractVectors(exchange.ResponseBody);
            var shapeOk = vectors.Count >= 3 && vectors.All(static vector => vector.Length > 0);
            var dimensionsOk = shapeOk && vectors.Select(static vector => vector.Length).Distinct().Count() == 1;
            var similarScore = shapeOk ? Cosine(vectors[0], vectors[1]) : 0;
            var unrelatedScore = shapeOk ? Cosine(vectors[0], vectors[2]) : 0;
            var rankingOk = shapeOk && similarScore > unrelatedScore;
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "Embeddings 请求成功。" : "Embeddings 请求失败。"),
                new AdvancedCheckResult("VectorShape", shapeOk, "3 vectors", shapeOk ? $"{vectors.Count} vectors" : "-", shapeOk ? "返回了 batch 向量。" : "没有返回完整 batch 向量。"),
                new AdvancedCheckResult("Dimensions", dimensionsOk, "same dimension", dimensionsOk ? vectors[0].Length.ToString() : "-", dimensionsOk ? "向量维度一致。" : "向量维度不一致。"),
                new AdvancedCheckResult("Similarity", rankingOk, "related > unrelated", $"{similarScore:0.000} > {unrelatedScore:0.000}", rankingOk ? "相似文本分数高于无关文本。" : "相似度排序不合理。")
            };

            if (exchange.IsSuccessStatusCode && shapeOk && dimensionsOk && rankingOk)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /embeddings similarity", "Embeddings 相似度排序通过。", checks);
            }

            var partial = exchange.IsSuccessStatusCode && shapeOk && dimensionsOk;
            return BuildResult(exchange, redactor, partial ? AdvancedTestStatus.Partial : AdvancedTestStatus.Failed, partial ? 62 : 0, "POST /embeddings similarity", partial ? "Embeddings 返回结构正常，但相似度排序需复核。" : "Embeddings 相似度测试失败。", checks, partial ? AdvancedErrorKind.UsageSuspicious : ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    private static IReadOnlyList<double[]> ExtractVectors(string? responseBody)
    {
        List<double[]> vectors = [];
        if (!TryParseJson(responseBody, out var document) || document is null)
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

    private static double Cosine(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var length = Math.Min(left.Count, right.Count);
        if (length == 0)
        {
            return 0;
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var i = 0; i < length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        return leftNorm <= 0 || rightNorm <= 0
            ? 0
            : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
