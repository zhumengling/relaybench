using RelayBench.Core.Services;
using RelayBench.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.App.Services;
using RelayBench.App.ViewModels;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class ToolCallProbeEvaluatorTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("tool call deep accepts correct tool and arguments", () =>
    {
        var result = ToolCallProbeEvaluator.Evaluate(
            "TC-DEEP-01",
            """{"choices":[{"message":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"search_docs","arguments":"{\"query\":\"relay cache isolation\",\"limit\":5}"}}]}}]}""",
            [new ToolCallExpectation("search_docs", new Dictionary<string, object?> { ["query"] = "relay cache isolation", ["limit"] = 5 })]);

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("tool call deep rejects missing tool calls", () =>
    {
        var result = ToolCallProbeEvaluator.Evaluate(
            "TC-DEEP-01",
            """{"choices":[{"message":{"content":"I would search docs."}}]}""",
            [new ToolCallExpectation("search_docs", new Dictionary<string, object?> { ["query"] = "relay cache isolation", ["limit"] = 5 })]);

        AssertFalse(result.Success, "Missing tool_calls must fail.");
        });

        yield return new TestCase("tool call deep rejects wrong tool", () =>
    {
        var result = ToolCallProbeEvaluator.Evaluate(
            "TC-DEEP-01",
            """{"choices":[{"message":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"create_ticket","arguments":"{\"query\":\"relay cache isolation\",\"limit\":5}"}}]}}]}""",
            [new ToolCallExpectation("search_docs", new Dictionary<string, object?> { ["query"] = "relay cache isolation", ["limit"] = 5 })]);

        AssertFalse(result.Success, "Wrong tool name must fail.");
        });

        yield return new TestCase("tool call deep rejects argument type drift", () =>
    {
        var result = ToolCallProbeEvaluator.Evaluate(
            "TC-DEEP-01",
            """{"choices":[{"message":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"search_docs","arguments":"{\"query\":\"relay cache isolation\",\"limit\":\"5\"}"}}]}}]}""",
            [new ToolCallExpectation("search_docs", new Dictionary<string, object?> { ["query"] = "relay cache isolation", ["limit"] = 5 })]);

        AssertFalse(result.Success, "limit must stay numeric.");
        });

        yield return new TestCase("tool call deep compares string arguments case insensitively", () =>
    {
        var result = ToolCallProbeEvaluator.Evaluate(
            "TC-DEEP-01",
            """{"choices":[{"message":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"search_docs","arguments":"{\"query\":\"Relay Cache Isolation\",\"limit\":5}"}}]}}]}""",
            [new ToolCallExpectation("search_docs", new Dictionary<string, object?> { ["query"] = "relay cache isolation", ["limit"] = 5 })]);

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("tool call deep accepts anthropic tool_use shape", () =>
    {
        var result = ToolCallProbeEvaluator.Evaluate(
            "TC-DEEP-01",
            """{"content":[{"type":"tool_use","id":"toolu_1","name":"search_docs","input":{"query":"relay cache isolation","limit":5}}]}""",
            [new ToolCallExpectation("search_docs", new Dictionary<string, object?> { ["query"] = "relay cache isolation", ["limit"] = 5 })]);

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("tool call deep accepts responses function_call shape", () =>
    {
        var result = ToolCallProbeEvaluator.Evaluate(
            "TC-DEEP-01",
            """{"output":[{"type":"function_call","call_id":"call_1","name":"search_docs","arguments":"{\"query\":\"relay cache isolation\",\"limit\":5}"}]}""",
            [new ToolCallExpectation("search_docs", new Dictionary<string, object?> { ["query"] = "relay cache isolation", ["limit"] = 5 })]);

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("function calling parser accepts object arguments from compatible relays", () =>
    {
        var (success, argumentsJson) = TryParseFunctionCallingToolResponse(
            """{"choices":[{"message":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"emit_probe_result","arguments":{"status":"proxy-ok","channel":"function-calling","round":1}}}]}}]}""");

        AssertTrue(success, "Object-shaped function arguments should still be accepted.");
        AssertContains(argumentsJson, "\"status\":\"proxy-ok\"");
        AssertContains(argumentsJson, "\"round\":1");
        });

        yield return new TestCase("tool call deep payload is served by shared payload factory", () =>
    {
        var fromFactory = ProxyProbePayloadFactory.BuildToolCallDeepPayload("gpt-test", "TC-DEEP-01");
        var fromDiagnosticsWrapper = BuildToolCallDeepPayload("gpt-test");

        AssertEqual(fromFactory, fromDiagnosticsWrapper);
        });

        yield return new TestCase("tool call deep payload gives every tool a properties schema", () =>
    {
        using var document = JsonDocument.Parse(BuildToolCallDeepPayload("gpt-test"));
        var tools = document.RootElement.GetProperty("tools").EnumerateArray().ToArray();

        AssertTrue(tools.Length > 1, "ToolCall deep payload should keep distractor tools.");
        foreach (var tool in tools)
        {
            var function = tool.GetProperty("function");
            var name = function.GetProperty("name").GetString() ?? "<unnamed>";
            var parameters = function.GetProperty("parameters");
            AssertTrue(parameters.TryGetProperty("properties", out var properties), $"{name} schema must include properties.");
            AssertTrue(properties.ValueKind == JsonValueKind.Object, $"{name} properties must be an object.");
        }
        });
    }
}
