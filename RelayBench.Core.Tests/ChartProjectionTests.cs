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

internal static class ChartProjectionTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("stability snapshot counts all semantic extension probes", () =>
    {
        var service = new ProxyDiagnosticsService();
        var settings = new ProxyEndpointSettings(
            "https://relay.example.com/v1",
            "sk-test",
            "gpt-test",
            false,
            20);
        var rounds = new[]
        {
            CreateProxyDiagnosticsResult([CreateScenario(ProxyProbeScenarioKind.InstructionFollowing, "Instruction")]),
            CreateProxyDiagnosticsResult([CreateScenario(ProxyProbeScenarioKind.DataExtraction, "Data")]),
            CreateProxyDiagnosticsResult([CreateFailedScenario(ProxyProbeScenarioKind.StructuredOutputEdge, "Structured Edge")]),
            CreateProxyDiagnosticsResult([CreateFailedScenario(ProxyProbeScenarioKind.ReasonMathConsistency, "ReasonMath")])
        };

        var snapshot = service.BuildStabilitySnapshot(settings, 4, 0, rounds);

        AssertTrue(Math.Abs(snapshot.SemanticStabilityRate - 50d) < 0.001d, $"Expected semantic stability 50, got {snapshot.SemanticStabilityRate}.");
        });

        yield return new TestCase("single capability live chart keeps pending throughput in final slot", () =>
    {
        var result = CreateProxyDiagnosticsResult(
        [
            CreateScenario(ProxyProbeScenarioKind.SystemPromptMapping, "System Prompt")
        ]);
        var items = BuildSupplementalPhaseSingleCapabilityChartItems(result);
        var names = items
            .OrderBy(item => item.Order)
            .Select(item => item.Name)
            .ToList();
        var throughputIndex = names.IndexOf("独立吞吐");
        var systemPromptIndex = names.IndexOf("System Prompt");

        AssertTrue(throughputIndex >= 0, "Pending throughput row must be present.");
        AssertTrue(systemPromptIndex >= 0, "Completed supplemental row must be present.");
        AssertTrue(
            throughputIndex < systemPromptIndex,
            $"Pending throughput must stay before deep supplemental rows, actual order: {string.Join(" > ", names)}");
        });

        yield return new TestCase("single capability live chart preallocates selected semantic deep probes", () =>
    {
        var result = CreateProxyDiagnosticsResult(
        [
            CreateScenario(ProxyProbeScenarioKind.SystemPromptMapping, "System Prompt")
        ]);
        var items = BuildDeepSupplementalPhaseSingleCapabilityChartItems(result);
        var ranks = items
            .Select(ResolveSingleCapabilityChartItemRank)
            .ToHashSet();

        AssertTrue(ranks.Contains(380), "StructuredOutputEdge pending row must be visible before it completes.");
        AssertTrue(ranks.Contains(390), "ToolCallDeep pending row must be visible before it completes.");
        AssertTrue(ranks.Contains(400), "ReasonMathConsistency pending row must be visible before it completes.");
        AssertTrue(ranks.Contains(410), "CodeBlockDiscipline pending row must be visible before it completes.");
        });

        yield return new TestCase("single capability chart model factory normalizes supplemental item order", () =>
    {
        var items = new[]
        {
            new ProxySingleCapabilityChartItem(20, "深度测试", "", "ToolCall 深测", "等待", false, false, null, null, "", false, "", ""),
            new ProxySingleCapabilityChartItem(10, "增强测试", "", "独立吞吐", "等待", false, false, null, null, "", false, "", "")
        };

        var normalized = ProxySingleCapabilityChartModelFactory.NormalizeItems(items, Array.Empty<string>());

        AssertEqual(normalized[0].Name, "独立吞吐");
        AssertEqual(normalized[1].Name, "ToolCall 深测");
        AssertTrue(normalized[0].Order == 1 && normalized[1].Order == 2, "Normalized order should be compact and stable.");
        });

        yield return new TestCase("single capability chart keeps semantic probe order after completion", () =>
    {
        var pending = ProxySingleCapabilityChartModelFactory.NormalizeItems(
        [
            new ProxySingleCapabilityChartItem(42, "真实使用体验", "", "代码块纪律", "等待", false, false, null, null, "", false, "", ""),
            new ProxySingleCapabilityChartItem(41, "语义稳定性", "", "推理一致性", "等待", false, false, null, null, "", false, "", ""),
            new ProxySingleCapabilityChartItem(40, "应用接入可用性", "", "ToolCall 深测", "等待", false, false, null, null, "", false, "", ""),
            new ProxySingleCapabilityChartItem(39, "结构化输出", "", "结构化边界", "等待", false, false, null, null, "", false, "", "")
        ], []);
        var completed = ProxySingleCapabilityChartModelFactory.NormalizeItems(
        [
            new ProxySingleCapabilityChartItem(4, "真实使用体验", "", "代码块纪律", "异常", true, false, 200, 120, "", false, "", ""),
            new ProxySingleCapabilityChartItem(1, "结构化输出", "", "结构化边界", "支持", true, true, 200, 95, "", false, "", ""),
            new ProxySingleCapabilityChartItem(3, "语义稳定性", "", "推理一致性", "支持", true, true, 200, 105, "", false, "", ""),
            new ProxySingleCapabilityChartItem(2, "应用接入可用性", "", "ToolCall 深测", "支持", true, true, 200, 100, "", false, "", "")
        ], []);

        var pendingNames = string.Join(">", pending.Select(static item => item.Name));
        var completedNames = string.Join(">", completed.Select(static item => item.Name));
        AssertEqual(completedNames, pendingNames);
        AssertEqual(completedNames, "结构化边界>ToolCall 深测>推理一致性>代码块纪律");
        });
    }
}
