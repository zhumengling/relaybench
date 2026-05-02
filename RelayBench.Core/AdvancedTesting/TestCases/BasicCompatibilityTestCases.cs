using System.Text.Json;
using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class ModelsEndpointTestCase : AdvancedTestCaseBase
{
    public ModelsEndpointTestCase()
        : base(new AdvancedTestCaseDefinition(
            "models_endpoint",
            "GET /models",
            AdvancedTestCategory.BasicCompatibility,
            1.0d,
            "检查模型列表接口是否可访问，并确认能返回数组结构。"))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var exchange = await client.GetAsync("models", cancellationToken).ConfigureAwait(false);
            var checks = new List<AdvancedCheckResult>();
            var success = exchange.IsSuccessStatusCode;
            checks.Add(new AdvancedCheckResult("HttpStatus", success, "2xx", exchange.StatusCode?.ToString() ?? "-", success ? "模型列表接口返回成功状态。" : "模型列表接口未返回成功状态。"));

            var hasDataArray = false;
            if (TryParseJson(exchange.ResponseBody, out var document) && document is not null)
            {
                using (document)
                {
                    hasDataArray = document.RootElement.TryGetProperty("data", out var data) &&
                                   data.ValueKind == JsonValueKind.Array;
                }
            }

            checks.Add(new AdvancedCheckResult("ModelsShape", hasDataArray, "data[]", hasDataArray ? "data[]" : "未发现 data 数组", hasDataArray ? "返回结构符合常见 OpenAI 模型列表格式。" : "返回结构不符合常见模型列表格式。"));

            if (success && hasDataArray)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "GET /models", "模型列表可用。", checks, suggestions: new[] { "模型列表接口正常，可继续检查聊天与高级能力。" });
            }

            var kind = ClassifyExchange(exchange);
            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "GET /models", "模型列表不可用或结构异常。", checks, kind);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

public sealed class ChatCompletionTestCase : AdvancedTestCaseBase
{
    public ChatCompletionTestCase()
        : base(new AdvancedTestCaseDefinition(
            "chat_non_stream",
            "非流式 Chat",
            AdvancedTestCategory.BasicCompatibility,
            1.5d,
            "检查 /chat/completions 非流式请求是否可用。"))
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
                "You are a RelayBench compatibility probe. Answer only with PONG.",
                "Return exactly PONG.",
                stream: false);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody);
            var contentOk = text.Trim().Contains("PONG", StringComparison.OrdinalIgnoreCase);
            var usageOk = HasUsage(exchange.ResponseBody);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "Chat 非流式接口返回成功状态。" : "Chat 非流式接口未成功。"),
                new AdvancedCheckResult("Content", contentOk, "PONG", string.IsNullOrWhiteSpace(text) ? "-" : text.Trim(), contentOk ? "模型输出可提取。" : "没有提取到预期文本。"),
                new AdvancedCheckResult("Usage", usageOk, "usage object", usageOk ? "usage" : "-", usageOk ? "返回了 token usage。" : "没有返回 usage，成本透明度会降低。")
            };

            if (exchange.IsSuccessStatusCode && contentOk)
            {
                var score = usageOk ? 100 : 88;
                return BuildResult(
                    exchange,
                    redactor,
                    usageOk ? AdvancedTestStatus.Passed : AdvancedTestStatus.Partial,
                    score,
                    "POST /chat/completions",
                    usageOk ? "非流式 Chat 正常，usage 已返回。" : "非流式 Chat 正常，但 usage 缺失。",
                    checks,
                    usageOk ? AdvancedErrorKind.None : AdvancedErrorKind.UsageMissing,
                    suggestions: usageOk
                        ? new[] { "基础聊天链路正常，可继续观察 stream、tool calling 和 JSON 输出。" }
                        : new[] { "如需核对成本，优先使用能够透传 usage 的入口。" });
            }

            var kind = ClassifyExchange(exchange);
            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions", "非流式 Chat 不可用。", checks, kind);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}

public sealed class ErrorPassThroughTestCase : AdvancedTestCaseBase
{
    public ErrorPassThroughTestCase()
        : base(new AdvancedTestCaseDefinition(
            "error_pass_through",
            "错误透传",
            AdvancedTestCategory.BasicCompatibility,
            0.8d,
            "构造一个不存在模型名，检查错误响应是否清晰透传。"))
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
                $"{context.Endpoint.Model}-relaybench-invalid-model",
                "You are a RelayBench error pass-through probe.",
                "This request should fail because the model name is intentionally invalid.",
                stream: false);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var statusLooksLikeError = exchange.StatusCode is >= 400 and < 500;
            var hasErrorMessage = (exchange.ResponseBody ?? string.Empty).Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                  (exchange.ResponseBody ?? string.Empty).Contains("model", StringComparison.OrdinalIgnoreCase);
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", statusLooksLikeError, "4xx", exchange.StatusCode?.ToString() ?? "-", statusLooksLikeError ? "错误请求返回了客户端错误状态。" : "错误请求没有返回明确 4xx。"),
                new AdvancedCheckResult("ErrorBody", hasErrorMessage, "error/model message", hasErrorMessage ? "包含错误信息" : "-", hasErrorMessage ? "响应正文包含可读错误线索。" : "响应正文缺少明确错误线索。")
            };

            if (statusLooksLikeError && hasErrorMessage)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions with invalid model", "错误信息透传清晰。", checks, suggestions: new[] { "错误透传正常，客户端排障体验较好。" });
            }

            return BuildResult(
                exchange,
                redactor,
                AdvancedTestStatus.Partial,
                55,
                "POST /chat/completions with invalid model",
                "错误透传不够清晰，需要人工复核。",
                checks,
                AdvancedErrorKind.InvalidRequest,
                suggestions: new[] { "如果错误请求返回 200 或正文不含错误原因，客户端定位模型名、Key、额度问题会更困难。" });
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}
