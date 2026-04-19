using System.Text;
using NetTest.App.Services;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private sealed class BatchDeepChartRowState
    {
        public BatchDeepChartRowState(
            ProxyBatchRankingRowViewModel row,
            double? quickChatLatencyMs,
            double? quickTtftMs,
            string quickCapabilityText,
            int totalScenarioCount)
        {
            Row = row;
            QuickChatLatencyMs = quickChatLatencyMs;
            QuickTtftMs = quickTtftMs;
            QuickCapabilityText = quickCapabilityText;
            DeepTotalCount = totalScenarioCount;
        }

        public ProxyBatchRankingRowViewModel Row { get; }

        public int Rank => Row.Rank;

        public string EntryName => Row.EntryName;

        public string BaseUrl => Row.BaseUrl;

        public double? QuickChatLatencyMs { get; }

        public double? QuickTtftMs { get; }

        public string QuickCapabilityText { get; }

        public Dictionary<ProxyProbeScenarioKind, ProxyProbeScenarioResult> ScenarioResults { get; } = [];

        public int CompletedCount { get; set; }

        public int DeepTotalCount { get; set; }

        public bool HasStarted { get; set; }

        public bool IsRunning { get; set; }

        public bool IsCompleted { get; set; }

        public string StageText { get; set; } = "等待开始";

        public string IssueText { get; set; } = "排队中";

        public string Verdict { get; set; } = "未开始";

        public DateTimeOffset? LastUpdatedAt { get; set; }

        public ProxyDiagnosticsResult? FinalResult { get; set; }
    }

    private ProxySingleExecutionPlan? _currentBatchDeepExecutionPlan;

    private void ResetBatchDeepComparisonState()
    {
        _batchDeepChartStates.Clear();
        _currentBatchDeepExecutionPlan = null;
        SetProxyChartSnapshot(
            ProxyChartViewMode.BatchDeepComparison,
            new ProxyChartDialogSnapshot(
                "候选站点深度测试总览图",
                "先完成快速对比，再在排行榜列表里手动勾选候选项，然后启动批量候选深度测试。",
                "当前还没有候选站点深度测试摘要。",
                "暂无候选站点深测摘要。",
                "暂无候选站点深测明细。",
                "建议流程：快速对比 → 勾选排行榜列表项 → 候选站点深度测试。",
                "勾选排行榜列表项并开始深度测试后，这里会显示候选站点深度测试总览图。",
                "当前还没有候选站点深度测试总览图。",
                null),
            activate: false);
    }

    private void StartBatchDeepComparisonLiveSession(
        IReadOnlyList<ProxyBatchRankingRowViewModel> selectedRows,
        ProxySingleExecutionPlan executionPlan)
    {
        _currentBatchDeepExecutionPlan = executionPlan;
        _batchDeepChartStates.Clear();

        var aggregateRows = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns)).ToArray();
        var totalScenarioCount = CountPlannedBatchDeepScenarioCount(executionPlan);

        foreach (var row in selectedRows.OrderBy(item => item.Rank))
        {
            var aggregateRow = aggregateRows.FirstOrDefault(item =>
                string.Equals(item.Entry.Name, row.EntryName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Entry.BaseUrl, row.BaseUrl, StringComparison.OrdinalIgnoreCase));

            var quickCapabilityText = aggregateRow is null
                ? row.CapabilitySummary
                : $"综合 {FormatBatchDisplayedCapabilityAverage(aggregateRow)} | {BuildBatchCapabilityBreakdown(aggregateRow, includeDeepHint: false)}";

            _batchDeepChartStates.Add(new BatchDeepChartRowState(
                row,
                aggregateRow?.AverageChatLatencyMs,
                aggregateRow?.AverageTtftMs,
                quickCapabilityText,
                totalScenarioCount));

            row.DeepStatus = "排队中";
            row.DeepSummary = string.Empty;
            row.DeepCheckedAt = "--";
        }

        SetProxyChartRetryMode(ProxyChartRetryMode.None, ProxyChartRetryButtonText);
        BatchDeepTestSummary = BuildBatchDeepTestSummary(_batchDeepChartStates);
        RefreshBatchDeepComparisonDialog(activate: true);
    }

    private void PrepareBatchDeepRowForExecution(ProxyBatchRankingRowViewModel row, int index, int totalRows)
    {
        var state = FindBatchDeepChartRowState(row);
        if (state is null)
        {
            return;
        }

        state.HasStarted = true;
        state.IsRunning = true;
        state.IsCompleted = false;
        state.CompletedCount = 0;
        state.StageText = "基础 5 项启动中";
        state.IssueText = $"候选 {index + 1}/{totalRows}：等待 /models 返回。";
        state.Verdict = "进行中";
        state.LastUpdatedAt = DateTimeOffset.Now;

        row.DeepStatus = "进行中";
        row.DeepSummary = "基础 5 项启动中，随后会自动进入深度探针。";
        row.DeepCheckedAt = "--";

        BatchDeepTestSummary = BuildBatchDeepTestSummary(_batchDeepChartStates);
        RefreshBatchDeepComparisonDialog(activate: true);
    }

    private void UpdateBatchDeepChartLive(ProxyBatchRankingRowViewModel row, ProxyDiagnosticsLiveProgress progress)
    {
        var state = FindBatchDeepChartRowState(row);
        if (state is null)
        {
            return;
        }

        state.HasStarted = true;
        state.IsRunning = true;
        state.IsCompleted = false;
        state.CompletedCount = Math.Max(state.CompletedCount, progress.CompletedScenarioCount);
        state.DeepTotalCount = Math.Max(state.DeepTotalCount, progress.TotalScenarioCount);
        state.StageText = progress.CurrentScenarioResult.DisplayName;
        state.IssueText = BuildBatchDeepLiveDigest(progress.CurrentScenarioResult);
        state.Verdict = "进行中";
        state.LastUpdatedAt = progress.ReportedAt;

        foreach (var scenario in progress.ScenarioResults)
        {
            state.ScenarioResults[scenario.Scenario] = scenario;
        }

        row.DeepStatus = "进行中";
        row.DeepSummary = $"进度 {progress.CompletedScenarioCount}/{progress.TotalScenarioCount} | 最新 {progress.CurrentScenarioResult.DisplayName}：{state.IssueText}";
        row.DeepCheckedAt = progress.ReportedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

        BatchDeepTestSummary = BuildBatchDeepTestSummary(_batchDeepChartStates);
        RefreshBatchDeepComparisonDialog(activate: true);
    }

    private void ApplyBatchDeepChartResult(
        ProxyBatchRankingRowViewModel row,
        ProxyDiagnosticsResult result,
        ProxySingleExecutionPlan executionPlan)
    {
        var state = FindBatchDeepChartRowState(row);
        if (state is null)
        {
            return;
        }

        foreach (var scenario in GetScenarioResults(result))
        {
            state.ScenarioResults[scenario.Scenario] = scenario;
        }

        state.HasStarted = true;
        state.IsRunning = false;
        state.IsCompleted = true;
        state.CompletedCount = CountCompletedBatchDeepScenarioCount(result, executionPlan);
        state.DeepTotalCount = CountPlannedBatchDeepScenarioCount(executionPlan);
        state.StageText = "已完成";
        state.IssueText = BuildBatchDeepFinalDigest(result);
        state.Verdict = result.Verdict ?? (string.IsNullOrWhiteSpace(result.Error) ? "已完成" : "待复核");
        state.LastUpdatedAt = result.CheckedAt;
        state.FinalResult = result;

        row.DeepStatus = state.Verdict;
        row.DeepCheckedAt = result.CheckedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        row.DeepSummary = BuildBatchDeepRowSummary(result, executionPlan);

        BatchDeepTestSummary = BuildBatchDeepTestSummary(_batchDeepChartStates);
        RefreshBatchDeepComparisonDialog(activate: true);
    }

    private void ApplyBatchDeepChartFailure(ProxyBatchRankingRowViewModel row, Exception ex)
    {
        var state = FindBatchDeepChartRowState(row);
        if (state is null)
        {
            return;
        }

        var checkedAt = DateTimeOffset.Now;
        state.HasStarted = true;
        state.IsRunning = false;
        state.IsCompleted = true;
        state.StageText = "执行失败";
        state.IssueText = ex.Message;
        state.Verdict = "执行失败";
        state.LastUpdatedAt = checkedAt;

        row.DeepStatus = "执行失败";
        row.DeepCheckedAt = checkedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        row.DeepSummary = $"{row.DeepCheckedAt} | 深测失败：{ex.Message}";

        BatchDeepTestSummary = BuildBatchDeepTestSummary(_batchDeepChartStates);
        RefreshBatchDeepComparisonDialog(activate: true);
    }

    private void RefreshBatchDeepComparisonDialog(bool activate)
    {
        if (_batchDeepChartStates.Count == 0 || _currentBatchDeepExecutionPlan is null)
        {
            ResetBatchDeepComparisonState();
            return;
        }

        var orderedStates = _batchDeepChartStates
            .OrderBy(item => item.Rank)
            .ToArray();
        var chartItems = CreateBatchDeepComparisonChartItems(orderedStates, _currentBatchDeepExecutionPlan);
        var chartResult = _proxyBatchDeepComparisonChartRenderService.Render(
            chartItems,
            ResolvePreferredBatchDeepChartWidth());
        var completedCount = orderedStates.Count(item => item.IsCompleted);
        var running = orderedStates.FirstOrDefault(item => item.IsRunning);
        var top = orderedStates[0];
        var isLive = orderedStates.Any(item => item.IsRunning);

        SetProxyChartSnapshot(
            ProxyChartViewMode.BatchDeepComparison,
            new ProxyChartDialogSnapshot(
                isLive ? "候选站点深度测试实时总览" : "候选站点深度测试结果总览",
                "这张总览图把快速对比基线、当前阶段、实时进度和深度探针矩阵放进同一张高密度结果面板里，方便像 benchmark 结果页一样横向比较多个候选站点。",
                BuildBatchDeepDialogSummaryText(orderedStates, top, running),
                BuildBatchDeepCapabilitySummaryText(orderedStates, _currentBatchDeepExecutionPlan),
                BuildBatchDeepCapabilityDetailText(orderedStates, _currentBatchDeepExecutionPlan),
                "左侧保留快速对比基线；中部显示当前阶段与实时进度；右侧矩阵依次展示 B5、System Prompt、Function Calling、错误透传、流式完整性、官方对照、多模态、缓存命中、缓存隔离。",
                chartResult.HasChart
                    ? $"{chartResult.Summary} 已完成 {completedCount}/{orderedStates.Length}。"
                    : chartResult.Error ?? chartResult.Summary,
                "正在等待候选站点深度测试总览图...",
                chartResult.ChartImage),
            activate: activate);

        if (activate)
        {
            IsProxyTrendChartOpen = true;
        }
    }

    private ProxyBatchDeepComparisonChartItem[] CreateBatchDeepComparisonChartItems(
        IReadOnlyList<BatchDeepChartRowState> states,
        ProxySingleExecutionPlan executionPlan)
        => states
            .Select(state => new ProxyBatchDeepComparisonChartItem(
                state.Rank,
                state.EntryName,
                state.BaseUrl,
                state.QuickChatLatencyMs,
                state.QuickTtftMs,
                state.QuickCapabilityText,
                state.CompletedCount,
                state.DeepTotalCount,
                state.StageText,
                state.IssueText,
                ResolveBatchDeepDisplayStatus(state),
                state.LastUpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "--",
                state.IsRunning,
                state.IsCompleted,
                BuildBatchDeepComparisonBadges(state, executionPlan)))
            .ToArray();

    private IReadOnlyList<ProxyBatchDeepComparisonBadge> BuildBatchDeepComparisonBadges(
        BatchDeepChartRowState state,
        ProxySingleExecutionPlan executionPlan)
    {
        List<ProxyBatchDeepComparisonBadge> badges =
        [
            BuildBatchDeepBaselineBadge(state),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.SystemPromptMapping, "Sys", executionPlan.EnableProtocolCompatibilityTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.FunctionCalling, "Fn", executionPlan.EnableProtocolCompatibilityTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.ErrorTransparency, "Err", executionPlan.EnableErrorTransparencyTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.StreamingIntegrity, "Str", executionPlan.EnableStreamingIntegrityTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.OfficialReferenceIntegrity, "Ref", executionPlan.EnableOfficialReferenceIntegrityTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.MultiModal, "MM", executionPlan.EnableMultiModalTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.CacheMechanism, "Cch", executionPlan.EnableCacheMechanismTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.CacheIsolation, "Iso", executionPlan.EnableCacheIsolationTest)
        ];

        return badges;
    }

    private static ProxyBatchDeepComparisonBadge BuildBatchDeepBaselineBadge(BatchDeepChartRowState state)
    {
        var baselineKinds = GetBatchDeepBaselineScenarioKinds();
        var completedCount = baselineKinds.Count(kind => state.ScenarioResults.ContainsKey(kind));
        var passCount = baselineKinds.Count(kind => state.ScenarioResults.TryGetValue(kind, out var scenario) && scenario.Success);

        if (!state.HasStarted)
        {
            return new ProxyBatchDeepComparisonBadge("B5", "--", ProxyBatchDeepComparisonBadgeState.Pending);
        }

        if (completedCount < baselineKinds.Count)
        {
            return new ProxyBatchDeepComparisonBadge("B5", $"{passCount}/5", ProxyBatchDeepComparisonBadgeState.Running);
        }

        return new ProxyBatchDeepComparisonBadge(
            "B5",
            $"{passCount}/5",
            passCount == 5
                ? ProxyBatchDeepComparisonBadgeState.Pass
                : passCount >= 3
                    ? ProxyBatchDeepComparisonBadgeState.Warn
                    : ProxyBatchDeepComparisonBadgeState.Fail);
    }

    private static ProxyBatchDeepComparisonBadge BuildBatchDeepScenarioBadge(
        BatchDeepChartRowState state,
        ProxyProbeScenarioKind kind,
        string label,
        bool enabled)
    {
        if (!enabled)
        {
            return new ProxyBatchDeepComparisonBadge(label, "Off", ProxyBatchDeepComparisonBadgeState.Pending);
        }

        if (state.ScenarioResults.TryGetValue(kind, out var scenario))
        {
            return new ProxyBatchDeepComparisonBadge(
                label,
                ResolveBatchDeepScenarioBadgeValue(scenario),
                ResolveBatchDeepScenarioBadgeState(scenario));
        }

        if (state.IsCompleted)
        {
            return new ProxyBatchDeepComparisonBadge(label, "SK", ProxyBatchDeepComparisonBadgeState.Warn);
        }

        if (state.IsRunning && state.CompletedCount > 0)
        {
            return new ProxyBatchDeepComparisonBadge(label, "--", ProxyBatchDeepComparisonBadgeState.Running);
        }

        return new ProxyBatchDeepComparisonBadge(label, "--", ProxyBatchDeepComparisonBadgeState.Pending);
    }

    private ProxyBatchRankingRowViewModel? GetCurrentBatchDeepRunningRow()
        => _batchDeepChartStates
            .FirstOrDefault(item => item.IsRunning)
            ?.Row;

    private BatchDeepChartRowState? FindBatchDeepChartRowState(ProxyBatchRankingRowViewModel row)
        => _batchDeepChartStates.FirstOrDefault(item => ReferenceEquals(item.Row, row));

    private static int CountPlannedBatchDeepScenarioCount(ProxySingleExecutionPlan executionPlan)
        => GetBatchDeepBaselineScenarioKinds().Count +
           (executionPlan.EnableProtocolCompatibilityTest ? 2 : 0) +
           (executionPlan.EnableErrorTransparencyTest ? 1 : 0) +
           (executionPlan.EnableStreamingIntegrityTest ? 1 : 0) +
           (executionPlan.EnableOfficialReferenceIntegrityTest ? 1 : 0) +
           (executionPlan.EnableMultiModalTest ? 1 : 0) +
           (executionPlan.EnableCacheMechanismTest ? 1 : 0) +
           (executionPlan.EnableCacheIsolationTest ? 1 : 0);

    private static int CountCompletedBatchDeepScenarioCount(ProxyDiagnosticsResult result, ProxySingleExecutionPlan executionPlan)
        => GetBatchDeepBaselineScenarioKinds().Count(kind => FindScenario(GetScenarioResults(result), kind) is not null) +
           GetBatchDeepSupplementalScenarioKinds(executionPlan).Count(kind => FindScenario(GetScenarioResults(result), kind) is not null);

    private static IReadOnlyList<ProxyProbeScenarioKind> GetBatchDeepBaselineScenarioKinds()
        =>
        [
            ProxyProbeScenarioKind.Models,
            ProxyProbeScenarioKind.ChatCompletions,
            ProxyProbeScenarioKind.ChatCompletionsStream,
            ProxyProbeScenarioKind.Responses,
            ProxyProbeScenarioKind.StructuredOutput
        ];

    private static IReadOnlyList<ProxyProbeScenarioKind> GetBatchDeepSupplementalScenarioKinds(ProxySingleExecutionPlan executionPlan)
    {
        List<ProxyProbeScenarioKind> kinds = [];
        if (executionPlan.EnableProtocolCompatibilityTest)
        {
            kinds.Add(ProxyProbeScenarioKind.SystemPromptMapping);
            kinds.Add(ProxyProbeScenarioKind.FunctionCalling);
        }

        if (executionPlan.EnableErrorTransparencyTest)
        {
            kinds.Add(ProxyProbeScenarioKind.ErrorTransparency);
        }

        if (executionPlan.EnableStreamingIntegrityTest)
        {
            kinds.Add(ProxyProbeScenarioKind.StreamingIntegrity);
        }

        if (executionPlan.EnableOfficialReferenceIntegrityTest)
        {
            kinds.Add(ProxyProbeScenarioKind.OfficialReferenceIntegrity);
        }

        if (executionPlan.EnableMultiModalTest)
        {
            kinds.Add(ProxyProbeScenarioKind.MultiModal);
        }

        if (executionPlan.EnableCacheMechanismTest)
        {
            kinds.Add(ProxyProbeScenarioKind.CacheMechanism);
        }

        if (executionPlan.EnableCacheIsolationTest)
        {
            kinds.Add(ProxyProbeScenarioKind.CacheIsolation);
        }

        return kinds;
    }

    private string BuildBatchDeepTestSummary(IReadOnlyList<BatchDeepChartRowState> states)
    {
        if (states.Count == 0)
        {
            return "完成快速对比后，请在排行榜列表中勾选候选项，再发起深度测试。";
        }

        var ordered = states.OrderBy(item => item.Rank).ToArray();
        var completed = ordered.Count(item => item.IsCompleted);
        var running = ordered.FirstOrDefault(item => item.IsRunning);

        StringBuilder builder = new();
        builder.AppendLine($"候选站点深度测试：已完成 {completed}/{ordered.Length}（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）。");
        builder.AppendLine(running is null
            ? $"当前没有正在执行的候选项；排行榜保留 TOP 1：#{ordered[0].Rank} {ordered[0].EntryName}。"
            : $"当前执行：#{running.Rank} {running.EntryName}，进度 {running.CompletedCount}/{running.DeepTotalCount}，阶段 {running.StageText}。");
        builder.AppendLine();

        foreach (var state in ordered)
        {
            builder.AppendLine(
                $"#{state.Rank} {state.EntryName} | {ResolveBatchDeepDisplayStatus(state)} | 快速 普通 {FormatMillisecondsValue(state.QuickChatLatencyMs)} / TTFT {FormatMillisecondsValue(state.QuickTtftMs)} | {state.QuickCapabilityText}");
            builder.AppendLine($"阶段：{state.StageText}");
            builder.AppendLine($"摘要：{state.IssueText}");
            builder.AppendLine($"矩阵：{BuildBatchDeepBadgeSummaryText(state, _currentBatchDeepExecutionPlan)}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildBatchDeepDialogSummaryText(
        IReadOnlyList<BatchDeepChartRowState> states,
        BatchDeepChartRowState top,
        BatchDeepChartRowState? running)
    {
        var completed = states.Count(item => item.IsCompleted);
        return
            $"候选总数：{states.Count}\n" +
            $"已完成：{completed}/{states.Count}\n" +
            $"当前执行：{(running is null ? "无" : $"#{running.Rank} {running.EntryName} ({running.CompletedCount}/{running.DeepTotalCount})")}\n" +
            $"排行榜 TOP 1：#{top.Rank} {top.EntryName}\n" +
            $"快速基线：普通 {FormatMillisecondsValue(top.QuickChatLatencyMs)} / TTFT {FormatMillisecondsValue(top.QuickTtftMs)} / {top.QuickCapabilityText}";
    }

    private static string BuildBatchDeepCapabilitySummaryText(
        IReadOnlyList<BatchDeepChartRowState> states,
        ProxySingleExecutionPlan executionPlan)
    {
        StringBuilder builder = new();
        builder.AppendLine("候选深测摘要");

        foreach (var state in states)
        {
            builder.AppendLine($"#{state.Rank} {state.EntryName}");
            builder.AppendLine(
                $"状态：{ResolveBatchDeepDisplayStatus(state)}；快速基线 普通 {FormatMillisecondsValue(state.QuickChatLatencyMs)} / TTFT {FormatMillisecondsValue(state.QuickTtftMs)}；{state.QuickCapabilityText}");
            builder.AppendLine($"矩阵：{BuildBatchDeepBadgeSummaryText(state, executionPlan)}");
            if (state.Rank < states.Count)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildBatchDeepCapabilityDetailText(
        IReadOnlyList<BatchDeepChartRowState> states,
        ProxySingleExecutionPlan executionPlan)
    {
        StringBuilder builder = new();
        builder.AppendLine("候选深测明细");

        foreach (var state in states)
        {
            builder.AppendLine($"#{state.Rank} {state.EntryName}");
            builder.AppendLine($"地址：{state.BaseUrl}");
            builder.AppendLine($"快速基线：普通 {FormatMillisecondsValue(state.QuickChatLatencyMs)} / TTFT {FormatMillisecondsValue(state.QuickTtftMs)} / {state.QuickCapabilityText}");
            builder.AppendLine($"执行状态：{ResolveBatchDeepDisplayStatus(state)}");
            builder.AppendLine($"阶段：{state.StageText}");
            builder.AppendLine($"摘要：{state.IssueText}");
            builder.AppendLine($"矩阵：{BuildBatchDeepBadgeSummaryText(state, executionPlan)}");

            if (state.FinalResult is not null)
            {
                builder.AppendLine($"最终结论：{state.FinalResult.Verdict ?? "待复核"}");
                builder.AppendLine($"主要问题：{state.FinalResult.PrimaryIssue ?? "无明显主要问题"}");
                builder.AppendLine($"结果摘要：{state.FinalResult.Summary}");
            }

            if (state.Rank < states.Count)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string ResolveBatchDeepDisplayStatus(BatchDeepChartRowState state)
    {
        if (state.IsCompleted)
        {
            return state.Verdict;
        }

        return state.IsRunning
            ? $"进行中 {state.CompletedCount}/{state.DeepTotalCount}"
            : "待执行";
    }

    private static string BuildBatchDeepBadgeSummaryText(BatchDeepChartRowState state, ProxySingleExecutionPlan? executionPlan)
    {
        if (executionPlan is null)
        {
            return "等待执行计划。";
        }

        var badges = new[]
        {
            BuildBatchDeepBaselineBadge(state),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.SystemPromptMapping, "Sys", executionPlan.EnableProtocolCompatibilityTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.FunctionCalling, "Fn", executionPlan.EnableProtocolCompatibilityTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.ErrorTransparency, "Err", executionPlan.EnableErrorTransparencyTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.StreamingIntegrity, "Str", executionPlan.EnableStreamingIntegrityTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.OfficialReferenceIntegrity, "Ref", executionPlan.EnableOfficialReferenceIntegrityTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.MultiModal, "MM", executionPlan.EnableMultiModalTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.CacheMechanism, "Cch", executionPlan.EnableCacheMechanismTest),
            BuildBatchDeepScenarioBadge(state, ProxyProbeScenarioKind.CacheIsolation, "Iso", executionPlan.EnableCacheIsolationTest)
        };

        return string.Join(" | ", badges.Select(item => $"{item.Label} {item.Value}"));
    }

    private static ProxyBatchDeepComparisonBadgeState ResolveBatchDeepScenarioBadgeState(ProxyProbeScenarioResult scenario)
    {
        if (scenario.Success)
        {
            return ProxyBatchDeepComparisonBadgeState.Pass;
        }

        var status = scenario.CapabilityStatus ?? string.Empty;
        if (status.Contains("跳过", StringComparison.Ordinal) ||
            status.Contains("待复核", StringComparison.Ordinal) ||
            status.Contains("未运行", StringComparison.Ordinal))
        {
            return ProxyBatchDeepComparisonBadgeState.Warn;
        }

        return ProxyBatchDeepComparisonBadgeState.Fail;
    }

    private static string ResolveBatchDeepScenarioBadgeValue(ProxyProbeScenarioResult scenario)
    {
        if (scenario.Success)
        {
            return "OK";
        }

        var status = scenario.CapabilityStatus ?? string.Empty;
        if (status.Contains("跳过", StringComparison.Ordinal))
        {
            return "SK";
        }

        if (status.Contains("待复核", StringComparison.Ordinal))
        {
            return "RV";
        }

        if (status.Contains("未运行", StringComparison.Ordinal))
        {
            return "--";
        }

        return "NO";
    }

    private static string BuildBatchDeepLiveDigest(ProxyProbeScenarioResult scenario)
    {
        var preview = FirstNonEmpty(
            NormalizeInlineText(scenario.Preview),
            NormalizeInlineText(scenario.Summary),
            FormatScenarioStatus(scenario));
        return TrimInline(preview ?? "已收到结果。", 64);
    }

    private static string BuildBatchDeepFinalDigest(ProxyDiagnosticsResult result)
    {
        var primary = NormalizeInlineText(result.PrimaryIssue);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        var summary = NormalizeInlineText(result.Summary);
        return string.IsNullOrWhiteSpace(summary)
            ? "本轮深测已完成。"
            : TrimInline(summary, 64);
    }

    private static string BuildBatchDeepRowSummary(ProxyDiagnosticsResult result, ProxySingleExecutionPlan executionPlan)
    {
        var scenarios = GetScenarioResults(result);
        List<string> parts =
        [
            $"结论 {result.Verdict ?? "待复核"}",
            $"B5 {GetBatchDeepBaselineScenarioKinds().Count(kind => FindScenario(scenarios, kind)?.Success == true)}/5"
        ];

        void AddIfEnabled(bool enabled, ProxyProbeScenarioKind kind, string label)
        {
            if (!enabled)
            {
                return;
            }

            var scenario = FindScenario(scenarios, kind);
            parts.Add($"{label} {(scenario is null ? "SK" : ResolveBatchDeepScenarioBadgeValue(scenario))}");
        }

        AddIfEnabled(executionPlan.EnableProtocolCompatibilityTest, ProxyProbeScenarioKind.SystemPromptMapping, "Sys");
        AddIfEnabled(executionPlan.EnableProtocolCompatibilityTest, ProxyProbeScenarioKind.FunctionCalling, "Fn");
        AddIfEnabled(executionPlan.EnableErrorTransparencyTest, ProxyProbeScenarioKind.ErrorTransparency, "Err");
        AddIfEnabled(executionPlan.EnableStreamingIntegrityTest, ProxyProbeScenarioKind.StreamingIntegrity, "Str");
        AddIfEnabled(executionPlan.EnableOfficialReferenceIntegrityTest, ProxyProbeScenarioKind.OfficialReferenceIntegrity, "Ref");
        AddIfEnabled(executionPlan.EnableMultiModalTest, ProxyProbeScenarioKind.MultiModal, "MM");
        AddIfEnabled(executionPlan.EnableCacheMechanismTest, ProxyProbeScenarioKind.CacheMechanism, "Cch");
        AddIfEnabled(executionPlan.EnableCacheIsolationTest, ProxyProbeScenarioKind.CacheIsolation, "Iso");

        if (!string.IsNullOrWhiteSpace(result.PrimaryIssue))
        {
            parts.Add($"问题 {TrimInline(result.PrimaryIssue!, 32)}");
        }

        return string.Join(" | ", parts);
    }

    private static string TrimInline(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(1, maxLength - 1)] + "…";
    }

    private static string? NormalizeInlineText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? null
            : text.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
}
