using System.Diagnostics;
using System.Text;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task RunProxyBatchCoreAsync(CancellationToken cancellationToken)
    {
        ExecuteWithoutProxyBatchSiteGroupSelectionHandling(() => SelectedProxyBatchSiteGroup = null);
        SelectedProxyBatchEditorItem = null;
        IsProxyBatchEditorOpen = false;

        var plan = BuildProxyBatchPlan(requireRunnable: true);
        PrepareForProxyBatchQuickCompare();
        _lastProxyBatchPlan = plan;
        _proxyBatchChartRuns.Clear();
        _lastProxyBatchRows = Array.Empty<ProxyBatchProbeRow>();

        var timeoutSeconds = ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 120);
        var enableLongStreamingTest = ProxyBatchEnableLongStreamingTest;
        var longStreamSegmentCount = GetProxyLongStreamSegmentCount();
        Dictionary<string, ProxyBatchProbeRow> liveRows = new(StringComparer.OrdinalIgnoreCase);
        StartProxyBatchChartLiveSession(plan.Targets);
        UpdateGlobalTaskProgress("\u51C6\u5907\u4E2D", 8d);
        var liveChartUpdateInterval = TimeSpan.FromMilliseconds(220);
        var liveChartUpdateStopwatch = Stopwatch.StartNew();
        var lastLiveChartUpdateAt = TimeSpan.Zero;
        var liveChartUpdateScheduled = false;
        IReadOnlyList<ProxyBatchProbeRow>? pendingLiveChartRows = null;

        void ApplyLiveChartRows(IReadOnlyList<ProxyBatchProbeRow> orderedRows)
        {
            UpdateProxyBatchChartLive(orderedRows, plan.Targets.Count);
            UpdateGlobalTaskProgress(CountCompletedLiveBatchRows(orderedRows), plan.Targets.Count, "\u8BF7\u6C42\u4E2D");
            lastLiveChartUpdateAt = liveChartUpdateStopwatch.Elapsed;
        }

        async void SchedulePendingLiveChartUpdate()
        {
            if (liveChartUpdateScheduled)
            {
                return;
            }

            liveChartUpdateScheduled = true;
            var elapsedSinceLastUpdate = liveChartUpdateStopwatch.Elapsed - lastLiveChartUpdateAt;
            var delay = liveChartUpdateInterval - elapsedSinceLastUpdate;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    liveChartUpdateScheduled = false;
                    return;
                }
            }

            liveChartUpdateScheduled = false;
            if (pendingLiveChartRows is not { } rowsToApply)
            {
                return;
            }

            pendingLiveChartRows = null;
            ApplyLiveChartRows(rowsToApply);
        }

        var progress = new Progress<string>(message =>
        {
            StatusMessage = message;
            ProxyChartDialogStatusSummary = message;
        });
        var rowProgress = new Progress<ProxyBatchProbeRow>(row =>
        {
            liveRows[BuildBatchTargetKey(row.Entry)] = row;
            var orderedRows = MaterializeLiveBatchRows(liveRows, plan.Targets);
            pendingLiveChartRows = orderedRows;
            var shouldUpdateImmediately = row.Stage == ProxyBatchProbeStage.Completed ||
                liveChartUpdateStopwatch.Elapsed - lastLiveChartUpdateAt >= liveChartUpdateInterval;
            if (shouldUpdateImmediately)
            {
                pendingLiveChartRows = null;
                ApplyLiveChartRows(orderedRows);
                return;
            }

            SchedulePendingLiveChartUpdate();
        });
        var rows = await ProbeBatchEntriesAsync(
            plan.Targets,
            timeoutSeconds,
            enableLongStreamingTest,
            longStreamSegmentCount,
            progress,
            rowProgress,
            cancellationToken);

        _proxyBatchChartRuns.Add(rows.ToArray());
        ApplyProxyBatchResults(_proxyBatchChartRuns, plan);
        RecordBatchProxyTrends(rows);

        var aggregateRows = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns)).ToArray();
        var bestRow = aggregateRows[0];

        DashboardCards[3].Status = BuildProxyBatchCardStatus(aggregateRows);
        DashboardCards[3].Detail =
            $"入口组累计 {_proxyBatchChartRuns.Count} 轮，推荐 {bestRow.Entry.Name}（平均普通延迟 {FormatMillisecondsValue(bestRow.AverageChatLatencyMs)} / 平均 TTFT {FormatMillisecondsValue(bestRow.AverageTtftMs)} / 独立吞吐 {FormatTokensPerSecond(bestRow.AverageBenchmarkTokensPerSecond)} / 综合分 {bestRow.CompositeScore:F1}）。";
        StatusMessage = $"入口组检测完成，当前累计 {_proxyBatchChartRuns.Count} 轮整组，推荐 {bestRow.Entry.Name}。";
        AppendHistory("接口", "接口入口组对比", ProxyBatchSummary);
    }

    private void ApplyProxyBatchResults(IReadOnlyList<IReadOnlyList<ProxyBatchProbeRow>> batchRuns, ProxyBatchPlan plan)
    {
        _currentProxyBatchLiveRows = Array.Empty<ProxyBatchProbeRow>();
        var latestRows = batchRuns.LastOrDefault() ?? Array.Empty<ProxyBatchProbeRow>();
        _lastProxyBatchRows = latestRows.ToArray();

        var aggregateRows = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(batchRuns)).ToArray();
        if (aggregateRows.Length == 0)
        {
            ProxyBatchSummary = "入口组检测尚未采集到有效结果。";
            ProxyBatchDetail = "暂无入口组结果。";
            RefreshProxyUnifiedOutput();
            return;
        }

        var allRows = batchRuns.SelectMany(run => run).ToArray();
        var modelsSuccessCount = allRows.Count(row => row.Result.ModelsRequestSucceeded);
        var chatSuccessCount = allRows.Count(row => row.Result.ChatRequestSucceeded);
        var streamSuccessCount = allRows.Count(row => row.Result.StreamRequestSucceeded);
        var responsesSuccessCount = allRows.Count(row => FindScenario(GetScenarioResults(row.Result), ProxyProbeScenarioKind.Responses)?.Success == true);
        var structuredOutputSuccessCount = allRows.Count(row => FindScenario(GetScenarioResults(row.Result), ProxyProbeScenarioKind.StructuredOutput)?.Success == true);
        var longStreamingSuccessCount = allRows.Count(row => row.Result.LongStreamingResult?.Success == true);
        var bestRow = aggregateRows[0];
        var recommendationLines = string.Join(
            "；",
            aggregateRows
                .Take(3)
                .Select((row, index) =>
                    $"TOP {index + 1}：{row.Entry.Name}（平均普通 {FormatMillisecondsValue(row.AverageChatLatencyMs)} / 平均 TTFT {FormatMillisecondsValue(row.AverageTtftMs)} / 独立吞吐 {FormatTokensPerSecond(row.AverageBenchmarkTokensPerSecond)} / 综合分 {row.CompositeScore:F1} / {BuildBatchCapabilityBreakdown(row, includeDeepHint: false)}）"));

        var standaloneCount = plan.SourceEntries.Count(entry => string.IsNullOrWhiteSpace(entry.SiteGroupName));
        var groupedCount = plan.SourceEntries.Count - standaloneCount;
        var siteGroupCount = plan.SourceEntries
            .Select(entry => entry.SiteGroupName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var inlineKeyCount = plan.Targets.Count(entry => entry.KeySource == ProxyBatchKeySource.Entry);
        var siteGroupKeyCount = plan.Targets.Count(entry => entry.KeySource == ProxyBatchKeySource.SiteGroup);

        ProxyBatchSummary =
            $"检测时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
            $"URL 数：{aggregateRows.Length}\n" +
            $"累计整组轮次：{batchRuns.Count}\n" +
            $"独立条目：{standaloneCount}\n" +
            $"站点组子入口：{groupedCount}\n" +
            $"站点组数量：{siteGroupCount}\n" +
            $"密钥来源：条目内 {inlineKeyCount} 项 / 站点组继承 {siteGroupKeyCount} 项\n" +
            $"累计采样：{allRows.Length} 条\n" +
            $"/models 通过：{modelsSuccessCount}/{allRows.Length}\n" +
            $"普通对话通过：{chatSuccessCount}/{allRows.Length}\n" +
            $"流式对话通过：{streamSuccessCount}/{allRows.Length}\n" +
            $"Responses 通过：{responsesSuccessCount}/{allRows.Length}\n" +
            $"结构化输出通过：{structuredOutputSuccessCount}/{allRows.Length}\n" +
            (ProxyBatchEnableLongStreamingTest ? $"长流简测通过：{longStreamingSuccessCount}/{allRows.Length}\n" : string.Empty) +
            $"当前推荐：{bestRow.Entry.Name}\n" +
            $"推荐地址：{bestRow.Entry.BaseUrl}\n" +
            $"推荐密钥：{bestRow.Entry.ApiKeyAlias} / {MaskApiKey(bestRow.Entry.ApiKey)}\n" +
            $"推荐模型：{bestRow.Entry.Model}\n" +
            $"推荐理由：平均普通对话 {FormatMillisecondsValue(bestRow.AverageChatLatencyMs)}，平均 TTFT {FormatMillisecondsValue(bestRow.AverageTtftMs)}，平均独立吞吐 {FormatTokensPerSecond(bestRow.AverageBenchmarkTokensPerSecond)}，综合分 {bestRow.CompositeScore:F1}，{BuildBatchCapabilityBreakdown(bestRow, includeDeepHint: true)}，最新结论 {bestRow.LatestResult.Verdict ?? "待复核"}\n" +
            $"最近一轮五项：{BuildBatchCapabilityMatrix(bestRow.LatestResult)}\n" +
            $"最近一轮独立吞吐：{BuildThroughputBenchmarkDigest(bestRow.LatestResult.ThroughputBenchmarkResult)}\n" +
            (bestRow.LatestResult.LongStreamingResult is { } bestLongStream
                ? $"最近一轮长流：{(bestLongStream.Success ? "通过" : "异常")} / {bestLongStream.ActualSegmentCount}/{bestLongStream.ExpectedSegmentCount} / {FormatTokensPerSecond(bestLongStream.OutputTokensPerSecond, bestLongStream.OutputTokenCountEstimated)}\n"
                : string.Empty) +
            $"可追溯性：{bestRow.LatestResult.TraceabilitySummary ?? "未识别"}\n" +
            $"排行榜：{recommendationLines}\n" +
            $"CDN 观察：{bestRow.LatestResult.CdnSummary ?? "未识别明显边缘特征"}\n" +
            "说明：入口组图表会累计多轮结果，最终目标是找出哪个 URL 长期更稳、更快。";

        StringBuilder builder = new();
        foreach (var row in aggregateRows.Select((value, index) => new { value, index }))
        {
            builder.AppendLine($"#{row.index + 1} {row.value.Entry.Name}");
            if (!string.IsNullOrWhiteSpace(row.value.Entry.SiteGroupName))
            {
                builder.AppendLine($"站点组：{row.value.Entry.SiteGroupName}");
            }

            builder.AppendLine($"地址：{row.value.Entry.BaseUrl}");
            builder.AppendLine($"密钥：{row.value.Entry.ApiKeyAlias} / {MaskApiKey(row.value.Entry.ApiKey)}");
            builder.AppendLine($"请求模型：{row.value.Entry.Model}");
            builder.AppendLine($"累计轮次：{row.value.RunCount}");
            builder.AppendLine($"稳定性：{BuildBatchStabilityLabel(row.value)}");
            builder.AppendLine($"综合分：{row.value.CompositeScore:F1}");
            builder.AppendLine($"能力均值：{FormatBatchDisplayedCapabilityAverage(row.value)}");
            builder.AppendLine($"基础均值：{FormatCapabilityAverage(row.value.AveragePassedCapabilityCount)}/5");
            builder.AppendLine($"满 5 项轮次：{row.value.FullPassRounds}/{row.value.RunCount}");
            builder.AppendLine($"平均普通对话：{FormatMillisecondsValue(row.value.AverageChatLatencyMs)}");
            builder.AppendLine($"平均 TTFT：{FormatMillisecondsValue(row.value.AverageTtftMs)}");
            builder.AppendLine($"平均独立吞吐：{FormatTokensPerSecond(row.value.AverageBenchmarkTokensPerSecond)}");
            if (ProxyBatchEnableLongStreamingTest)
            {
                builder.AppendLine(row.value.LongStreamingExecutedRounds > 0
                    ? $"增强长流：{row.value.LongStreamingPassRounds}/{row.value.LongStreamingExecutedRounds} 轮通过"
                    : "增强长流：未执行");
            }
            builder.AppendLine("深度测试：入口组模式不聚合，需查看单次诊断图表。");
            builder.AppendLine($"最近一轮时间：{row.value.LatestResult.CheckedAt:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"最近一轮五项：{BuildBatchCapabilityMatrix(row.value.LatestResult)}");
            builder.AppendLine($"最近一轮普通对话：{(row.value.LatestResult.ChatRequestSucceeded ? "成功" : "失败")} / 延迟 {FormatMilliseconds(row.value.LatestResult.ChatLatency)}");
            builder.AppendLine($"最近一轮流式对话：{(row.value.LatestResult.StreamRequestSucceeded ? "成功" : "失败")} / 首 Token {FormatMilliseconds(row.value.LatestResult.StreamFirstTokenLatency)}");
            var latestStreamScenario = FindScenario(GetScenarioResults(row.value.LatestResult), ProxyProbeScenarioKind.ChatCompletionsStream);
            builder.AppendLine($"最近一轮输出速率：{FormatTokensPerSecond(latestStreamScenario?.OutputTokensPerSecond, latestStreamScenario?.OutputTokenCountEstimated == true, latestStreamScenario?.OutputTokensPerSecondSampleCount ?? 1)} / 输出 {latestStreamScenario?.OutputTokenCount?.ToString() ?? "--"} token");
            builder.AppendLine($"最近一轮独立吞吐：{BuildThroughputBenchmarkDigest(row.value.LatestResult.ThroughputBenchmarkResult)}");
            builder.AppendLine($"最近一轮 Responses：{FormatScenarioStatus(FindScenario(GetScenarioResults(row.value.LatestResult), ProxyProbeScenarioKind.Responses))}");
            builder.AppendLine($"最近一轮结构化输出：{FormatScenarioStatus(FindScenario(GetScenarioResults(row.value.LatestResult), ProxyProbeScenarioKind.StructuredOutput))}");
            if (row.value.LatestResult.LongStreamingResult is { } longStreamingResult)
            {
                builder.AppendLine($"最近一轮长流：{(longStreamingResult.Success ? "通过" : "异常")} / 段数 {longStreamingResult.ActualSegmentCount}/{longStreamingResult.ExpectedSegmentCount} / DONE {(longStreamingResult.ReceivedDone ? "已收到" : "缺失")} / 速率 {FormatTokensPerSecond(longStreamingResult.OutputTokensPerSecond, longStreamingResult.OutputTokenCountEstimated)}");
            }
            builder.AppendLine($"最近一轮判定：{row.value.LatestResult.Verdict ?? "待复核"}");
            builder.AppendLine($"可追溯性：{row.value.LatestResult.TraceabilitySummary ?? "未识别"}");
            builder.AppendLine($"Request-ID：{row.value.LatestResult.RequestId ?? "--"}");
            builder.AppendLine($"Trace-ID：{row.value.LatestResult.TraceId ?? "--"}");
            builder.AppendLine($"CDN / 边缘：{row.value.LatestResult.CdnSummary ?? "未识别"}");
            builder.AppendLine($"摘要：{row.value.LatestResult.Summary}");
            builder.AppendLine($"错误：{row.value.LatestResult.Error ?? "无"}");
            builder.AppendLine();
        }

        ProxyBatchDetail = builder.ToString().TrimEnd();
        RefreshProxyBatchRecommendation(aggregateRows);
        RefreshProxyBatchRankingRows(aggregateRows);
        ProxyBatchQuickCompareCompleted = true;
        if (_lastProxySingleResult is not null)
        {
            RefreshProxyManagedEntryAssessment(_lastProxySingleResult);
        }

        RefreshProxyOverviewSummary();
        RefreshProxyUnifiedOutput();
        AppendModuleOutput("接口入口组返回", ProxyBatchSummary, ProxyBatchDetail);
    }

    private static IReadOnlyList<ProxyBatchProbeRow> MaterializeLiveBatchRows(
        IReadOnlyDictionary<string, ProxyBatchProbeRow> liveRows,
        IReadOnlyList<ProxyBatchTargetEntry> targets)
    {
        List<ProxyBatchProbeRow> orderedRows = [];
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            if (liveRows.TryGetValue(BuildBatchTargetKey(target), out var row))
            {
                orderedRows.Add(row);
                continue;
            }

            orderedRows.Add(CreatePendingProxyBatchProbeRow(target, ResolvePendingProxyBatchMessage(targets, index)));
        }

        return orderedRows;
    }

    private static int CountCompletedLiveBatchRows(IEnumerable<ProxyBatchProbeRow> rows)
        => rows.Count(row => row.Stage == ProxyBatchProbeStage.Completed);

    private static string ResolvePendingProxyBatchMessage(
        IReadOnlyList<ProxyBatchTargetEntry> targets,
        int targetIndex)
    {
        var target = targets[targetIndex];
        if (!string.IsNullOrWhiteSpace(target.SiteGroupName))
        {
            for (var index = 0; index < targetIndex; index++)
            {
                if (string.Equals(targets[index].SiteGroupName, target.SiteGroupName, StringComparison.OrdinalIgnoreCase))
                {
                    return "等待同组其他入口测试中";
                }
            }
        }

        return "等待开始";
    }
}

