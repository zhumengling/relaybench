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

namespace RelayBench.Core.Tests;

internal static partial class TestSupport
{
    internal static ProxyDiagnosticsResult CreateProxyDiagnosticsResult(IReadOnlyList<ProxyProbeScenarioResult> scenarios)
        => new(
            DateTimeOffset.Now,
            "https://relay.example.com",
            "gpt-test",
            "gpt-test",
            true,
            200,
            1,
            ["gpt-test"],
            TimeSpan.FromMilliseconds(20),
            true,
            200,
            TimeSpan.FromMilliseconds(100),
            "ok",
            true,
            200,
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(500),
            "stream ok",
            "测试结果",
            null,
            scenarios);

    internal static ProxyProbeScenarioResult CreateScenario(ProxyProbeScenarioKind kind, string displayName)
        => new(
            kind,
            displayName,
            "通过",
            true,
            200,
            TimeSpan.FromMilliseconds(100),
            null,
            TimeSpan.FromMilliseconds(100),
            true,
            1,
            true,
            $"{displayName} 通过",
            $"{displayName} preview",
            null,
            null,
            null);

    internal static ProxyProbeScenarioResult CreateFailedScenario(ProxyProbeScenarioKind kind, string displayName)
        => CreateScenario(kind, displayName) with
        {
            CapabilityStatus = "寮傚父",
            Success = false,
            SemanticMatch = false,
            Summary = $"{displayName} failed",
            FailureKind = ProxyFailureKind.SemanticMismatch,
            Error = $"{displayName} failed"
        };

    internal static ProxyBatchEditorItemViewModel CreateBatchTemplateRow(
        string entryName,
        string baseUrl,
        bool includeInBatchTest)
        => new(
            entryName,
            baseUrl,
            null,
            null,
            null,
            null,
            null,
            includeInBatchTest);

    internal static ProxyProbeTrace CreateProbeTrace(bool success)
        => new(
            "ChatCompletions",
            "普通对话",
            "https://relay.example.com",
            "v1/chat/completions",
            "gpt-test",
            "OpenAI Chat Completions",
            """{"model":"gpt-test","messages":[{"role":"user","content":"Reply with exactly: proxy-ok"}]}""",
            ["Authorization: Bearer ***", "Content-Type: application/json"],
            success ? 200 : 200,
            success ? """{"choices":[{"message":{"content":"proxy-ok"}}]}""" : """{"choices":[{"message":{"content":"proxy-not-ok"}}]}""",
            ["server: test"],
            success ? "proxy-ok" : "proxy-not-ok",
            [
                new ProxyProbeEvaluationCheck(
                    "字段匹配",
                    success,
                    "proxy-ok",
                    success ? "proxy-ok" : "proxy-not-ok",
                    success ? "输出完全符合预期。" : "输出没有严格匹配要求文本。")
            ],
            success ? "通过" : "异常",
            success ? null : "模型输出与期望不一致。",
            "req-1",
            "trace-1",
            1200,
            null,
            1200);

    internal static ProxyProbeTrace CreateInvalidFunctionSchemaTrace()
        => new(
            "ToolCallDeep",
            "ToolCall 深测",
            "https://relay.example.com/v1",
            "chat/completions",
            "gpt-test",
            "OpenAI Chat Completions",
            """{"model":"gpt-test","tools":[{"type":"function","function":{"name":"create_ticket","parameters":{"type":"object"}}}]}""",
            ["Authorization: Bearer ***", "Content-Type: application/json"],
            400,
            """
            {
              "error": {
                "message": "Invalid schema for function 'create_ticket': In context=(), object schema missing properties.",
                "type": "invalid_request_error",
                "param": "tools[1].parameters",
                "code": "invalid_function_parameters"
              }
            }
            """,
            ["content-type: application/json"],
            null,
            [],
            "异常",
            "POST chat/completions 返回 400 Bad Request。Invalid schema for function 'create_ticket': object schema missing properties.",
            null,
            null,
            238,
            null,
            null);

    internal static IReadOnlyDictionary<string, int> ReadMainWindowOverlayZIndexes()
    {
        var xamlPath = Path.Combine(FindRepositoryRoot(), "RelayBench.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);
        var overlays = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(
                     xaml,
                     "<Grid\\s+x:Name=\"(?<name>[^\"]+Overlay)\"(?<attributes>[^>]*)>",
                     RegexOptions.Singleline))
        {
            var zIndexMatch = Regex.Match(match.Groups["attributes"].Value, "Panel\\.ZIndex=\"(?<z>[0-9]+)\"");
            if (zIndexMatch.Success)
            {
                overlays[match.Groups["name"].Value] = int.Parse(
                    zIndexMatch.Groups["z"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return overlays;
    }

    internal static IReadOnlyDictionary<string, string> ReadMainWindowOverlayPanelStyles()
    {
        var xamlPath = Path.Combine(FindRepositoryRoot(), "RelayBench.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);
        var styles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(
                     xaml,
                     "<Border\\s+x:Name=\"(?<name>[^\"]+OverlayPanel)\"(?<attributes>[^>]*)>",
                     RegexOptions.Singleline))
        {
            var styleMatch = Regex.Match(
                match.Groups["attributes"].Value,
                "Style=\"\\{StaticResource (?<style>[^}]+)\\}\"");
            if (styleMatch.Success)
            {
                styles[match.Groups["name"].Value] = styleMatch.Groups["style"].Value;
            }
        }

        return styles;
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RelayBenchSuite.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate RelayBenchSuite.slnx.");
    }
}
