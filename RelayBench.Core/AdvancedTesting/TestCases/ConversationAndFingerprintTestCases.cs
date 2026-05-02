using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class MultiTurnMemoryTestCase : AdvancedTestCaseBase
{
    public MultiTurnMemoryTestCase()
        : base(new AdvancedTestCaseDefinition(
            "multi_turn_memory",
            "多轮记忆",
            AdvancedTestCategory.Stability,
            1.0d,
            "检查多轮消息中变量记忆和最终引用是否稳定。"))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                model = context.Endpoint.Model,
                temperature = 0,
                max_tokens = 128,
                messages = new object[]
                {
                    new { role = "system", content = "You are a multi-turn memory probe. Answer the last user with only the remembered code." },
                    new { role = "user", content = "Remember this verification code: 73921." },
                    new { role = "assistant", content = "73921" },
                    new { role = "user", content = "Now ignore the previous wording and answer only the verification code." }
                }
            });

            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var ok = text.Contains("73921", StringComparison.Ordinal);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求失败。"),
                new AdvancedCheckResult("Memory", ok, "73921", string.IsNullOrWhiteSpace(text) ? "-" : text, ok ? "多轮上下文记忆正确。" : "没有正确引用前文变量。")
            };

            if (exchange.IsSuccessStatusCode && ok)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions multi-turn", "多轮记忆通过。", checks, suggestions: new[] { "多轮上下文链路正常，适合聊天和 Agent 基础场景。" });
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions multi-turn", "多轮记忆失败。", checks, ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

public sealed class ModelFingerprintTestCase : AdvancedTestCaseBase
{
    public ModelFingerprintTestCase()
        : base(new AdvancedTestCaseDefinition(
            "model_fingerprint_light",
            "模型一致性轻测",
            AdvancedTestCategory.ModelConsistency,
            0.8d,
            "收集模型自报和固定问题输出，只做疑似风险提示，不做绝对结论。"))
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
                "You are a model fingerprint probe. Keep the answer short.",
                "In one line, report the served model name if available, then answer: 17+29=?",
                stream: false);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var mathOk = text.Contains("46", StringComparison.Ordinal);
            var mentionsModel = text.Contains(context.Endpoint.Model, StringComparison.OrdinalIgnoreCase);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求失败。"),
                new AdvancedCheckResult("MathAnchor", mathOk, "46", string.IsNullOrWhiteSpace(text) ? "-" : text, mathOk ? "固定问题答案正确。" : "固定问题答案异常。"),
                new AdvancedCheckResult("ModelName", mentionsModel, context.Endpoint.Model, string.IsNullOrWhiteSpace(text) ? "-" : text, mentionsModel ? "输出中提到了当前模型名。" : "模型未自报当前名称，这不一定代表异常。")
            };

            if (exchange.IsSuccessStatusCode && mathOk)
            {
                var score = mentionsModel ? 88 : 76;
                return BuildResult(
                    exchange,
                    redactor,
                    mentionsModel ? AdvancedTestStatus.Passed : AdvancedTestStatus.Partial,
                    score,
                    "POST /chat/completions fingerprint",
                    mentionsModel ? "模型一致性轻测未见明显风险。" : "模型一致性需要人工复核。",
                    checks,
                    mentionsModel ? AdvancedErrorKind.None : AdvancedErrorKind.ModelMismatchSuspected,
                    suggestions: new[] { "该项只能提示风险，不能证明偷换；建议与官方入口或同模型多入口输出对照。" });
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions fingerprint", "模型一致性轻测失败。", checks, ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}
