using System.Text;
using System.Windows.Media.Imaging;
using NetTest.App.Infrastructure;
using NetTest.App.Services;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void RecordSingleProxyTrend(ProxyDiagnosticsResult result)
    {
        _lastProxySingleResult = result;

        _proxyTrendStore.Append(new ProxyTrendEntry(
            result.CheckedAt,
            result.BaseUrl,
            TryBuildHostLabel(result.BaseUrl),
            "单次探测",
            result.RequestedModel,
            result.EffectiveModel,
            result.ModelsRequestSucceeded,
            result.ChatRequestSucceeded,
            result.StreamRequestSucceeded,
            IsFullSuccess(result) ? 100 : ComputeComponentSuccessRate(result),
            result.ChatRequestSucceeded ? 100 : 0,
            result.StreamRequestSucceeded ? 100 : 0,
            result.ChatLatency?.TotalMilliseconds,
            result.StreamFirstTokenLatency?.TotalMilliseconds,
            null,
            null,
            result.Summary,
            result.Error));

        RefreshProxyTrendView(result.BaseUrl);
        SyncProxyChartDialogWithTrendView();
        OpenProxyTrendChartIfAvailable();
    }

    private void RecordProxyStabilityTrend(ProxyStabilityResult result)
    {
        _lastProxyStabilityResult = result;

        _proxyTrendStore.Append(new ProxyTrendEntry(
            result.CheckedAt,
            result.BaseUrl,
            TryBuildHostLabel(result.BaseUrl),
            "稳定性序列",
            ProxyModel,
            ProxyModel,
            result.ModelsSuccessRate >= 99.9,
            result.ChatSuccessRate >= 99.9,
            result.StreamSuccessRate >= 99.9,
            result.FullSuccessRate,
            result.ChatSuccessRate,
            result.StreamSuccessRate,
            result.AverageChatLatency?.TotalMilliseconds,
            result.AverageStreamFirstTokenLatency?.TotalMilliseconds,
            result.HealthScore,
            null,
            result.Summary,
            null));

        RefreshProxyTrendView(result.BaseUrl);
        SyncProxyChartDialogWithTrendView();
        OpenProxyTrendChartIfAvailable();
    }

    private void RecordBatchProxyTrends(IReadOnlyList<ProxyBatchProbeRow> rows)
    {
        var entries = rows.Select(row => new ProxyTrendEntry(
            row.Result.CheckedAt,
            row.Result.BaseUrl,
            row.Entry.Name,
            "入口组对比",
            row.Result.RequestedModel,
            row.Result.EffectiveModel,
            row.Result.ModelsRequestSucceeded,
            row.Result.ChatRequestSucceeded,
            row.Result.StreamRequestSucceeded,
            IsFullSuccess(row.Result) ? 100 : ComputeComponentSuccessRate(row.Result),
            row.Result.ChatRequestSucceeded ? 100 : 0,
            row.Result.StreamRequestSucceeded ? 100 : 0,
            row.Result.ChatLatency?.TotalMilliseconds,
            row.Result.StreamFirstTokenLatency?.TotalMilliseconds,
            null,
            row.Score,
            row.Result.Summary,
            row.Result.Error));

        _proxyTrendStore.AppendRange(entries);
        RefreshProxyTrendView(ResolvePreferredTrendTarget(rows));
        RefreshProxyBatchComparisonDialog();
        OpenProxyTrendChartIfAvailable();
    }
    private void RefreshProxyTrendView(string? baseUrl = null)
    {
        var target = ResolveProxyTrendTarget(baseUrl);
        if (string.IsNullOrWhiteSpace(target))
        {
            _lastProxyTrendRecords = Array.Empty<ProxyTrendEntry>();
            ProxyTrendChartImage = null;
            IsProxyTrendChartOpen = false;
            ProxyTrendSummary = "填写中转站地址后，这里会显示同一中转站的历史趋势。";
            ProxyTrendDetail = "尚无中转站趋势记录。";
            ProxyTrendChartStatusSummary = "完成中转站诊断后，这里会显示稳定性图表。";
            SyncProxyChartDialogWithTrendView();
            RefreshProxyUnifiedOutput();
            return;
        }

        var recentRecords = _proxyTrendStore.GetRecentEntries(target, limit: 36)
            .OrderBy(record => record.Timestamp)
            .ToArray();
        _lastProxyTrendRecords = _proxyTrendStore.GetEntriesSince(target, DateTimeOffset.Now.AddHours(-24), limit: 240);

        if (recentRecords.Length == 0)
        {
            ProxyTrendChartImage = null;
            IsProxyTrendChartOpen = false;
            ProxyTrendSummary = $"当前中转站还没有历史趋势数据：{target}";
            ProxyTrendDetail = "尚无中转站趋势记录。";
            ProxyTrendChartStatusSummary = $"当前目标还没有可绘制的稳定性样本：{ProxyTrendStore.NormalizeBaseUrl(target)}";
            SyncProxyChartDialogWithTrendView();
            RefreshProxyUnifiedOutput();
            return;
        }

        var averageStability = Average(recentRecords.Select(record => (double?)ResolveSuccessRate(record)));
        var averageChatLatency = Average(recentRecords.Select(record => record.ChatLatencyMs));
        var averageTtft = Average(recentRecords.Select(record => record.StreamFirstTokenLatencyMs));
        var volatilityLabel = BuildVolatilityLabel(recentRecords);
        var comparison = AnalyzeProxyTrend(_lastProxyTrendRecords.Count == 0 ? recentRecords : _lastProxyTrendRecords);
        var chartResult = _proxyTrendChartRenderService.Render(recentRecords, target);

        ProxyTrendChartImage = chartResult.ChartImage;
        ProxyTrendChartStatusSummary = chartResult.HasChart
            ? chartResult.Summary
            : chartResult.Error ?? chartResult.Summary;

        ProxyTrendSummary =
            $"趋势目标：{ProxyTrendStore.NormalizeBaseUrl(target)}\n" +
            $"样本数：{recentRecords.Length}\n" +
            $"时间范围：{recentRecords.First().Timestamp:yyyy-MM-dd HH:mm:ss} ~ {recentRecords.Last().Timestamp:yyyy-MM-dd HH:mm:ss}\n" +
            $"平均稳定性：{FormatPercentValue(averageStability)}\n" +
            $"平均普通对话延迟：{FormatMillisecondsValue(averageChatLatency)}\n" +
            $"平均 TTFT：{FormatMillisecondsValue(averageTtft)}\n" +
            $"波动判断：{volatilityLabel}\n" +
            $"近 24 小时变化：{comparison.Summary}\n" +
            $"图表状态：{ProxyTrendChartStatusSummary}";

        StringBuilder builder = new();
        foreach (var record in recentRecords.OrderByDescending(record => record.Timestamp))
        {
            builder.AppendLine($"[{record.Timestamp:yyyy-MM-dd HH:mm:ss}] {record.Mode} / {record.Label}");
            builder.AppendLine($"地址：{record.BaseUrl}");
            builder.AppendLine($"模型：{record.EffectiveModel ?? record.RequestedModel ?? "--"}");
            builder.AppendLine($"稳定性：{FormatPercentValue(ResolveSuccessRate(record))}");
            builder.AppendLine($"模型列表：{(record.ModelsSuccess ? "成功" : "失败")} / 普通对话：{(record.ChatSuccess ? "成功" : "失败")} / 流式：{(record.StreamSuccess ? "成功" : "失败")}");
            builder.AppendLine($"普通对话延迟：{FormatMillisecondsValue(record.ChatLatencyMs)} / TTFT：{FormatMillisecondsValue(record.StreamFirstTokenLatencyMs)}");
            if (record.HealthScore.HasValue || record.BatchScore.HasValue)
            {
                builder.AppendLine($"补充指标：健康度 {record.HealthScore?.ToString() ?? "--"} / 批量排序 {record.BatchScore?.ToString() ?? "--"}");
            }

            builder.AppendLine($"摘要：{record.Summary}");
            builder.AppendLine($"错误：{record.Error ?? "无"}");
            builder.AppendLine();
        }

        ProxyTrendDetail = builder.ToString().TrimEnd();
        SyncProxyChartDialogWithTrendView();
        RefreshProxyUnifiedOutput();
    }

    private void SyncProxyChartDialogWithTrendView()
    {
        var trendTarget = ResolveProxyTrendTarget(null);
        ApplyProxyChartDialogCapabilitiesForTrendTarget(trendTarget);
        SetProxyChartSnapshot(
            ProxyChartViewMode.StabilityTrend,
            new ProxyChartDialogSnapshot(
                "中转站稳定性图表",
                "弹窗会显示同一个 URL 的历史趋势，适合看稳定性、普通延迟和 TTFT 是否长期波动。",
                ProxyTrendSummary,
                ProxyChartDialogCapabilitySummary,
                ProxyChartDialogCapabilityDetail,
                "蓝线：稳定性，越高越好。\n橙线：普通延迟，越低越好。\n绿线：TTFT，越低越好，适合看首字响应是否抖动。",
                ProxyTrendChartStatusSummary,
                "当前还没有可展示的趋势图表。",
                ProxyTrendChartImage),
            activate: false);
    }

    private void ApplyProxyChartDialogCapabilitiesForTrendTarget(string? trendTarget)
    {
        if (_lastProxySingleResult is not null &&
            IsMatchingChartDialogTarget(_lastProxySingleResult.BaseUrl, trendTarget))
        {
            ProxyChartDialogCapabilitySummary = BuildDialogCapabilityMatrix(_lastProxySingleResult);
            ProxyChartDialogCapabilityDetail = BuildDialogCapabilityDetail(_lastProxySingleResult);
            return;
        }

        if (_lastProxyStabilityResult is not null &&
            IsMatchingChartDialogTarget(_lastProxyStabilityResult.BaseUrl, trendTarget) &&
            _lastProxyStabilityResult.RoundResults.Count > 0)
        {
            var latestRound = _lastProxyStabilityResult.RoundResults
                .OrderByDescending(item => item.CheckedAt)
                .First();

            ProxyChartDialogCapabilitySummary =
                $"最近一轮能力矩阵（{latestRound.CheckedAt:yyyy-MM-dd HH:mm:ss}）\n" +
                BuildDialogCapabilityMatrix(latestRound) + "\n\n" +
                $"轮次通过：/models {_lastProxyStabilityResult.ModelsSuccessCount}/{_lastProxyStabilityResult.CompletedRounds}；" +
                $"普通对话 {_lastProxyStabilityResult.ChatSuccessCount}/{_lastProxyStabilityResult.CompletedRounds}；" +
                $"流式对话 {_lastProxyStabilityResult.StreamSuccessCount}/{_lastProxyStabilityResult.CompletedRounds}；" +
                $"Responses {_lastProxyStabilityResult.ResponsesSuccessCount}/{_lastProxyStabilityResult.CompletedRounds}；" +
                $"结构化输出 {_lastProxyStabilityResult.StructuredOutputSuccessCount}/{_lastProxyStabilityResult.CompletedRounds}";
            ProxyChartDialogCapabilityDetail =
                $"最近一轮能力明细\n{BuildDialogCapabilityDetail(latestRound)}\n\n" +
                $"平均耗时：普通对话 {FormatMilliseconds(_lastProxyStabilityResult.AverageChatLatency)}；" +
                $"TTFT {FormatMilliseconds(_lastProxyStabilityResult.AverageStreamFirstTokenLatency)}；" +
                $"Responses {FormatMilliseconds(_lastProxyStabilityResult.AverageResponsesLatency)}；" +
                $"结构化输出 {FormatMilliseconds(_lastProxyStabilityResult.AverageStructuredOutputLatency)}";
            return;
        }

        ProxyChartDialogCapabilitySummary = "当前图表还没有可关联的检测摘要。先运行一次基础或深度单次诊断，这里会按基础能力、增强测试、深度测试分区展示结果。";
        ProxyChartDialogCapabilityDetail = "暂无检测明细。";
    }

    private void RefreshProxyBatchComparisonDialog()
    {
        var aggregateRows = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns)).ToArray();
        if (aggregateRows.Length == 0)
        {
            SyncProxyChartDialogWithTrendView();
            return;
        }

        var chartItems = CreateProxyBatchComparisonChartItems(aggregateRows);
        var chartResult = _proxyBatchComparisonChartRenderService.Render(
            chartItems,
            ResolvePreferredBatchChartWidth());
        var best = aggregateRows[0];
        var runCount = _proxyBatchChartRuns.Count;

        SetProxyChartSnapshot(
            ProxyChartViewMode.BatchComparison,
            new ProxyChartDialogSnapshot(
                "中转站入口组累计对比图",
                "这里直接比较多个 URL 在多轮整组测试后的平均延迟、平均 TTFT 和综合能力。无论是单站点多入口，还是多站点多 Key，最终目标都是找出长期更稳的 URL。",
                $"当前已累计 {runCount} 轮入口组测试，共 {aggregateRows.Length} 个 URL。\n" +
                $"当前推荐：{best.Entry.Name}\n" +
                $"推荐地址：{best.Entry.BaseUrl}\n" +
                $"推荐原因：平均普通对话 {FormatMillisecondsValue(best.AverageChatLatencyMs)}，平均 TTFT {FormatMillisecondsValue(best.AverageTtftMs)}，综合能力 {FormatBatchDisplayedCapabilityAverage(best)}，基础/增强拆分见右侧摘要。\n\n" +
                BuildProxyBatchTopSummary(aggregateRows),
                BuildProxyBatchCapabilitySummaryText(aggregateRows, "多入口累计对比摘要"),
                BuildProxyBatchCapabilityDetailText(aggregateRows, "多入口累计对比明细"),
                "蓝条：平均普通对话延迟，越长代表越快。\n橙条：平均 TTFT，越长代表首字响应越快。\n绿条：入口组综合能力；第二行会补充基础/增强拆分，深度测试请看单次图表。",
                chartResult.HasChart ? chartResult.Summary : chartResult.Error ?? chartResult.Summary,
                "正在等待入口组累计图表生成。",
                chartResult.ChartImage),
            activate: true);
    }
    private static bool IsMatchingChartDialogTarget(string? sourceBaseUrl, string? trendTarget)
    {
        if (string.IsNullOrWhiteSpace(sourceBaseUrl) || string.IsNullOrWhiteSpace(trendTarget))
        {
            return false;
        }

        return string.Equals(
            ProxyTrendStore.NormalizeBaseUrl(sourceBaseUrl),
            ProxyTrendStore.NormalizeBaseUrl(trendTarget),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDialogCapabilityMatrix(ProxyDiagnosticsResult result)
    {
        var scenarios = GetScenarioResults(result);
        var models = FindScenario(scenarios, ProxyProbeScenarioKind.Models);
        var chat = FindScenario(scenarios, ProxyProbeScenarioKind.ChatCompletions);
        var stream = FindScenario(scenarios, ProxyProbeScenarioKind.ChatCompletionsStream);
        var responses = FindScenario(scenarios, ProxyProbeScenarioKind.Responses);
        var structuredOutput = FindScenario(scenarios, ProxyProbeScenarioKind.StructuredOutput);
        var streamingIntegrity = FindScenario(scenarios, ProxyProbeScenarioKind.StreamingIntegrity);
        var systemPrompt = FindScenario(scenarios, ProxyProbeScenarioKind.SystemPromptMapping);
        var functionCalling = FindScenario(scenarios, ProxyProbeScenarioKind.FunctionCalling);
        var errorTransparency = FindScenario(scenarios, ProxyProbeScenarioKind.ErrorTransparency);
        var officialReferenceIntegrity = FindScenario(scenarios, ProxyProbeScenarioKind.OfficialReferenceIntegrity);
        var multiModal = FindScenario(scenarios, ProxyProbeScenarioKind.MultiModal);
        var cacheMechanism = FindScenario(scenarios, ProxyProbeScenarioKind.CacheMechanism);
        var cacheIsolation = FindScenario(scenarios, ProxyProbeScenarioKind.CacheIsolation);

        StringBuilder builder = new();
        AppendDialogSection(
            builder,
            "基础能力",
            new[]
            {
                $"/models：{FormatScenarioStatus(models)}",
                $"普通对话：{FormatScenarioStatus(chat)}",
                $"流式对话：{FormatScenarioStatus(stream)}",
                $"Responses：{FormatScenarioStatus(responses)}",
                $"结构化输出：{FormatScenarioStatus(structuredOutput)}"
            });

        var enhancedLines = new List<string>();
        if (result.LongStreamingResult is not null)
        {
            enhancedLines.Add($"长流稳定：{BuildLongStreamingStatus(result.LongStreamingResult)}");
        }

        if (streamingIntegrity is not null)
        {
            enhancedLines.Add($"流式完整性：{FormatScenarioStatus(streamingIntegrity)}");
        }

        AppendDialogSection(builder, "增强测试", enhancedLines);

        AppendDialogSection(
            builder,
            "深度测试",
            BuildAvailableLines(new[]
            {
                BuildScenarioStatusLine("System Prompt", systemPrompt),
                BuildScenarioStatusLine("Function Calling", functionCalling),
                BuildScenarioStatusLine("错误透传", errorTransparency),
                BuildScenarioStatusLine("官方对照完整性", officialReferenceIntegrity),
                BuildScenarioStatusLine("多模态", multiModal),
                BuildScenarioStatusLine("缓存命中", cacheMechanism),
                BuildScenarioStatusLine("缓存隔离", cacheIsolation)
            }));

        return builder.ToString().TrimEnd();
    }

    private static string BuildDialogCapabilityDetail(ProxyDiagnosticsResult result)
    {
        var scenarios = GetScenarioResults(result);
        var models = FindScenario(scenarios, ProxyProbeScenarioKind.Models);
        var chat = FindScenario(scenarios, ProxyProbeScenarioKind.ChatCompletions);
        var stream = FindScenario(scenarios, ProxyProbeScenarioKind.ChatCompletionsStream);
        var responses = FindScenario(scenarios, ProxyProbeScenarioKind.Responses);
        var structuredOutput = FindScenario(scenarios, ProxyProbeScenarioKind.StructuredOutput);
        var streamingIntegrity = FindScenario(scenarios, ProxyProbeScenarioKind.StreamingIntegrity);
        var systemPrompt = FindScenario(scenarios, ProxyProbeScenarioKind.SystemPromptMapping);
        var functionCalling = FindScenario(scenarios, ProxyProbeScenarioKind.FunctionCalling);
        var errorTransparency = FindScenario(scenarios, ProxyProbeScenarioKind.ErrorTransparency);
        var officialReferenceIntegrity = FindScenario(scenarios, ProxyProbeScenarioKind.OfficialReferenceIntegrity);
        var multiModal = FindScenario(scenarios, ProxyProbeScenarioKind.MultiModal);
        var cacheMechanism = FindScenario(scenarios, ProxyProbeScenarioKind.CacheMechanism);
        var cacheIsolation = FindScenario(scenarios, ProxyProbeScenarioKind.CacheIsolation);

        StringBuilder builder = new();
        AppendDialogSection(
            builder,
            "基础能力",
            new[]
            {
                $"/models：{BuildSingleScenarioDigest(models, $"模型数 {result.ModelCount} / 示例 {FormatSampleModels(result.SampleModels)}", fallbackStatusCode: result.ModelsStatusCode, fallbackLatency: result.ModelsLatency)}",
                $"普通对话：{BuildSingleScenarioDigest(chat, PreviewLabel(result.ChatPreview), fallbackStatusCode: result.ChatStatusCode, fallbackLatency: result.ChatLatency)}",
                $"流式对话：{BuildSingleScenarioDigest(stream, BuildStreamPreviewLabel(result.StreamPreview), fallbackStatusCode: result.StreamStatusCode, fallbackLatency: result.StreamDuration, fallbackFirstTokenLatency: result.StreamFirstTokenLatency)}",
                $"Responses：{BuildSingleScenarioDigest(responses, PreviewLabel(responses?.Preview), fallbackStatusCode: responses?.StatusCode, fallbackLatency: responses?.Latency)}",
                $"结构化输出：{BuildSingleScenarioDigest(structuredOutput, PreviewLabel(structuredOutput?.Preview), fallbackStatusCode: structuredOutput?.StatusCode, fallbackLatency: structuredOutput?.Latency)}"
            });

        var enhancedLines = new List<string>();
        if (result.LongStreamingResult is not null)
        {
            enhancedLines.Add($"长流稳定：{BuildLongStreamingDetail(result.LongStreamingResult)}");
        }

        if (streamingIntegrity is not null)
        {
            enhancedLines.Add($"流式完整性：{BuildSingleScenarioDigest(streamingIntegrity, PreviewLabel(BuildStreamingIntegrityDigest(streamingIntegrity)), fallbackStatusCode: streamingIntegrity.StatusCode, fallbackLatency: streamingIntegrity.Latency, fallbackFirstTokenLatency: streamingIntegrity.FirstTokenLatency)}");
        }

        AppendDialogSection(builder, "增强测试", enhancedLines);

        AppendDialogSection(
            builder,
            "深度测试",
            BuildAvailableLines(new[]
            {
                BuildScenarioDetailLine("System Prompt", systemPrompt),
                BuildScenarioDetailLine("Function Calling", functionCalling),
                BuildScenarioDetailLine("错误透传", errorTransparency),
                BuildScenarioDetailLine("官方对照完整性", officialReferenceIntegrity, BuildOfficialReferenceIntegrityDigest(officialReferenceIntegrity)),
                BuildScenarioDetailLine("多模态", multiModal),
                BuildScenarioDetailLine("缓存命中", cacheMechanism, BuildCacheMechanismDigest(cacheMechanism)),
                BuildScenarioDetailLine("缓存隔离", cacheIsolation, BuildCacheIsolationDigest(cacheIsolation))
            }));

        return builder.ToString().TrimEnd();
    }

    private static void AppendDialogSection(StringBuilder builder, string title, IEnumerable<string> lines)
    {
        var filtered = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (filtered.Length == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine($"【{title}】");
        foreach (var line in filtered)
        {
            builder.AppendLine(line);
        }
    }

    private static IEnumerable<string> BuildAvailableLines(IEnumerable<string?> lines)
        => lines.Where(line => !string.IsNullOrWhiteSpace(line)).OfType<string>();

    private static string? BuildScenarioStatusLine(string label, ProxyProbeScenarioResult? scenario)
        => scenario is null ? null : $"{label}：{FormatScenarioStatus(scenario)}";

    private static string? BuildScenarioDetailLine(
        string label,
        ProxyProbeScenarioResult? scenario,
        string? previewText = null)
        => scenario is null
            ? null
            : $"{label}：{BuildSingleScenarioDigest(scenario, PreviewLabel(previewText ?? scenario.Preview), fallbackStatusCode: scenario.StatusCode, fallbackLatency: scenario.Latency, fallbackFirstTokenLatency: scenario.FirstTokenLatency)}";

    private static string BuildLongStreamingStatus(ProxyStreamingStabilityResult result)
        => $"{(result.Success ? "通过" : "异常")} / {result.ActualSegmentCount}/{result.ExpectedSegmentCount} / DONE {(result.ReceivedDone ? "已收到" : "缺失")}";

    private static string BuildLongStreamingDetail(ProxyStreamingStabilityResult result)
    {
        var gapPart = result.MaxChunkGapMilliseconds is not null
            ? $"最大间隔 {result.MaxChunkGapMilliseconds:F0} ms"
            : $"总耗时 {FormatMilliseconds(result.TotalDuration)}";
        return $"{BuildLongStreamingStatus(result)} / TTFT {FormatMilliseconds(result.FirstTokenLatency)} / {gapPart} / 速率 {FormatTokensPerSecond(result.OutputTokensPerSecond, result.OutputTokenCountEstimated)}";
    }

}
