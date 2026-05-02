using System.Text;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.Services;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class StreamingIntegrityTestCase : AdvancedTestCaseBase
{
    public StreamingIntegrityTestCase()
        : base(new AdvancedTestCaseDefinition(
            "streaming_integrity",
            "流式完整性",
            AdvancedTestCategory.BasicCompatibility,
            1.5d,
            "检查 SSE data、delta 内容、[DONE] 结束和首包延迟。"))
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
                "You are a streaming probe. Return exactly: ONE TWO THREE.",
                "Return exactly ONE TWO THREE.",
                stream: true);
            var exchange = await client.PostJsonStreamAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var extracted = ExtractTextFromStream(exchange.StreamDataLines);
            var sawDone = exchange.StreamDataLines.Any(ChatSseParser.IsDone);
            var hasDelta = !string.IsNullOrWhiteSpace(extracted);
            var contentOk = extracted.Contains("ONE", StringComparison.OrdinalIgnoreCase) &&
                            extracted.Contains("TWO", StringComparison.OrdinalIgnoreCase) &&
                            extracted.Contains("THREE", StringComparison.OrdinalIgnoreCase);
            var ttft = exchange.FirstTokenLatency?.TotalMilliseconds;
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "流式请求成功连接。" : "流式请求未成功。"),
                new AdvancedCheckResult("Delta", hasDelta, "delta text", hasDelta ? extracted : "-", hasDelta ? "成功拼接 delta 文本。" : "没有从 SSE 中提取到文本 delta。"),
                new AdvancedCheckResult("Done", sawDone, "[DONE]", sawDone ? "[DONE]" : "-", sawDone ? "观察到标准结束事件。" : "没有观察到 [DONE]，可能是上游非标准结束或中途断流。"),
                new AdvancedCheckResult("Content", contentOk, "ONE TWO THREE", extracted, contentOk ? "流式内容完整。" : "流式文本缺少预期片段。"),
                new AdvancedCheckResult("TTFT", ttft.HasValue, "first data latency", ttft.HasValue ? $"{ttft.Value:0} ms" : "-", ttft.HasValue ? "已记录首个 data 行延迟。" : "未记录到首个 data 行。")
            };

            if (exchange.IsSuccessStatusCode && hasDelta && contentOk)
            {
                var score = sawDone ? 100 : 82;
                return BuildResult(
                    exchange,
                    redactor,
                    sawDone ? AdvancedTestStatus.Passed : AdvancedTestStatus.Partial,
                    score,
                    "POST /chat/completions stream=true",
                    sawDone ? "流式输出完整。" : "流式输出有内容，但结束事件不标准。",
                    checks,
                    sawDone ? AdvancedErrorKind.None : AdvancedErrorKind.StreamMalformed,
                    suggestions: sawDone
                        ? new[] { "流式路径可用于聊天和 Agent 客户端。" }
                        : new[] { "建议复测长流和 tool_call 流式场景，确认不会提前结束。" });
            }

            var kind = exchange.IsSuccessStatusCode ? AdvancedErrorKind.StreamMalformed : ClassifyExchange(exchange);
            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions stream=true", "流式输出不可用或不完整。", checks, kind);
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
}
