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
        ResetProxyTrendChartAutoOpenSuppression();
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
                chartResult.ChartImage,
                chartResult.HitRegions),
            activate: activate);

        if (activate)
        {
            AutoOpenProxyTrendChartIfAllowed();
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
        var (title, description) = GetBatchDeepBadgeDefinition("B5");

        if (!state.HasStarted)
        {
            return new ProxyBatchDeepComparisonBadge("B5", "--", ProxyBatchDeepComparisonBadgeState.Pending, title, description);
        }

        if (completedCount < baselineKinds.Count)
        {
            return new ProxyBatchDeepComparisonBadge("B5", $"{passCount}/5", ProxyBatchDeepComparisonBadgeState.Running, title, description);
        }

        return new ProxyBatchDeepComparisonBadge(
            "B5",
            $"{passCount}/5",
            passCount == 5
                ? ProxyBatchDeepComparisonBadgeState.Pass
                : passCount >= 3
                    ? ProxyBatchDeepComparisonBadgeState.Warn
                    : ProxyBatchDeepComparisonBadgeState.Fail,
            title,
            description);
    }

    private static ProxyBatchDeepComparisonBadge BuildBatchDeepScenarioBadge(
        BatchDeepChartRowState state,
        ProxyProbeScenarioKind kind,
        string label,
        bool enabled)
    {
        var (title, description) = GetBatchDeepBadgeDefinition(label);

        if (!enabled)
        {
            return new ProxyBatchDeepComparisonBadge(
                label,
                "Off",
                ProxyBatchDeepComparisonBadgeState.Pending,
                title,
                description,
                BuildBatchDeepScenarioPlaceholderDetail(
                    kind,
                    "\u7ed3\u679c\uff1a\u672a\u542f\u7528",
                    "\u5f53\u524d\u6279\u91cf\u6df1\u6d4b\u8ba1\u5212\u672a\u52fe\u9009\u8be5\u9879\u76ee\u3002"));
        }

        if (state.ScenarioResults.TryGetValue(kind, out var scenario))
        {
            return new ProxyBatchDeepComparisonBadge(
                label,
                ResolveBatchDeepScenarioBadgeValue(scenario),
                ResolveBatchDeepScenarioBadgeState(scenario),
                title,
                description,
                BuildBatchDeepScenarioTooltipDetail(kind, scenario));
        }

        if (state.IsCompleted)
        {
            return new ProxyBatchDeepComparisonBadge(
                label,
                "SK",
                ProxyBatchDeepComparisonBadgeState.Warn,
                title,
                description,
                BuildBatchDeepScenarioPlaceholderDetail(
                    kind,
                    "\u7ed3\u679c\uff1a\u5df2\u8df3\u8fc7",
                    "\u672c\u8f6e\u6df1\u6d4b\u5df2\u7ed3\u675f\uff0c\u4f46\u8be5\u9879\u76ee\u6ca1\u6709\u8fd4\u56de\u7ed3\u679c\uff0c\u901a\u5e38\u8868\u793a\u88ab\u8df3\u8fc7\u6216\u63d0\u524d\u7ec8\u6b62\u3002"));
        }

        if (state.IsRunning && state.CompletedCount > 0)
        {
            return new ProxyBatchDeepComparisonBadge(
                label,
                "--",
                ProxyBatchDeepComparisonBadgeState.Running,
                title,
                description,
                BuildBatchDeepScenarioPlaceholderDetail(
                    kind,
                    "\u7ed3\u679c\uff1a\u8fdb\u884c\u4e2d",
                    "\u8be5\u9879\u76ee\u5c1a\u672a\u8fd4\u56de\u6700\u7ec8\u7ed3\u679c\uff0c\u8bf7\u7b49\u5f85\u5f53\u524d\u7ad9\u70b9\u8dd1\u5b8c\u8fd9\u4e00\u9879\u3002"));
        }

        return new ProxyBatchDeepComparisonBadge(
            label,
            "--",
            ProxyBatchDeepComparisonBadgeState.Pending,
            title,
            description,
            BuildBatchDeepScenarioPlaceholderDetail(
                kind,
                "\u7ed3\u679c\uff1a\u672a\u5f00\u59cb",
                "\u8be5\u9879\u76ee\u5c1a\u672a\u5f00\u59cb\u6267\u884c\u3002"));
    }

    private static (string Title, string Description) GetBatchDeepBadgeDefinition(string label)
        => label switch
        {
            "B5" => (
                "基础 5 项",
                "快速核对基础 5 项：/models、普通对话、流式对话、Responses、结构化输出。数值表示通过项数 / 5。"),
            "Sys" => (
                "System Prompt 映射",
                "检查接口是否能正确传递 system 指令，避免系统提示词被吞掉、改写或错位。"),
            "Fn" => (
                "Function Calling",
                "检查工具调用 / function calling 的请求格式、参数回填和响应结构是否兼容。"),
            "Err" => (
                "错误透传",
                "检查报错时是否能保留原始错误语义，而不是统一改写成模糊或误导性的错误。"),
            "Str" => (
                "流式完整性",
                "检查流式输出是否连续、收尾是否完整，以及是否存在截断、乱序或异常结束。"),
            "Ref" => (
                "官方对照",
                "把关键结果与官方接口基线做对照，判断能力、格式和表现是否明显偏离。"),
            "MM" => (
                "多模态",
                "检查图片等多模态输入是否可以被正确接收、转发并返回可用结果。"),
            "Cch" => (
                "缓存命中",
                "检查重复请求是否表现出缓存命中迹象，用来判断站点是否存在复用响应的缓存层。"),
            "Iso" => (
                "缓存隔离",
                "检查不同账号 / key / 会话之间的缓存是否正确隔离，避免出现串号或交叉命中。"),
            _ => (
                label,
                "该标签表示本轮深度测试中的一个专项探针结果。")
        };

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

        if (scenario.FailureKind is ProxyFailureKind.ConfigurationInvalid)
        {
            return ProxyBatchDeepComparisonBadgeState.Warn;
        }

        var status = scenario.CapabilityStatus ?? string.Empty;
        if (status.Contains("待复核", StringComparison.Ordinal) ||
            status.Contains("未执行", StringComparison.Ordinal) ||
            status.Contains("前置不足", StringComparison.Ordinal) ||
            status.Contains("参数", StringComparison.Ordinal) ||
            status.Contains("配置", StringComparison.Ordinal))
        {
            return ProxyBatchDeepComparisonBadgeState.Warn;
        }

        return ProxyBatchDeepComparisonBadgeState.Fail;
    }

    private static string BuildBatchDeepScenarioTooltipDetail(ProxyProbeScenarioKind kind, ProxyProbeScenarioResult scenario)
    {
        var preview = NormalizeInlineText(scenario.Preview);
        var summary = NormalizeInlineText(scenario.Summary);
        var capabilityStatus = NormalizeInlineText(scenario.CapabilityStatus);
        List<string> actualParts = [];

        if (!string.IsNullOrWhiteSpace(capabilityStatus))
        {
            actualParts.Add($"\u72b6\u6001\uff1a{TrimInline(capabilityStatus, 120)}");
        }

        if (scenario.StatusCode.HasValue)
        {
            actualParts.Add($"HTTP {scenario.StatusCode.Value}");
        }

        if (!string.IsNullOrWhiteSpace(preview))
        {
            actualParts.Add($"\u8fd4\u56de\u7247\u6bb5\uff1a{TrimInline(preview, 180)}");
        }

        if (!string.IsNullOrWhiteSpace(summary) &&
            !string.Equals(summary, preview, StringComparison.Ordinal))
        {
            actualParts.Add($"\u89c2\u6d4b\uff1a{TrimInline(summary, 180)}");
        }

        var actual = actualParts.Count == 0
            ? "\u672a\u63d0\u4f9b\u53ef\u7528\u8fd4\u56de\u7247\u6bb5\u3002"
            : string.Join("\uff1b", actualParts);

        var reason = FirstNonEmpty(
                NormalizeInlineText(scenario.Error),
                summary,
                capabilityStatus)
            ?? (scenario.Success
                ? "\u8fd4\u56de\u7ed3\u679c\u4e0e\u9884\u671f\u4e00\u81f4\u3002"
                : "\u672a\u63d0\u4f9b\u989d\u5916\u539f\u56e0\u3002");

        var reasonLabel = scenario.Success ? "\u8bf4\u660e\uff1a" : "\u539f\u56e0\uff1a";

        return string.Join(
            Environment.NewLine,
            ResolveBatchDeepScenarioResultText(scenario),
            $"\u9884\u671f\uff1a{GetBatchDeepScenarioExpectedText(kind)}",
            $"\u5b9e\u9645\uff1a{actual}",
            $"{reasonLabel}{TrimInline(reason, 220)}");
    }

    private static string BuildBatchDeepScenarioPlaceholderDetail(
        ProxyProbeScenarioKind kind,
        string resultText,
        string actualText)
        => string.Join(
            Environment.NewLine,
            resultText,
            $"\u9884\u671f\uff1a{GetBatchDeepScenarioExpectedText(kind)}",
            $"\u5b9e\u9645\uff1a{actualText}");

    private static string ResolveBatchDeepScenarioResultText(ProxyProbeScenarioResult scenario)
        => ResolveBatchDeepScenarioBadgeValue(scenario) switch
        {
            "OK" => "\u7ed3\u679c\uff1a\u901a\u8fc7",
            "RV" => "\u7ed3\u679c\uff1a\u5f85\u590d\u6838",
            "CFG" => "\u7ed3\u679c\uff1a\u914d\u7f6e\u4e0d\u8db3",
            "SK" => "\u7ed3\u679c\uff1a\u5df2\u8df3\u8fc7",
            "--" => "\u7ed3\u679c\uff1a\u672a\u5f00\u59cb",
            _ => "\u7ed3\u679c\uff1a\u672a\u901a\u8fc7"
        };

    private static string GetBatchDeepScenarioExpectedText(ProxyProbeScenarioKind kind)
        => kind switch
        {
            ProxyProbeScenarioKind.Models or
            ProxyProbeScenarioKind.ChatCompletions or
            ProxyProbeScenarioKind.ChatCompletionsStream or
            ProxyProbeScenarioKind.Responses or
            ProxyProbeScenarioKind.StructuredOutput
                => "\u5e94\u4fdd\u6301\u57fa\u7840 5 \u9879\u8fde\u901a\u6027\uff1a/models\u3001\u666e\u901a\u5bf9\u8bdd\u3001\u6d41\u5f0f\u5bf9\u8bdd\u3001Responses\u3001\u7ed3\u6784\u5316\u8f93\u51fa\u90fd\u5e94\u8fd4\u56de\u6709\u6548\u7ed3\u679c\u3002",
            ProxyProbeScenarioKind.SystemPromptMapping
                => "\u6a21\u578b\u5e94\u7a33\u5b9a\u9075\u5faa system \u6307\u4ee4\uff0c\u4e0d\u5e94\u88ab\u7528\u6237\u8986\u76d6\u63d0\u793a\u5e26\u504f\u3002",
            ProxyProbeScenarioKind.FunctionCalling
                => "\u5e94\u8fd4\u56de\u5408\u6cd5\u7684 tool_calls / function calling \u7ed3\u6784\uff0c\u53c2\u6570\u4e0e\u6700\u7ec8\u7b54\u6848\u90fd\u5e94\u7b26\u5408\u9884\u671f\u3002",
            ProxyProbeScenarioKind.ErrorTransparency
                => "\u6784\u9020 bad request \u540e\uff0c\u5e94\u8fd4\u56de 4xx\uff0c\u5e76\u4fdd\u7559\u53ef\u8bfb\u3001\u53ef\u5b9a\u4f4d\u7684\u539f\u59cb\u9519\u8bef\u4fe1\u606f\u3002",
            ProxyProbeScenarioKind.StreamingIntegrity
                => "\u6d41\u5f0f\u8f93\u51fa\u5e94\u5b8c\u6574\u6536\u5c3e\uff0c\u975e\u6d41\u5f0f\u4e0e\u6d41\u5f0f\u6838\u5fc3\u5185\u5bb9\u5e94\u4e00\u81f4\uff0c\u4e0d\u5e94\u622a\u65ad\u3001\u4e71\u5e8f\u6216\u5f02\u5e38\u7ed3\u675f\u3002",
            ProxyProbeScenarioKind.OfficialReferenceIntegrity
                => "\u4e2d\u8f6c\u7ad9\u4e0e\u5b98\u65b9\u63a5\u53e3\u5bf9\u540c\u4e00\u63d0\u793a\u5e94\u8868\u73b0\u4e00\u81f4\uff0c\u5173\u952e\u8f93\u51fa\u4e0d\u5e94\u660e\u663e\u504f\u79bb\u3002",
            ProxyProbeScenarioKind.MultiModal
                => "\u53cc\u56fe\u591a\u6a21\u6001\u8bf7\u6c42\u5e94\u88ab\u6b63\u786e\u8bc6\u522b\uff0c\u8fd4\u56de\u5185\u5bb9\u9700\u660e\u786e\u533a\u5206\u7ea2\u56fe\u4e0e\u84dd\u56fe\u3002",
            ProxyProbeScenarioKind.CacheMechanism
                => "\u91cd\u590d\u8bf7\u6c42\u5e94\u8fd4\u56de\u9884\u671f\u5185\u5bb9\uff0c\u5e76\u6839\u636e TTFT \u53d8\u5316\u4f53\u73b0\u51fa\u5408\u7406\u7f13\u5b58\u8ff9\u8c61\uff0c\u6216\u660e\u786e\u8bf4\u660e\u672a\u547d\u4e2d\u3002",
            ProxyProbeScenarioKind.CacheIsolation
                => "A/B Key \u7684\u7f13\u5b58\u5e94\u5f7c\u6b64\u9694\u79bb\uff0cB \u4e0d\u5e94\u8bfb\u5230 A \u7684 secret\uff0c\u4e14\u4e24\u6b21\u8f93\u51fa\u90fd\u5e94\u7b26\u5408\u5404\u81ea\u9884\u671f\u3002",
            _ => "\u5e94\u8fd4\u56de\u4e0e\u8be5\u4e13\u9879\u80fd\u529b\u4e00\u81f4\u7684\u9884\u671f\u7ed3\u679c\u3002"
        };

    private static string ResolveBatchDeepScenarioBadgeValue(ProxyProbeScenarioResult scenario)
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
