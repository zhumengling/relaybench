using System.Text.Json;
using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class ReasoningCompatibilityTestCase : AdvancedTestCaseBase
{
    public ReasoningCompatibilityTestCase()
        : base(new AdvancedTestCaseDefinition(
            "reasoning_responses_probe",
            "Reasoning / Responses",
            AdvancedTestCategory.ReasoningCompatibility,
            1.2d,
            "使用 Responses API 的 reasoning.effort 做轻量兼容性探测。"))
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
                max_output_tokens = 128,
                reasoning = new { effort = "low" },
                input = "Return only the word READY."
            });
            var exchange = await client.PostJsonAsync("responses", body, cancellationToken).ConfigureAwait(false);
            var bodyText = exchange.ResponseBody ?? string.Empty;
            var contentOk = bodyText.Contains("READY", StringComparison.OrdinalIgnoreCase) ||
                            bodyText.Contains("output_text", StringComparison.OrdinalIgnoreCase);
            var reasoningRejected = bodyText.Contains("reasoning", StringComparison.OrdinalIgnoreCase) &&
                                    bodyText.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx or clear 4xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "Responses 请求成功。" : "Responses 或 reasoning 参数被拒绝。"),
                new AdvancedCheckResult("Output", contentOk, "READY/output_text", contentOk ? "present" : "-", contentOk ? "可以提取 Responses 输出线索。" : "没有提取到 Responses 输出线索。"),
                new AdvancedCheckResult("ReasoningRejection", !reasoningRejected, "not unsupported", reasoningRejected ? "unsupported reasoning" : "ok", reasoningRejected ? "服务端明确不支持 reasoning 参数。" : "没有观察到 reasoning 参数被明确拒绝。")
            };

            if (exchange.IsSuccessStatusCode && contentOk)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /responses reasoning=low", "Responses reasoning 探测通过。", checks, suggestions: new[] { "该入口更适合 Codex/Responses 路径和 reasoning 参数实验。" });
            }

            var invalidRequest = ClassifyExchange(exchange);
            return BuildResult(
                exchange,
                redactor,
                AdvancedTestStatus.Partial,
                invalidRequest == AdvancedErrorKind.InvalidRequest ? 45 : 25,
                "POST /responses reasoning=low",
                "Responses reasoning 未通过，仍可作为 Chat Completions 入口复核。",
                checks,
                AdvancedErrorKind.ReasoningProtocolIncompatible,
                suggestions: new[] { "如果客户端只用 /chat/completions，可以取消该测试；如果用于 Codex/Responses，请优先选择通过该项的入口。" });
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}
