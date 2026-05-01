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

internal static class ProbeTraceTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("probe trace redactor masks headers urls and json body", () =>
    {
        var headers = ProbeTraceRedactor.RedactHeaders([
            "Authorization: Bearer sk-1234567890abcdef",
            "x-api-key: rb-secret-key-value"
        ]);
        var url = ProbeTraceRedactor.RedactUrl("https://relay.example.com/v1/chat/completions?key=abc123&token=def456&safe=ok");
        var body = ProbeTraceRedactor.RedactJsonBody("""{"model":"gpt-test","api_key":"secret","messages":[{"role":"user","content":"hello"}]}""");

        AssertContains(headers[0], "Bearer sk-");
        AssertFalse(string.Join("\n", headers).Contains("1234567890abcdef", StringComparison.Ordinal), "Authorization value must be masked.");
        AssertFalse(string.Join("\n", headers).Contains("rb-secret-key-value", StringComparison.Ordinal), "API key header must be masked.");
        AssertContains(url, "key=***");
        AssertContains(url, "token=***");
        AssertContains(body, "\"api_key\":\"***\"");
        AssertContains(body, "\"model\":\"gpt-test\"");
        }, group: "trace");

        yield return new TestCase("probe trace redactor handles empty headers and urls without query", () =>
    {
        var headers = ProbeTraceRedactor.RedactHeaders([null!, "", "Bearer sk-1234567890abcdef"]);
        var url = ProbeTraceRedactor.RedactUrl("https://relay.example.com/v1/models");

        AssertTrue(headers.Count == 3, $"Expected 3 headers, got {headers.Count}.");
        AssertEqual(headers[0], string.Empty);
        AssertEqual(headers[1], string.Empty);
        AssertFalse(headers[2].Contains("1234567890abcdef", StringComparison.Ordinal), "Bare bearer header text should be masked.");
        AssertEqual(url, "https://relay.example.com/v1/models");
        }, group: "trace");

        yield return new TestCase("probe trace redactor masks nested json and summarizes large binary strings", () =>
    {
        var binary = new string('A', 640);
        var body = ProbeTraceRedactor.RedactJsonBody(
            $$"""
            {
              "outer": {
                "token": "nested-secret",
                "items": [
                  { "api_key": "array-secret" },
                  { "image": "{{binary}}" }
                ]
              }
            }
            """);

        AssertContains(body, "\"token\":\"***\"");
        AssertContains(body, "\"api_key\":\"***\"");
        AssertContains(body, "binary:640 chars:");
        AssertFalse(body.Contains("nested-secret", StringComparison.Ordinal), "Nested token must be masked.");
        AssertFalse(body.Contains("array-secret", StringComparison.Ordinal), "Array item secret must be masked.");
        }, group: "trace");

        yield return new TestCase("probe trace dialog explains failed verdict before raw trace", () =>
    {
        var content = BuildProbeTraceDialogContent(CreateProbeTrace(success: false));

        AssertContains(content, "[判定解读]");
        AssertContains(content, "为什么失败");
        AssertContains(content, "字段匹配");
        AssertOrder(content, "为什么失败", "[原始 Trace]");
        AssertOrder(content, "[关键证据]", "[原始 Trace]");
        }, group: "trace");

        yield return new TestCase("probe trace dialog explains successful verdict before raw trace", () =>
    {
        var content = BuildProbeTraceDialogContent(CreateProbeTrace(success: true));

        AssertContains(content, "[判定解读]");
        AssertContains(content, "为什么通过");
        AssertContains(content, "所有自动判定项均通过");
        AssertOrder(content, "为什么通过", "[原始 Trace]");
        }, group: "trace");

        yield return new TestCase("probe trace dialog explains invalid function schema specifically", () =>
    {
        var content = BuildProbeTraceDialogContent(CreateInvalidFunctionSchemaTrace());

        AssertContains(content, "工具 schema");
        AssertContains(content, "执行前拒绝");
        AssertOrder(content, "工具 schema", "[原始 Trace]");
        }, group: "trace");
    }
}
