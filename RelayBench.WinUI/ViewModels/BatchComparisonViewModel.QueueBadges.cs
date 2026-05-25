using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Security.Cryptography;
using System.Text;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class BatchComparisonViewModel
{
    private static void InitializeTestBadges(DeepTestQueueItem item, BatchRunMode runMode)
    {
        item.TestBadges.Clear();
        AddBadge(item, "B5", "0/5", GetBadgeDescription("B5"));
        AddBadge(item, "TP", "--", GetBadgeDescription("TP"));

        if (runMode == BatchRunMode.Deep)
        {
            foreach (var (_, label) in DeepSupplementalScenarioBadges)
            {
                AddBadge(item, label, "--", GetBadgeDescription(label));
                if (label == "TC")
                {
                    AddBadge(item, "RM", "Off", GetBadgeDescription("RM"), "Off");
                }
            }

            AddBadge(item, "ST", $"0/{DeepStabilityRounds}", GetBadgeDescription("ST"));
        }
    }

    private static void AddBadge(
        DeepTestQueueItem item,
        string label,
        string value,
        string tooltip,
        string tone = "Pending",
        string? title = null,
        string? description = null,
        string? detailText = null)
        => item.TestBadges.Add(new DeepTestBadgeItem(
            label,
            value,
            tooltip,
            tone,
            title ?? GetBadgeTitle(label),
            description ?? GetBadgeDescription(label),
            detailText ?? string.Empty));

    private static void UpdateBadge(
        DeepTestQueueItem item,
        string label,
        string value,
        string tooltip,
        string? tone = null,
        string? title = null,
        string? description = null,
        string? detailText = null)
    {
        var resolvedTitle = title ?? GetBadgeTitle(label);
        var resolvedDescription = description ?? GetBadgeDescription(label);
        var resolvedDetailText = detailText ?? string.Empty;
        var badge = item.TestBadges.FirstOrDefault(badge =>
            string.Equals(badge.Label, label, StringComparison.OrdinalIgnoreCase));
        if (badge is null)
        {
            item.TestBadges.Add(new DeepTestBadgeItem(
                label,
                value,
                tooltip,
                tone ?? ResolveBadgeTone(value),
                resolvedTitle,
                resolvedDescription,
                resolvedDetailText));
            return;
        }

        badge.Value = value;
        badge.Tooltip = tooltip;
        badge.Title = resolvedTitle;
        badge.Description = resolvedDescription;
        badge.DetailText = resolvedDetailText;
        badge.Tone = tone ?? ResolveBadgeTone(value);
    }

    private static void ApplyScenarioBadges(
        DeepTestQueueItem item,
        IReadOnlyList<ProxyProbeScenarioResult> scenarios,
        bool includeDeepBadges)
    {
        var baselinePassed = DeepBaselineCapabilityKinds.Count(kind => FindScenario(scenarios, kind)?.Success == true);
        var baselineSeen = DeepBaselineCapabilityKinds.Count(kind => FindScenario(scenarios, kind) is not null);
        var baselineDetail = BuildBaselineBadgeDetail(scenarios);
        UpdateBadge(
            item,
            "B5",
            $"{baselinePassed}/{DeepBaselineCapabilityKinds.Length}",
            BuildBadgeTooltip(GetBadgeDescription("B5"), baselineDetail),
            baselineSeen < DeepBaselineCapabilityKinds.Length ? "Running" : baselinePassed == DeepBaselineCapabilityKinds.Length ? "Pass" : "Warn",
            detailText: baselineDetail);

        if (!includeDeepBadges)
        {
            return;
        }

        foreach (var (kind, label) in DeepSupplementalScenarioBadges)
        {
            var scenario = FindScenario(scenarios, kind);
            if (scenario is null)
            {
                continue;
            }

            var labelDescription = GetBadgeDescription(label);
            var detailText = BuildScenarioBadgeDetail(kind, scenario);
            UpdateBadge(
                item,
                label,
                ResolveScenarioBadgeValue(scenario),
                BuildBadgeTooltip(labelDescription, detailText),
                description: labelDescription,
                detailText: detailText);
        }
    }

    private static void ApplyThroughputBadge(DeepTestQueueItem item, ProxyThroughputBenchmarkResult benchmark)
    {
        var value = benchmark.SuccessfulSampleCount > 0
            ? FormatTokensPerSecond(benchmark.MedianOutputTokensPerSecond ?? benchmark.AverageOutputTokensPerSecond)
            : "NO";
        var tone = benchmark.SuccessfulSampleCount > 0 ? "Pass" : "Fail";
        UpdateBadge(
            item,
            "TP",
            value,
            $"独立吞吐：完成 {benchmark.SuccessfulSampleCount}/{benchmark.CompletedSampleCount} 轮采样。{benchmark.Summary}",
            tone);
    }

    private static void ApplyStabilityBadge(DeepTestQueueItem item, ProxyStabilityResult stability)
    {
        var tone = stability.CompletedRounds < stability.RequestedRounds
            ? "Warn"
            : stability.FullSuccessCount == stability.CompletedRounds ? "Pass" : "Warn";
        UpdateBadge(
            item,
            "ST",
            $"{stability.FullSuccessCount}/{Math.Max(stability.CompletedRounds, stability.RequestedRounds)}",
            $"稳定复测：{stability.Summary} 健康度 {stability.HealthScore}/100，平均普通 {FormatNullableMilliseconds(stability.AverageChatLatency)}，平均 TTFT {FormatNullableMilliseconds(stability.AverageStreamFirstTokenLatency)}。",
            tone);
    }

    private static string BuildBaselineBadgeDetail(IReadOnlyList<ProxyProbeScenarioResult> scenarios)
    {
        var lines = DeepBaselineCapabilityKinds
            .Select(kind =>
            {
                var scenario = FindScenario(scenarios, kind);
                return $"{ResolveScenarioShortName(kind)}：{FormatScenarioShortStatus(scenario)}";
            });

        return string.Join("\n", lines);
    }

    private static string BuildScenarioBadgeDetail(
        ProxyProbeScenarioKind kind,
        ProxyProbeScenarioResult scenario)
    {
        var preview = NormalizeBadgeText(scenario.Preview);
        var summary = NormalizeBadgeText(scenario.Summary);
        var capabilityStatus = NormalizeBadgeText(scenario.CapabilityStatus);
        var error = NormalizeBadgeText(scenario.Error);
        var actualParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(capabilityStatus))
        {
            actualParts.Add($"状态：{TrimBadgeText(capabilityStatus, 120)}");
        }

        if (scenario.StatusCode.HasValue)
        {
            actualParts.Add($"HTTP {scenario.StatusCode.Value}");
        }

        AddMetricIfPresent(actualParts, "延迟", scenario.Latency);
        AddMetricIfPresent(actualParts, "TTFT", scenario.FirstTokenLatency);

        if (scenario.ReceivedDone)
        {
            actualParts.Add("DONE 已收到");
        }

        if (scenario.ChunkCount > 0)
        {
            actualParts.Add($"分片 {scenario.ChunkCount}");
        }

        if (scenario.OutputTokensPerSecond is > 0)
        {
            actualParts.Add($"速率 {scenario.OutputTokensPerSecond.Value:F1} tok/s");
        }

        if (!string.IsNullOrWhiteSpace(preview))
        {
            actualParts.Add($"返回片段：{TrimBadgeText(preview, 180)}");
        }

        if (!string.IsNullOrWhiteSpace(summary) &&
            !string.Equals(summary, preview, StringComparison.Ordinal))
        {
            actualParts.Add($"观察：{TrimBadgeText(summary, 180)}");
        }

        if (!string.IsNullOrWhiteSpace(scenario.RequestId))
        {
            actualParts.Add($"RequestId {TrimBadgeText(scenario.RequestId!, 80)}");
        }

        if (!string.IsNullOrWhiteSpace(scenario.TraceId))
        {
            actualParts.Add($"TraceId {TrimBadgeText(scenario.TraceId!, 80)}");
        }

        var actual = actualParts.Count == 0
            ? "未提供可用返回片段。"
            : string.Join("；", actualParts);

        var reason = FirstNonEmpty(error, summary, capabilityStatus) ??
                     (scenario.Success ? "返回结果与预期一致。" : "未提供额外原因。");
        var reasonLabel = scenario.Success ? "说明：" : "原因：";

        return string.Join(
            "\n",
            ResolveScenarioResultText(scenario),
            $"预期：{GetScenarioExpectedText(kind)}",
            $"实际：{actual}",
            $"{reasonLabel}{TrimBadgeText(reason, 220)}");
    }

    private static string BuildBadgeTooltip(string description, string detailText)
        => string.IsNullOrWhiteSpace(detailText)
            ? description
            : $"{description}\n{detailText}";

    private static string FormatScenarioShortStatus(ProxyProbeScenarioResult? scenario)
        => scenario is null ? "--" : scenario.Success ? "通过" : scenario.CapabilityStatus;

    private static string ResolveScenarioShortName(ProxyProbeScenarioKind kind)
        => kind switch
        {
            ProxyProbeScenarioKind.Models => "/models",
            ProxyProbeScenarioKind.ChatCompletions => "普通",
            ProxyProbeScenarioKind.ChatCompletionsStream => "流式",
            ProxyProbeScenarioKind.Responses => "Responses",
            ProxyProbeScenarioKind.StructuredOutput => "结构化",
            _ => kind.ToString()
        };

    private static string GetBadgeDescription(string label)
        => label switch
        {
            "B5" => "基础 5 项：/models、普通对话、流式对话、Responses、结构化输出。Anthropic 协议会在基础探测中执行，但不计入 B5。",
            "TP" => "独立吞吐：单独发长输出流式请求，统计 tok/s 中位数/均值。",
            "Sys" => "System Prompt 映射：检查中转是否正确保留 system 指令。",
            "Fn" => "Function Calling：检查工具定义、工具调用和后续消息是否兼容。",
            "Err" => "错误透传：检查上游错误是否保留状态码、错误类型和可诊断信息。",
            "Str" => "流式完整性：对照流式和非流式输出，检查内容与 DONE 事件。",
            "MM" => "多模态：检查图像/多内容块请求是否能通过当前入口。",
            "Cch" => "缓存机制：检查相同提示下缓存命中、响应一致性和可追踪性。",
            "IF" => "指令遵循：检查 system 约束、禁止项和 JSON 字段是否稳定遵循。",
            "DE" => "数据抽取：检查模型能否稳定抽取结构化字段。",
            "SO" => "结构化边界：检查 JSON/CSV/转义字符/类型边界是否可解析。",
            "TC" => "ToolCall 深测：检查复杂工具参数和工具调用结构。",
            "RM" => "推理一致性：标准批量深测默认关闭，避免额外耗时；单站自定义深测可单独打开。",
            "CB" => "代码块纪律：检查代码块语言、围栏和纯 JSON 输出约束。",
            "ST" => "稳定复测：连续 5 轮基础测试，观察成功率、TTFT 和健康度。",
            _ => "该标签表示本轮批量测试中的一个专项探针。"
        };

    private static string GetBadgeTitle(string label)
        => label switch
        {
            "B5" => "基础 5 项",
            "TP" => "独立吞吐",
            "Sys" => "System Prompt 映射",
            "Fn" => "Function Calling",
            "Err" => "错误透传",
            "Str" => "流式完整性",
            "MM" => "多模态",
            "Cch" => "缓存机制",
            "IF" => "指令遵循",
            "DE" => "数据抽取",
            "SO" => "结构化边界",
            "TC" => "ToolCall 深测",
            "RM" => "推理一致性",
            "CB" => "代码块纪律",
            "ST" => "稳定复测",
            _ => label
        };

    private static string ResolveScenarioResultText(ProxyProbeScenarioResult scenario)
        => ResolveScenarioBadgeValue(scenario) switch
        {
            "OK" => "结果：通过",
            "RV" => "结果：待复核",
            "CFG" => "结果：配置不足",
            "SK" => "结果：已跳过",
            "--" => "结果：未开始",
            _ => "结果：未通过"
        };

    private static string GetScenarioExpectedText(ProxyProbeScenarioKind kind)
        => kind switch
        {
            ProxyProbeScenarioKind.Models or
            ProxyProbeScenarioKind.ChatCompletions or
            ProxyProbeScenarioKind.ChatCompletionsStream or
            ProxyProbeScenarioKind.Responses or
            ProxyProbeScenarioKind.StructuredOutput
                => "应保持基础连通性：/models、普通对话、流式对话、Responses、结构化输出都应返回有效结果。",
            ProxyProbeScenarioKind.SystemPromptMapping
                => "模型应稳定遵循 system 指令，不应被用户内容覆盖或带偏。",
            ProxyProbeScenarioKind.FunctionCalling
                => "应返回合法的 tool_calls / function calling 结构，工具名称、参数和后续消息都应兼容。",
            ProxyProbeScenarioKind.ErrorTransparency
                => "构造 bad request 后，应返回 4xx，并保留可读、可定位的原始错误信息。",
            ProxyProbeScenarioKind.StreamingIntegrity
                => "流式输出应完整收尾，非流式与流式核心内容应一致，不应截断、乱序或异常结束。",
            ProxyProbeScenarioKind.OfficialReferenceIntegrity
                => "中转站与官方接口对同一提示应表现一致，关键输出不应明显偏离。",
            ProxyProbeScenarioKind.MultiModal
                => "图像或多内容块请求应被正确识别，返回内容需要明确区分输入素材。",
            ProxyProbeScenarioKind.CacheMechanism
                => "重复请求应返回预期内容，并根据 TTFT 或缓存标记体现合理缓存迹象。",
            ProxyProbeScenarioKind.CacheIsolation
                => "不同 cache key 的请求应彼此隔离，不应读取到另一组提示中的敏感内容。",
            ProxyProbeScenarioKind.InstructionFollowing
                => "System 约束、禁止项和 JSON 字段应被稳定遵循，不应被 user 指令覆盖。",
            ProxyProbeScenarioKind.DataExtraction
                => "中文实体、金额、日期、URL、null 字段和明细数组应被稳定抽取，不应发生事实漂移。",
            ProxyProbeScenarioKind.StructuredOutputEdge
                => "JSON、CSV、转义字符和类型边界应保持可解析，不应被 Markdown 包裹或发生类型漂移。",
            ProxyProbeScenarioKind.ToolCallDeep
                => "应返回真实 tool_calls，工具名称和参数类型、枚举、数组值都应匹配预期。",
            ProxyProbeScenarioKind.ReasonMathConsistency
                => "固定答案任务应返回严格的结果行，答案和校验项都应与标准答案一致。",
            ProxyProbeScenarioKind.CodeBlockDiscipline
                => "应只返回一个带正确语言标签的代码块，修复点明确，且不夹带额外解释文字。",
            _ => "应返回与该专项能力一致的预期结果。"
        };

    private static void AddMetricIfPresent(List<string> parts, string label, TimeSpan? value)
    {
        if (value is { TotalMilliseconds: > 0 } timeSpan)
        {
            parts.Add($"{label} {timeSpan.TotalMilliseconds:F0} ms");
        }
    }

    private static string NormalizeBadgeText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();

    private static string TrimBadgeText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(1, maxLength - 1)] + "…";
    }

    private static string ResolveBadgeTone(string value)
        => value switch
        {
            "OK" => "Pass",
            "Off" => "Off",
            "SK" => "Off",
            "NO" => "Fail",
            "ER" => "Fail",
            "CFG" => "Warn",
            "RV" => "Warn",
            "--" => "Pending",
            _ when value.EndsWith("tok/s", StringComparison.Ordinal) => "Pass",
            _ when value.Contains('/', StringComparison.Ordinal) => value.StartsWith("0/", StringComparison.Ordinal) ? "Running" : "Pass",
            _ => "Pending"
        };

    private static string FormatNullableMilliseconds(TimeSpan? value)
        => value.HasValue ? $"{value.Value.TotalMilliseconds:F0} ms" : "--";

    private static string FormatSiteLatestResult(BatchSiteRunResult result)
        => $"延迟 {result.LatencyMs:F0} ms | TTFT {(result.TtftMs.HasValue ? $"{result.TtftMs.Value:F0} ms" : "--")} | 成功率 {result.SuccessRate:F1}%";

    private static string BuildThroughputBenchmarkDigest(ProxyThroughputBenchmarkResult benchmark)
        => $"独立吞吐 {FormatTokensPerSecond(benchmark.MedianOutputTokensPerSecond ?? benchmark.AverageOutputTokensPerSecond)} · 成功 {benchmark.SuccessfulSampleCount}/{benchmark.CompletedSampleCount}";

    private static string FormatTokensPerSecond(double? value)
        => value.HasValue ? $"{value.Value:F1} tok/s" : "--";

    private static int CountCompletedDeepSupplementalScenarios(IReadOnlyList<ProxyProbeScenarioResult> scenarios)
        => DeepSupplementalScenarioKinds.Count(kind => FindScenario(scenarios, kind) is not null);

    private static string BuildCapabilityMatrixSummary(ProxyDiagnosticsResult result)
        => BuildCapabilityMatrixSummary(result.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>());

    private static string BuildCapabilityMatrixSummary(IReadOnlyList<ProxyProbeScenarioResult> scenarios)
    {
        var baselinePassed = DeepBaselineCapabilityKinds.Count(kind => FindScenario(scenarios, kind)?.Success == true);
        List<string> parts = [$"B5 {baselinePassed}/{DeepBaselineCapabilityKinds.Length}"];

        foreach (var (kind, label) in DeepSupplementalScenarioBadges)
        {
            if (kind == ProxyProbeScenarioKind.CodeBlockDiscipline)
            {
                parts.Add("RM Off");
            }

            var scenario = FindScenario(scenarios, kind);
            parts.Add($"{label} {(scenario is null ? "--" : ResolveScenarioBadgeValue(scenario))}");
        }

        return string.Join(" | ", parts);
    }

    private static ProxyProbeScenarioResult? FindScenario(
        IReadOnlyList<ProxyProbeScenarioResult> scenarios,
        ProxyProbeScenarioKind kind)
        => scenarios.FirstOrDefault(scenario => scenario.Scenario == kind);

    private static string ResolveScenarioBadgeValue(ProxyProbeScenarioResult scenario)
    {
        if (scenario.Success)
        {
            return "OK";
        }

        if (scenario.FailureKind is ProxyFailureKind.ConfigurationInvalid)
        {
            return "CFG";
        }

        var status = scenario.CapabilityStatus ?? string.Empty;
        if (status.Contains("未执行", StringComparison.Ordinal))
        {
            return "SK";
        }

        if (status.Contains("待复核", StringComparison.Ordinal))
        {
            return "RV";
        }

        if (status.Contains("前置不足", StringComparison.Ordinal))
        {
            return "--";
        }

        if (status.Contains("参数", StringComparison.Ordinal) ||
            status.Contains("配置", StringComparison.Ordinal))
        {
            return "CFG";
        }

        return "NO";
    }

    private static string FormatDuration(TimeSpan elapsed)
        => elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

}
