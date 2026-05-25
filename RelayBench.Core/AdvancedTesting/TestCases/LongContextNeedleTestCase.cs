using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class LongContextNeedleTestCase : AdvancedTestCaseBase
{
    public LongContextNeedleTestCase()
        : base(new AdvancedTestCaseDefinition(
            "long_context_needle",
            "长上下文 Needle 轻测",
            AdvancedTestCategory.LongContext,
            1.1d,
            "构造约 8K 级上下文，在中间插入 needle，检查召回是否正确。",
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
            var filler = string.Join("\n", Enumerable.Range(0, 180).Select(i => $"Line {i:000}: RelayBench context filler for stability testing."));
            var needle = "NEEDLE_CODE: RB-73921-LONG";
            var prompt = $"{filler}\n{needle}\n{filler}\n\nWhat is the NEEDLE_CODE? Return only the code.";
            var body = BuildChatPayload(
                context.Endpoint.Model,
                "You are a long-context recall probe. Answer only the requested code.",
                prompt,
                stream: false);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var ok = text.Contains("RB-73921-LONG", StringComparison.OrdinalIgnoreCase);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求失败。"),
                new AdvancedCheckResult("NeedleRecall", ok, "RB-73921-LONG", string.IsNullOrWhiteSpace(text) ? "-" : text, ok ? "needle 召回正确。" : "needle 召回失败或被截断。")
            };

            if (exchange.IsSuccessStatusCode && ok)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions 8K needle", "8K 级长上下文召回通过。", checks, suggestions: new[] { "轻量长上下文可用；需要验证虚标上下文时再运行 16K/32K/64K 梯度。" });
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions 8K needle", "长上下文召回失败。", checks, exchange.StatusCode is 400 or 413 ? AdvancedErrorKind.ContextOverflow : ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}
