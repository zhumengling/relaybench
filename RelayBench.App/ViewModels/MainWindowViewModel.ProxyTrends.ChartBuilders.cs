using System.Text;
using System.Windows.Media.Imaging;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static IReadOnlyList<ProxyTrendEntry> BuildLiveSeriesChartEntries(
        IReadOnlyList<ProxyDiagnosticsResult> rounds,
        string baseUrl,
        bool includePlaceholder)
    {
        if (rounds.Count == 0 && includePlaceholder)
        {
            return
            [
                new ProxyTrendEntry(
                    DateTimeOffset.Now,
                    baseUrl,
                    "等待第 1 轮",
                    "稳定性巡检（实时）",
                    string.Empty,
                    null,
                    false,
                    false,
                    false,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    "等待首轮结果。",
                    null)
            ];
        }

        return rounds
            .Select((round, index) => new ProxyTrendEntry(
                round.CheckedAt,
                round.BaseUrl,
                $"第 {index + 1} 轮",
                "稳定性巡检（实时）",
                round.RequestedModel,
                round.EffectiveModel,
                round.ModelsRequestSucceeded,
                round.ChatRequestSucceeded,
                round.StreamRequestSucceeded,
                IsFullSuccess(round) ? 100 : ComputeComponentSuccessRate(round),
                round.ChatRequestSucceeded ? 100 : 0,
                round.StreamRequestSucceeded ? 100 : 0,
                round.ChatLatency?.TotalMilliseconds,
                round.StreamFirstTokenLatency?.TotalMilliseconds,
                null,
                null,
                round.Summary,
                round.Error))
            .ToArray();
    }

    private static IReadOnlyList<ProxyTrendEntry> BuildSingleRetryTrendEntries(IReadOnlyList<ProxyDiagnosticsResult> runs)
    {
        if (runs.Count == 0)
        {
            return
            [
                new ProxyTrendEntry(
                    DateTimeOffset.Now,
                    string.Empty,
                    "等待第 1 次",
                    "单次诊断重试",
                    string.Empty,
                    null,
                    false,
                    false,
                    false,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    "等待首个结果。",
                    null)
            ];
        }

        return runs
            .Select((run, index) => new ProxyTrendEntry(
                run.CheckedAt,
                run.BaseUrl,
                $"第 {index + 1} 次",
                "单次诊断重试",
                run.RequestedModel,
                run.EffectiveModel,
                run.ModelsRequestSucceeded,
                run.ChatRequestSucceeded,
                run.StreamRequestSucceeded,
                IsFullSuccess(run) ? 100 : ComputeComponentSuccessRate(run),
                run.ChatRequestSucceeded ? 100 : 0,
                run.StreamRequestSucceeded ? 100 : 0,
                run.ChatLatency?.TotalMilliseconds,
                run.StreamFirstTokenLatency?.TotalMilliseconds,
                null,
                null,
                run.Summary,
                run.Error))
            .ToArray();
    }

    private static IReadOnlyList<ProxySingleCapabilityChartItem> BuildSingleCapabilityChartItems(
        IReadOnlyList<ProxyProbeScenarioResult> scenarios,
        int modelCount,
        IReadOnlyList<string> sampleModels,
        int completedCount,
        int totalCount)
    {
        var orderedKinds = GetOrderedScenarioDefinitions();
        var runningIndex = completedCount < totalCount ? completedCount : -1;
        List<ProxySingleCapabilityChartItem> items = new(orderedKinds.Length);

        for (var index = 0; index < orderedKinds.Length; index++)
        {
            var definition = orderedKinds[index];
            var scenario = FindScenario(scenarios, definition.Kind);
            var isRunning = scenario is null && index == runningIndex;

            items.Add(CreateSingleCapabilityChartItem(
                index + 1,
                definition.Label,
                definition.Kind,
                scenario,
                modelCount,
                sampleModels,
                isRunning));
        }

        return items;
    }

    private static IReadOnlyList<ProxySingleCapabilityChartItem> BuildFinalSingleCapabilityChartItems(ProxyDiagnosticsResult result)
    {
        var scenarios = GetScenarioResults(result);
        List<ProxySingleCapabilityChartItem> items = new();
        var order = 1;

        foreach (var definition in GetOrderedScenarioDefinitions())
        {
            var scenario = FindScenario(scenarios, definition.Kind);
            var detailText = definition.Kind switch
            {
                ProxyProbeScenarioKind.Models => $"模型数 {result.ModelCount} / 示例 {FormatSampleModels(result.SampleModels)}",
                ProxyProbeScenarioKind.ChatCompletionsStream => BuildScenarioChartDetail(scenario, "长连接首包与输出节奏"),
                _ => BuildScenarioChartDetail(scenario)
            };
            var previewText = definition.Kind switch
            {
                ProxyProbeScenarioKind.ChatCompletions => result.ChatPreview ?? scenario?.Preview ?? scenario?.Summary,
                ProxyProbeScenarioKind.ChatCompletionsStream => result.StreamPreview ?? scenario?.Preview ?? scenario?.Summary,
                _ => scenario?.Preview ?? scenario?.Summary
            };

            items.Add(CreateFinalScenarioChartItem(
                order++,
                "基础能力",
                "核心 API 通断与时延",
                definition.Label,
                scenario,
                detailText,
                previewText));
        }

        if (result.ThroughputBenchmarkResult is not null)
        {
            items.Add(CreateThroughputBenchmarkChartItem(order++, result.ThroughputBenchmarkResult));
        }

        if (result.LongStreamingResult is not null)
        {
            items.Add(CreateLongStreamingChartItem(order++, result.LongStreamingResult));
        }

        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.StreamingIntegrity,
            "增强测试",
            "长流保持与内容完整性",
            "流式完整性",
            previewOverride: BuildStreamingIntegrityDigest,
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "SSE 片段完整性"));

        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.Embeddings,
            "非聊天 API",
            "embeddings / images / audio / moderation",
            "Embeddings",
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "向量化能力"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.Images,
            "非聊天 API",
            "embeddings / images / audio / moderation",
            "Images",
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "生图能力"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.AudioTranscription,
            "非聊天 API",
            "embeddings / images / audio / moderation",
            "Audio Transcription",
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "音频转写能力"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.AudioSpeech,
            "非聊天 API",
            "embeddings / images / audio / moderation",
            "Audio Speech / TTS",
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "文本转语音能力"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.Moderation,
            "非聊天 API",
            "embeddings / images / audio / moderation",
            "Moderation",
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "内容审核能力"));

        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.SystemPromptMapping,
            "深度测试",
            "协议兼容、错误透传与缓存隔离",
            "System Prompt",
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "角色映射与指令注入"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.FunctionCalling,
            "深度测试",
            "协议兼容、错误透传与缓存隔离",
            "Function Calling",
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "工具调用协议对齐"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.ErrorTransparency,
            "深度测试",
            "协议兼容、错误透传与缓存隔离",
            "错误透传",
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "上游错误与状态码映射"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.OfficialReferenceIntegrity,
            "深度测试",
            "协议兼容、错误透传与缓存隔离",
            "官方对照完整性",
            previewOverride: BuildOfficialReferenceIntegrityDigest,
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "与官方输出对照"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.MultiModal,
            "深度测试",
            "协议兼容、错误透传与缓存隔离",
            "多模态",
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "图片 / 文件透传"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.CacheMechanism,
            "深度测试",
            "协议兼容、错误透传与缓存隔离",
            "缓存命中",
            previewOverride: BuildCacheMechanismDigest,
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "重复 Prompt 命中缓存"));
        AddScenarioChartItemIfPresent(
            items,
            ref order,
            scenarios,
            ProxyProbeScenarioKind.CacheIsolation,
            "深度测试",
            "协议兼容、错误透传与缓存隔离",
            "缓存隔离",
            previewOverride: BuildCacheIsolationDigest,
            detailOverride: scenario => BuildScenarioChartDetail(scenario, "跨账户隔离校验"));

        if (result.MultiModelSpeedResults is { Count: > 0 } multiModelResults)
        {
            foreach (var multiModelResult in multiModelResults
                         .OrderByDescending(item => item.Success)
                         .ThenByDescending(item => item.OutputTokensPerSecond ?? double.MinValue)
                         .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(CreateMultiModelSpeedChartItem(order++, multiModelResult));
            }
        }

        return items;
    }

    private static ProxySingleCapabilityChartItem CreateSingleCapabilityChartItem(
        int order,
        string label,
        ProxyProbeScenarioKind kind,
        ProxyProbeScenarioResult? scenario,
        int modelCount,
        IReadOnlyList<string> sampleModels,
        bool isRunning)
    {
        if (scenario is null)
        {
            var statusText = isRunning ? "进行中" : "等待中";
            var pendingDetailText = isRunning ? "正在探测..." : "尚未开始";
            return new ProxySingleCapabilityChartItem(
                order,
                "基础能力",
                "核心 API 通断与时延",
                label,
                statusText,
                false,
                false,
                null,
                null,
                isRunning ? "等待返回首个结果" : "--",
                false,
                pendingDetailText,
                isRunning ? "等待服务返回..." : "等待执行");
        }

        var detailText = kind switch
        {
            ProxyProbeScenarioKind.Models => $"模型数 {modelCount} / 示例 {FormatSampleModels(sampleModels)}",
            ProxyProbeScenarioKind.ChatCompletionsStream => BuildScenarioChartDetail(scenario, "长连接首包与输出节奏"),
            _ => BuildScenarioChartDetail(scenario)
        };

        return new ProxySingleCapabilityChartItem(
            order,
            "基础能力",
            "核心 API 通断与时延",
            label,
            scenario.CapabilityStatus,
            true,
            scenario.Success,
            scenario.StatusCode,
            ResolveScenarioMetricValueMs(scenario),
            ResolveScenarioMetricText(scenario),
            scenario.ReceivedDone,
            detailText,
            scenario.Preview ?? scenario.Summary);
    }

    private static ProxySingleCapabilityChartItem CreateFinalScenarioChartItem(
        int order,
        string sectionName,
        string sectionHint,
        string label,
        ProxyProbeScenarioResult? scenario,
        string detailText,
        string? previewText = null)
    {
        if (scenario is null)
        {
            return new ProxySingleCapabilityChartItem(
                order,
                sectionName,
                sectionHint,
                label,
                "未运行",
                false,
                false,
                null,
                null,
                "--",
                false,
                detailText,
                previewText ?? "本次未执行");
        }

        return new ProxySingleCapabilityChartItem(
            order,
            sectionName,
            sectionHint,
            label,
            scenario.CapabilityStatus,
            true,
            scenario.Success,
            scenario.StatusCode,
            ResolveScenarioMetricValueMs(scenario),
            ResolveScenarioMetricText(scenario),
            scenario.ReceivedDone,
            detailText,
            previewText ?? scenario.Preview ?? scenario.Summary);
    }

    private static ProxySingleCapabilityChartItem CreateFinalScenarioPlaceholderItem(
        int order,
        string sectionName,
        string sectionHint,
        string label,
        string statusText,
        string detailText,
        string previewText)
        => new(
            order,
            sectionName,
            sectionHint,
            label,
            statusText,
            false,
            false,
            null,
            null,
            "--",
            false,
            detailText,
            previewText);

    private static ProxySingleCapabilityChartItem CreateLongStreamingChartItem(int order, ProxyStreamingStabilityResult result)
    {
        var detailParts = new List<string>
        {
            $"段数 {result.ActualSegmentCount}/{result.ExpectedSegmentCount}",
            result.ReceivedDone ? "DONE 已收到" : "DONE 缺失"
        };
        var throughput = FormatTokensPerSecond(result.OutputTokensPerSecond, result.OutputTokenCountEstimated);
        if (!string.Equals(throughput, "--", StringComparison.Ordinal))
        {
            detailParts.Add($"速率 {throughput}");
        }

        return new ProxySingleCapabilityChartItem(
            order,
            "增强测试",
            "长流保持与内容完整性",
            "长流稳定",
            result.Success ? "通过" : "异常",
            true,
            result.Success,
            null,
            ResolveLongStreamingMetricValueMs(result),
            ResolveLongStreamingMetricText(result),
            result.ReceivedDone,
            string.Join(" / ", detailParts),
            result.Preview ?? result.Summary);
    }

    private static ProxySingleCapabilityChartItem CreateThroughputBenchmarkChartItem(int order, ProxyThroughputBenchmarkResult result)
    {
        if (result.IsLive)
        {
            List<string> liveDetailParts =
            [
                $"\u7B2C {Math.Max(result.CurrentSampleIndex, 1)}/{Math.Max(result.RequestedSampleCount, 1)} \u8F6E",
                $"\u5DF2\u5B8C\u6210 {result.CompletedSampleCount}/{Math.Max(result.RequestedSampleCount, 1)}"
            ];

            if (result.CurrentSampleElapsed is { } currentElapsed)
            {
                liveDetailParts.Add($"\u5F53\u524D\u7528\u65F6 {currentElapsed.TotalSeconds:F1}s");
            }

            if (result.CurrentOutputTokenCount is > 0)
            {
                liveDetailParts.Add($"\u5DF2\u8F93\u51FA {result.CurrentOutputTokenCount} token");
            }

            var liveMetricText = FormatTokensPerSecond(
                result.CurrentOutputTokensPerSecond ?? result.MedianOutputTokensPerSecond,
                result.CurrentOutputTokenCountEstimated || result.OutputTokenCountEstimated,
                Math.Max(result.CompletedSampleCount, 1));

            return new ProxySingleCapabilityChartItem(
                order,
                "\u589E\u5F3A\u6D4B\u8BD5",
                "\u957F\u6D41\u4FDD\u6301\u3001\u72EC\u7ACB\u541E\u5410\u4E0E\u5185\u5BB9\u5B8C\u6574\u6027",
                "\u72EC\u7ACB\u541E\u5410",
                "\u8FDB\u884C\u4E2D",
                false,
                false,
                null,
                null,
                liveMetricText,
                false,
                string.Join(" / ", liveDetailParts),
                result.Summary);
        }

        var detailParts = new List<string>
        {
            $"样本 {result.SuccessfulSampleCount}/{result.CompletedSampleCount}",
            $"区间 {FormatThroughputBenchmarkRange(result)}"
        };

        if (result.AverageOutputTokenCount is > 0)
        {
            detailParts.Add($"平均输出 {result.AverageOutputTokenCount} token");
        }

        return new ProxySingleCapabilityChartItem(
            order,
            "增强测试",
            "长流保持、独立吞吐与内容完整性",
            "独立吞吐",
            result.SuccessfulSampleCount > 0 ? "通过" : "异常",
            true,
            result.SuccessfulSampleCount > 0,
            null,
            null,
            FormatTokensPerSecond(
                result.MedianOutputTokensPerSecond,
                result.OutputTokenCountEstimated,
                result.CompletedSampleCount),
            false,
            string.Join(" / ", detailParts),
            result.Summary);
    }

    private static ProxySingleCapabilityChartItem CreateMultiModelSpeedChartItem(int order, ProxyMultiModelSpeedTestResult result)
    {
        List<string> detailParts =
        [
            "\u4E32\u884C\u5355\u6D41\u901F\u5EA6\u5BF9\u6BD4"
        ];

        if (result.StatusCode.HasValue)
        {
            detailParts.Add($"\u72B6\u6001\u7801 {result.StatusCode.Value}");
        }

        return new ProxySingleCapabilityChartItem(
            order,
            "\u591A\u6A21\u578B\u6D4B\u901F",
            "\u4E32\u884C\u5355\u6D41 tok/s \u5BF9\u6BD4",
            result.Model,
            result.Success ? "\u901A\u8FC7" : "\u5F02\u5E38",
            true,
            result.Success,
            result.StatusCode,
            null,
            FormatTokensPerSecond(result.OutputTokensPerSecond, result.OutputTokenCountEstimated),
            false,
            string.Join(" / ", detailParts),
            result.Preview ?? result.Error ?? result.Summary);
    }

    private static void AddScenarioChartItemIfPresent(
        ICollection<ProxySingleCapabilityChartItem> items,
        ref int order,
        IReadOnlyList<ProxyProbeScenarioResult> scenarios,
        ProxyProbeScenarioKind kind,
        string sectionName,
        string sectionHint,
        string label,
        Func<ProxyProbeScenarioResult?, string>? previewOverride = null,
        Func<ProxyProbeScenarioResult?, string>? detailOverride = null)
    {
        var scenario = FindScenario(scenarios, kind);
        if (scenario is null || IsCapabilityScenarioUnconfigured(scenario))
        {
            return;
        }

        items.Add(CreateFinalScenarioChartItem(
            order++,
            sectionName,
            sectionHint,
            label,
            scenario,
            detailOverride?.Invoke(scenario) ?? BuildScenarioChartDetail(scenario),
            previewOverride?.Invoke(scenario) ?? scenario.Preview ?? scenario.Summary));
    }

    private static string BuildScenarioChartDetail(ProxyProbeScenarioResult? scenario, string? primaryDetail = null)
    {
        List<string> parts = new();

        if (!string.IsNullOrWhiteSpace(primaryDetail))
        {
            parts.Add(primaryDetail!);
        }

        if (scenario?.OutputTokensPerSecond is not null)
        {
            parts.Add($"速率 {FormatTokensPerSecond(scenario.OutputTokensPerSecond, scenario.OutputTokenCountEstimated, scenario.OutputTokensPerSecondSampleCount)}");
        }

        var outputCount = FormatOutputCount(scenario);
        if (!string.Equals(outputCount, "--", StringComparison.Ordinal))
        {
            parts.Add($"输出 {outputCount}");
        }

        if (scenario?.ReceivedDone == true)
        {
            parts.Add("DONE 已收到");
        }

        if (parts.Count == 0)
        {
            return "查看右侧摘要说明";
        }

        return string.Join(" / ", parts.Take(3));
    }

    private static double? ResolveScenarioMetricValueMs(ProxyProbeScenarioResult? scenario)
        => scenario?.FirstTokenLatency?.TotalMilliseconds
           ?? scenario?.Latency?.TotalMilliseconds
           ?? scenario?.Duration?.TotalMilliseconds;

    private static string ResolveScenarioMetricText(ProxyProbeScenarioResult? scenario)
    {
        if (scenario?.FirstTokenLatency is not null)
        {
            return $"TTFT {FormatMillisecondsValue(scenario.FirstTokenLatency)}";
        }

        if (scenario?.Latency is not null)
        {
            return FormatMillisecondsValue(scenario.Latency);
        }

        if (scenario?.Duration is not null)
        {
            return FormatMillisecondsValue(scenario.Duration);
        }

        return "--";
    }

    private static double? ResolveLongStreamingMetricValueMs(ProxyStreamingStabilityResult? result)
        => result?.MaxChunkGapMilliseconds
           ?? result?.FirstTokenLatency?.TotalMilliseconds
           ?? result?.TotalDuration?.TotalMilliseconds;

    private static string ResolveLongStreamingMetricText(ProxyStreamingStabilityResult? result)
    {
        if (result?.MaxChunkGapMilliseconds is not null)
        {
            return $"Gap {result.MaxChunkGapMilliseconds:F0} ms";
        }

        if (result?.FirstTokenLatency is not null)
        {
            return $"TTFT {FormatMillisecondsValue(result.FirstTokenLatency)}";
        }

        if (result?.TotalDuration is not null)
        {
            return FormatMillisecondsValue(result.TotalDuration);
        }

        return "--";
    }

    private static string BuildLiveCapabilityMatrix(
        IReadOnlyList<ProxyProbeScenarioResult> scenarios,
        int completedCount,
        int totalCount)
    {
        var orderedKinds = GetOrderedScenarioDefinitions();
        var runningIndex = completedCount < totalCount ? completedCount : -1;

        return string.Join(
            "\n",
            orderedKinds.Select((definition, index) =>
            {
                var scenario = FindScenario(scenarios, definition.Kind);
                var status = scenario is null
                    ? index == runningIndex ? "进行中" : "等待中"
                    : FormatScenarioStatus(scenario);
                return $"{definition.Label}：{status}";
            }));
    }

    private static string BuildLiveCapabilityDetail(
        IReadOnlyList<ProxyProbeScenarioResult> scenarios,
        int completedCount,
        int totalCount,
        int modelCount,
        IReadOnlyList<string> sampleModels)
    {
        var orderedKinds = GetOrderedScenarioDefinitions();
        var runningIndex = completedCount < totalCount ? completedCount : -1;

        return string.Join(
            "\n",
            orderedKinds.Select((definition, index) =>
            {
                var scenario = FindScenario(scenarios, definition.Kind);
                if (scenario is null)
                {
                    return $"{definition.Label}：{BuildPendingScenarioDigest(index == runningIndex)}";
                }

                return definition.Kind switch
                {
                    ProxyProbeScenarioKind.Models => $"{definition.Label}：{BuildSingleScenarioDigest(scenario, $"模型数 {modelCount} / 示例 {FormatSampleModels(sampleModels)}", fallbackStatusCode: scenario.StatusCode, fallbackLatency: scenario.Latency)}",
                    ProxyProbeScenarioKind.ChatCompletionsStream => $"{definition.Label}：{BuildSingleScenarioDigest(scenario, BuildStreamPreviewLabel(scenario.Preview), fallbackStatusCode: scenario.StatusCode, fallbackLatency: scenario.Duration ?? scenario.Latency, fallbackFirstTokenLatency: scenario.FirstTokenLatency)}",
                    _ => $"{definition.Label}：{BuildSingleScenarioDigest(scenario, PreviewLabel(scenario.Preview), fallbackStatusCode: scenario.StatusCode, fallbackLatency: scenario.Latency)}"
                };
            }));
    }

    private static string BuildPendingScenarioDigest(bool isRunning)
        => isRunning
            ? "进行中；状态码 --；耗时 --；预览 等待返回"
            : "等待中；状态码 --；耗时 --；预览 尚未开始";

    private static (ProxyProbeScenarioKind Kind, string Label)[] GetOrderedScenarioDefinitions()
        =>
        [
            (ProxyProbeScenarioKind.Models, "/models"),
            (ProxyProbeScenarioKind.ChatCompletions, "普通对话"),
            (ProxyProbeScenarioKind.ChatCompletionsStream, "流式对话"),
            (ProxyProbeScenarioKind.Responses, "Responses"),
            (ProxyProbeScenarioKind.StructuredOutput, "结构化输出")
        ];

    private static double ResolveBatchRowCapabilityRatio(ProxyBatchProbeRow row)
        => Math.Round(
            Math.Clamp(
                (double)ResolveBatchPassedCapabilityCount(row.Result) /
                Math.Max(1, row.TotalBaselineScenarioCount),
                0d,
                1d) * 100d,
            1);

    private static int ResolveBatchPassedCapabilityCount(ProxyBatchProbeRow row)
        => ResolveBatchPassedCapabilityCount(row.Result);

    private static string BuildBatchCapabilityMatrix(ProxyBatchProbeRow row)
        => BuildBatchCapabilityMatrix(row.Result);

    private static IOrderedEnumerable<ProxyBatchProbeRow> OrderBatchRowsForComparison(IEnumerable<ProxyBatchProbeRow> rows)
        => rows
            .OrderByDescending(row => row.Stage == ProxyBatchProbeStage.Completed ? 1 : 0)
            .ThenByDescending(row => row.CompletedBaselineScenarioCount)
            .ThenBy(row => row.Result.ChatLatency ?? TimeSpan.MaxValue)
            .ThenBy(row => row.Result.StreamFirstTokenLatency ?? TimeSpan.MaxValue)
            .ThenBy(row => row.Result.PrimaryFailureKind is null ? 0 : 1)
            .ThenBy(row => row.Entry.Name, StringComparer.OrdinalIgnoreCase);

    private static string BuildBatchStabilityLabel(ProxyBatchProbeRow row)
    {
        if (row.Stage != ProxyBatchProbeStage.Completed)
        {
            return "进行中";
        }

        var passedCount = ResolveBatchPassedCapabilityCount(row);
        var chatLatency = row.Result.ChatLatency?.TotalMilliseconds;
        var ttft = row.Result.StreamFirstTokenLatency?.TotalMilliseconds;

        if (passedCount >= 4 &&
            (!chatLatency.HasValue || chatLatency.Value <= 1800) &&
            (!ttft.HasValue || ttft.Value <= 1800))
        {
            return "稳定";
        }

        if (passedCount >= 3)
        {
            return "可用";
        }

        return "待复核";
    }

}
