using System.Text.Json;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.Services;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class ToolCallingBasicTestCase : AdvancedTestCaseBase
{
    public ToolCallingBasicTestCase()
        : base(new AdvancedTestCaseDefinition(
            "tool_calling_basic",
            "Tool Calling 基础",
            AdvancedTestCategory.AgentCompatibility,
            2.0d,
            "检查 tools 参数、tool_choice=auto 和 arguments JSON 是否可用。"))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = ToolPayload(context.Endpoint.Model, toolChoice: "auto");
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var inspection = InspectToolCall(exchange.ResponseBody, expectedName: "search_docs");
            var checks = BuildToolChecks(exchange, inspection);

            if (exchange.IsSuccessStatusCode && inspection.HasValidToolCall)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions with tools", "Tool Calling 基础链路通过。", checks, suggestions: new[] { "该入口具备 Agent 类客户端所需的基础 tools 调用能力。" });
            }

            var kind = exchange.IsSuccessStatusCode ? AdvancedErrorKind.ToolCallMalformed : ClassifyExchange(exchange);
            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions with tools", "Tool Calling 基础链路失败。", checks, kind);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    internal static string ToolPayload(string model, object toolChoice)
        => JsonSerializer.Serialize(new
        {
            model,
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

    internal static ToolCallInspection InspectToolCall(string? responseBody, string expectedName)
    {
        if (TryInspectFlexibleToolCall(responseBody, expectedName, out var flexibleInspection))
        {
            return flexibleInspection;
        }

        if (!TryParseJson(responseBody, out var document) || document is null)
        {
            return new ToolCallInspection(false, false, false, "-", "-", "响应不是合法 JSON。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return new ToolCallInspection(false, false, false, "-", "-", "响应缺少 choices。");
            }

            var message = choices[0].TryGetProperty("message", out var messageElement)
                ? messageElement
                : default;
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("tool_calls", out var toolCalls) ||
                toolCalls.ValueKind != JsonValueKind.Array ||
                toolCalls.GetArrayLength() == 0)
            {
                return new ToolCallInspection(false, false, false, "-", "-", "message.tool_calls 缺失。");
            }

            var toolCall = toolCalls[0];
            var name = toolCall.TryGetProperty("function", out var function) &&
                       function.TryGetProperty("name", out var nameElement) &&
                       nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var arguments = function.ValueKind == JsonValueKind.Object &&
                            function.TryGetProperty("arguments", out var argumentsElement) &&
                            argumentsElement.ValueKind == JsonValueKind.String
                ? argumentsElement.GetString() ?? string.Empty
                : string.Empty;

            var nameOk = string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase);
            var argsOk = false;
            if (TryParseJson(arguments, out var argsDocument) && argsDocument is not null)
            {
                using (argsDocument)
                {
                    var args = argsDocument.RootElement;
                    argsOk = args.TryGetProperty("query", out var query) &&
                             query.ValueKind == JsonValueKind.String &&
                             string.Equals(query.GetString(), "relay cache isolation", StringComparison.OrdinalIgnoreCase) &&
                             args.TryGetProperty("limit", out var limit) &&
                             limit.ValueKind == JsonValueKind.Number &&
                             limit.TryGetInt32(out var limitValue) &&
                             limitValue == 5;
                }
            }

            return new ToolCallInspection(true, nameOk, argsOk, name, arguments, nameOk && argsOk ? "工具名与参数正确。" : "工具名或参数不符合预期。");
        }
    }

    private static bool TryInspectFlexibleToolCall(
        string? responseBody,
        string expectedName,
        out ToolCallInspection inspection)
    {
        var evaluation = ToolCallProbeEvaluator.Evaluate(
            "ADV-TOOL",
            responseBody ?? string.Empty,
            [
                new ToolCallExpectation(
                    expectedName,
                    new Dictionary<string, object?>
                    {
                        ["query"] = "relay cache isolation",
                        ["limit"] = 5
                    })
            ]);

        var toolSelection = evaluation.Checks.FirstOrDefault(static item => item.Name == "ToolSelection");
        if (toolSelection is null ||
            string.Equals(toolSelection.Actual, "no tool_calls", StringComparison.OrdinalIgnoreCase))
        {
            inspection = new ToolCallInspection(false, false, false, "-", "-", evaluation.Error ?? evaluation.Summary);
            return true;
        }

        var argumentChecks = evaluation.Checks
            .Where(static item => item.Name.StartsWith("Argument:", StringComparison.Ordinal))
            .ToArray();
        var arguments = ExtractArgumentsFromPreview(evaluation.NormalizedPreview);
        inspection = new ToolCallInspection(
            true,
            toolSelection.Passed,
            argumentChecks.Length > 0 && argumentChecks.All(static item => item.Passed),
            string.IsNullOrWhiteSpace(toolSelection.Actual) ? ExtractNameFromPreview(evaluation.NormalizedPreview) : toolSelection.Actual,
            string.IsNullOrWhiteSpace(arguments) ? "-" : arguments,
            evaluation.Success ? "工具名与参数正确。" : evaluation.Error ?? evaluation.Summary);
        return true;
    }

    private static string ExtractNameFromPreview(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return "-";
        }

        var openParen = preview.IndexOf('(', StringComparison.Ordinal);
        return openParen > 0 ? preview[..openParen] : preview;
    }

    private static string ExtractArgumentsFromPreview(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return "-";
        }

        var openParen = preview.IndexOf('(', StringComparison.Ordinal);
        var closeParen = preview.LastIndexOf(')');
        return openParen >= 0 && closeParen > openParen
            ? preview[(openParen + 1)..closeParen]
            : preview;
    }

    internal static IReadOnlyList<AdvancedCheckResult> BuildToolChecks(
        AdvancedModelExchange exchange,
        ToolCallInspection inspection)
        =>
        [
            new("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "请求成功。" : "请求失败。"),
            new("ToolCalls", inspection.HasToolCall, "message.tool_calls[]", inspection.HasToolCall ? "present" : "-", inspection.Detail),
            new("ToolName", inspection.NameMatches, "search_docs", inspection.ActualName, inspection.NameMatches ? "函数名正确。" : "函数名不匹配。"),
            new("Arguments", inspection.ArgumentsValid, "{\"query\":\"relay cache isolation\",\"limit\":5}", inspection.ActualArguments, inspection.ArgumentsValid ? "arguments 可解析且参数正确。" : "arguments 缺失、不可解析或值不匹配。")
        ];

    internal sealed record ToolCallInspection(
        bool HasToolCall,
        bool NameMatches,
        bool ArgumentsValid,
        string ActualName,
        string ActualArguments,
        string Detail)
    {
        public bool HasValidToolCall => HasToolCall && NameMatches && ArgumentsValid;
    }
}

public sealed class ToolChoiceForcedTestCase : AdvancedTestCaseBase
{
    public ToolChoiceForcedTestCase()
        : base(new AdvancedTestCaseDefinition(
            "tool_choice_forced",
            "指定 Tool Choice",
            AdvancedTestCategory.AgentCompatibility,
            1.5d,
            "检查 tool_choice 指定函数时是否仍能返回合法 tool_calls。"))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var toolChoice = new
            {
                type = "function",
                function = new { name = "search_docs" }
            };
            var body = ToolCallingBasicTestCase.ToolPayload(context.Endpoint.Model, toolChoice);
            var exchange = await client.PostJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
            var inspection = ToolCallingBasicTestCase.InspectToolCall(exchange.ResponseBody, "search_docs");
            var checks = ToolCallingBasicTestCase.BuildToolChecks(exchange, inspection);

            if (exchange.IsSuccessStatusCode && inspection.HasValidToolCall)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions with forced tool_choice", "指定 tool_choice 通过。", checks, suggestions: new[] { "强制工具调用可用，Agent 在需要指定函数时兼容性较好。" });
            }

            var kind = exchange.IsSuccessStatusCode ? AdvancedErrorKind.ToolCallMalformed : ClassifyExchange(exchange);
            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /chat/completions with forced tool_choice", "指定 tool_choice 失败。", checks, kind);
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}
