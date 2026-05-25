using System.Text.Json;
using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class JsonStructuredOutputTestCase : AdvancedTestCaseBase
{
    public JsonStructuredOutputTestCase()
        : base(new AdvancedTestCaseDefinition(
            "json_schema_required_enum",
            "JSON 结构化输出",
            AdvancedTestCategory.StructuredOutput,
            1.6d,
            "检查 JSON 对象、required 字段、enum 字段、嵌套对象和数组对象。"))
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
                "You are a strict JSON output probe. Return raw JSON only. Do not wrap markdown.",
                "Return a JSON object with exactly these fields: status enum ok|fail = ok, score number = 97, user object with name RelayBench and tier pro, tags array containing agent and rag, chinese_field value 稳定. No markdown.",
                stream: false);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var text = ExtractOpenAiText(exchange.ResponseBody).Trim();
            var normalized = StripMarkdownFence(text);
            var parseOk = TryParseJson(normalized, out var document) && document is not null;
            var requiredOk = false;
            var enumOk = false;
            var nestedOk = false;
            var arrayOk = false;
            var chineseOk = false;

            if (document is not null)
            {
                using (document)
                {
                    var root = document.RootElement;
                    requiredOk = root.ValueKind == JsonValueKind.Object &&
                                 root.TryGetProperty("status", out _) &&
                                 root.TryGetProperty("score", out _) &&
                                 root.TryGetProperty("user", out _) &&
                                 root.TryGetProperty("tags", out _) &&
                                 root.TryGetProperty("chinese_field", out _);
                    enumOk = root.TryGetProperty("status", out var status) &&
                             status.ValueKind == JsonValueKind.String &&
                             string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
                    nestedOk = root.TryGetProperty("user", out var user) &&
                               user.ValueKind == JsonValueKind.Object &&
                               user.TryGetProperty("name", out var name) &&
                               string.Equals(name.GetString(), "RelayBench", StringComparison.OrdinalIgnoreCase);
                    arrayOk = root.TryGetProperty("tags", out var tags) &&
                              tags.ValueKind == JsonValueKind.Array &&
                              tags.EnumerateArray().Any(item => string.Equals(item.GetString(), "agent", StringComparison.OrdinalIgnoreCase)) &&
                              tags.EnumerateArray().Any(item => string.Equals(item.GetString(), "rag", StringComparison.OrdinalIgnoreCase));
                    chineseOk = root.TryGetProperty("chinese_field", out var chinese) &&
                                chinese.ValueKind == JsonValueKind.String &&
                                chinese.GetString() == "稳定";
                }
            }

            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求失败。"),
                new AdvancedCheckResult("JsonParse", parseOk, "valid JSON", string.IsNullOrWhiteSpace(normalized) ? "-" : normalized, parseOk ? "输出可以被 JSON.parse。" : "输出不是合法 JSON。"),
                new AdvancedCheckResult("Required", requiredOk, "status/score/user/tags/chinese_field", requiredOk ? "all present" : "missing fields", requiredOk ? "必需字段完整。" : "缺少必需字段。"),
                new AdvancedCheckResult("Enum", enumOk, "status=ok", enumOk ? "ok" : "-", enumOk ? "enum 字段正确。" : "enum 字段不符合预期。"),
                new AdvancedCheckResult("NestedObject", nestedOk, "user.name=RelayBench", nestedOk ? "ok" : "-", nestedOk ? "嵌套对象正确。" : "嵌套对象缺失或字段错误。"),
                new AdvancedCheckResult("ArrayObject", arrayOk, "tags include agent, rag", arrayOk ? "ok" : "-", arrayOk ? "数组字段正确。" : "数组字段不完整。"),
                new AdvancedCheckResult("ChineseField", chineseOk, "稳定", chineseOk ? "稳定" : "-", chineseOk ? "中文字段值正确。" : "中文字段缺失或错误。")
            };

            var passed = exchange.IsSuccessStatusCode && parseOk && requiredOk && enumOk && nestedOk && arrayOk && chineseOk;
            if (passed)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions JSON probe", "JSON 结构化输出稳定。", checks, suggestions: new[] { "该入口适合需要结构化响应的业务系统。" });
            }

            var partial = exchange.IsSuccessStatusCode && parseOk && requiredOk;
            return BuildResult(
                exchange,
                redactor,
                partial ? AdvancedTestStatus.Partial : AdvancedTestStatus.Failed,
                partial ? 62 : 0,
                "POST /chat/completions JSON probe",
                partial ? "JSON 可解析但字段细节有偏差。" : "JSON 结构化输出失败。",
                checks,
                parseOk ? AdvancedErrorKind.InvalidRequest : AdvancedErrorKind.JsonMalformed,
                suggestions: new[] { "查看原始输出是否包含 Markdown 包裹、漏字段或字段名漂移；必要时使用 response_format / JSON Schema 强约束。" });
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    private static string StripMarkdownFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineEnd < 0 || lastFence <= firstLineEnd)
        {
            return trimmed;
        }

        return trimmed[(firstLineEnd + 1)..lastFence].Trim();
    }
}
