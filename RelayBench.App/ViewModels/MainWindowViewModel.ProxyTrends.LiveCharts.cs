using System.Text;
using System.Windows.Media.Imaging;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private ProxySingleExecutionPlan GetActiveSingleExecutionPlan()
        => _currentProxySingleExecutionPlan ?? BuildBasicProxySingleExecutionPlan();

    private string GetSingleProxyChartRetryButtonText()
        => GetActiveSingleExecutionPlan().Mode == ProxySingleExecutionMode.Deep
            ? "重试深度诊断"
            : "重试基础诊断";

    private string GetSingleProxyChartTitle(bool isLive)
        => GetActiveSingleExecutionPlan().Mode == ProxySingleExecutionMode.Deep
            ? (isLive ? "深度单次诊断实时图表" : "深度单次诊断结果图表")
            : (isLive ? "基础单次诊断实时图表" : "基础单次诊断结果图表");

    private void StartSingleProxyChartLiveSession()
    {
        ResetProxyTrendChartAutoOpenSuppression();
        var executionPlan = GetActiveSingleExecutionPlan();
        SetProxyChartRetryMode(ProxyChartRetryMode.Single, GetSingleProxyChartRetryButtonText());
        var baseUrl = string.IsNullOrWhiteSpace(ProxyBaseUrl) ? "（未填写）" : ProxyBaseUrl.Trim();
        var model = string.IsNullOrWhiteSpace(ProxyModel) ? "（待选择）" : ProxyModel.Trim();
        var items = BuildLiveSingleCapabilityChartItems(
            Array.Empty<ProxyProbeScenarioResult>(),
            modelCount: 0,
            sampleModels: Array.Empty<string>(),
            completedBaselineCount: 0,
            totalBaselineCount: 5);
        var totalCount = items.Count;
        var chartResult = _proxySingleCapabilityChartRenderService.Render(
            baseUrl,
            model,
            items,
            completedCount: 0,
            totalCount,
            $"{GetSingleProxyExecutionDisplayName()}已启动，正在等待 /models 返回。",
            ResolvePreferredSingleChartWidth());

        SetProxyChartSnapshot(
            ProxyChartViewMode.SingleLatency,
            new ProxyChartDialogSnapshot(
                GetSingleProxyChartTitle(isLive: true),
                executionPlan.Mode == ProxySingleExecutionMode.Deep
                    ? "弹窗已提前打开，基础 5 项会先实时刷新；当前深度诊断计划中的增强/深度探针也会先显示待执行占位。"
                    : "弹窗已提前打开，基础能力会先实时刷新；如果启用了长流稳定增强测试，下面也会先显示待执行占位。",
                $"目标：{baseUrl}\n" +
                $"模型：{model}\n" +
                $"进度：0/{totalCount}\n" +
                $"当前状态：准备开始{GetSingleProxyExecutionDisplayName()}。",
                BuildLiveCapabilityMatrix(Array.Empty<ProxyProbeScenarioResult>(), 0, 5),
                BuildLiveCapabilityDetail(
                    Array.Empty<ProxyProbeScenarioResult>(),
                    completedCount: 0,
                    totalCount: 5,
                    modelCount: 0,
                    sampleModels: Array.Empty<string>()),
                "基础能力实时推进；增强与深度项会先显示待执行，最终完成后会统一补齐到图表里。",
                $"{GetSingleProxyExecutionDisplayName()}已启动，正在等待 /models 返回。",
                $"正在初始化{GetSingleProxyExecutionDisplayName()}实时图表...",
                chartResult.ChartImage),
            activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

    private void UpdateSingleProxyChartLive(ProxyDiagnosticsLiveProgress progress)
    {
        var items = BuildLiveSingleCapabilityChartItems(
            progress.ScenarioResults,
            progress.ModelCount,
            progress.SampleModels,
            progress.CompletedScenarioCount,
            progress.TotalScenarioCount);
        var completedCount = items.Count(item => item.IsCompleted);
        var totalCount = items.Count;
        var chartResult = _proxySingleCapabilityChartRenderService.Render(
                    progress.BaseUrl,
                    progress.EffectiveModel ?? progress.RequestedModel,
                    items,
                    completedCount,
                    totalCount,
            $"{progress.CurrentScenarioResult.DisplayName}：{progress.CurrentScenarioResult.Summary}",
            ResolvePreferredSingleChartWidth());

        SetProxyChartSnapshot(
            ProxyChartViewMode.SingleLatency,
            new ProxyChartDialogSnapshot(
                GetSingleProxyChartTitle(isLive: true),
                GetActiveSingleExecutionPlan().Mode == ProxySingleExecutionMode.Deep
                    ? "弹窗正在实时刷新基础 5 项；深度诊断计划中的增强测试和深度测试会以待执行或已完成状态显示在下方分区。"
                    : "弹窗正在实时刷新基础能力；当前基础诊断包含的增强测试会以待执行或已完成状态显示在下方分区。",
                $"目标：{progress.BaseUrl}\n" +
                $"模型：{progress.EffectiveModel ?? progress.RequestedModel}\n" +
                $"进度：{completedCount}/{totalCount}\n" +
                $"最新返回：{progress.CurrentScenarioResult.DisplayName} / {progress.CurrentScenarioResult.CapabilityStatus}",
                BuildLiveCapabilityMatrix(
                    progress.ScenarioResults,
                    progress.CompletedScenarioCount,
                    progress.TotalScenarioCount),
                BuildLiveCapabilityDetail(
                    progress.ScenarioResults,
                    progress.CompletedScenarioCount,
                    progress.TotalScenarioCount,
                    progress.ModelCount,
                    progress.SampleModels),
                "实时阶段先刷新基础能力；补充探针和长流简测开始后，图表也会切换到对应分区状态。",
                $"{progress.CurrentScenarioResult.DisplayName} 已返回：{progress.CurrentScenarioResult.Summary}",
                $"正在等待{GetSingleProxyExecutionDisplayName()}图表数据...",
                chartResult.ChartImage),
            activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

    private void ShowSingleProxySupplementalChartPhase(ProxyDiagnosticsResult result, string phaseTitle, string phaseStatus)
    {
        var items = BuildSupplementalPhaseSingleCapabilityChartItems(result);
        var completedCount = items.Count(item => item.IsCompleted);
        var totalCount = items.Count;
        var chartResult = _proxySingleCapabilityChartRenderService.Render(
            result.BaseUrl,
            result.EffectiveModel ?? result.RequestedModel,
            items,
            completedCount,
            totalCount,
            phaseStatus,
            ResolvePreferredSingleChartWidth());

        SetProxyChartSnapshot(
            ProxyChartViewMode.SingleLatency,
            new ProxyChartDialogSnapshot(
                GetSingleProxyChartTitle(isLive: true),
                GetActiveSingleExecutionPlan().Mode == ProxySingleExecutionMode.Deep
                    ? "基础 5 项已结束，当前正在继续执行深度测试页选中的复杂探针；图表会保留已完成项，并标记剩余待执行项。"
                    : "基础能力已结束，当前正在继续执行基础页启用的增强测试；图表会保留已完成项，并标记剩余待执行项。",
                $"目标：{result.BaseUrl}\n" +
                $"模型：{result.EffectiveModel ?? result.RequestedModel}\n" +
                $"进度：{completedCount}/{totalCount}\n" +
                $"当前阶段：{phaseTitle}",
                BuildDialogCapabilityMatrix(result),
                BuildDialogCapabilityDetail(result),
                "基础能力先完成，随后继续跑协议兼容、错误透传、官方对照或长流简测等补充项。",
                phaseStatus,
                $"正在等待{GetSingleProxyExecutionDisplayName()}图表数据...",
                chartResult.ChartImage),
            activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

    private IReadOnlyList<ProxySingleCapabilityChartItem> BuildLiveSingleCapabilityChartItems(

        IReadOnlyList<ProxyProbeScenarioResult> scenarios,

        int modelCount,

        IReadOnlyList<string> sampleModels,

        int completedBaselineCount,

        int totalBaselineCount)

    {

        var items = BuildSingleCapabilityChartItems(

            scenarios,

            modelCount,

            sampleModels,

            completedBaselineCount,

            totalBaselineCount).ToList();

        var nextOrder = ResolveNextSingleCapabilityOrder(items);

        AppendCompletedSupplementalCapabilityChartItems(items, ref nextOrder, scenarios);

        AppendPendingSupplementalCapabilityChartItems(items, ref nextOrder, skipExisting: true);

        return items;

    }



    private static void AppendCompletedSupplementalCapabilityChartItems(

        ICollection<ProxySingleCapabilityChartItem> items,

        ref int nextOrder,

        IReadOnlyList<ProxyProbeScenarioResult> scenarios)

    {

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.StreamingIntegrity,

            "\u589e\u5f3a\u6d4b\u8bd5",

            "\u957f\u6d41\u4fdd\u6301\u4e0e\u5185\u5bb9\u5b8c\u6574\u6027",

            "\u6d41\u5f0f\u5b8c\u6574\u6027",

            previewOverride: BuildStreamingIntegrityDigest,

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "SSE \u7247\u6bb5\u5b8c\u6574\u6027"));



        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.SystemPromptMapping,

            "\u6df1\u5ea6\u6d4b\u8bd5",

            "\u534f\u8bae\u517c\u5bb9\u3001\u9519\u8bef\u900f\u4f20\u4e0e\u7f13\u5b58\u9694\u79bb",

            "System Prompt",

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u89d2\u8272\u6620\u5c04\u4e0e\u6307\u4ee4\u6ce8\u5165"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.FunctionCalling,

            "\u6df1\u5ea6\u6d4b\u8bd5",

            "\u534f\u8bae\u517c\u5bb9\u3001\u9519\u8bef\u900f\u4f20\u4e0e\u7f13\u5b58\u9694\u79bb",

            "Function Calling",

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u5de5\u5177\u8c03\u7528\u534f\u8bae\u5bf9\u9f50"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.ErrorTransparency,

            "\u6df1\u5ea6\u6d4b\u8bd5",

            "\u534f\u8bae\u517c\u5bb9\u3001\u9519\u8bef\u900f\u4f20\u4e0e\u7f13\u5b58\u9694\u79bb",

            "\u9519\u8bef\u900f\u4f20",

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u4e0a\u6e38\u9519\u8bef\u4e0e\u72b6\u6001\u7801\u6620\u5c04"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.OfficialReferenceIntegrity,

            "\u6df1\u5ea6\u6d4b\u8bd5",

            "\u534f\u8bae\u517c\u5bb9\u3001\u9519\u8bef\u900f\u4f20\u4e0e\u7f13\u5b58\u9694\u79bb",

            "\u5b98\u65b9\u5bf9\u7167\u5b8c\u6574\u6027",

            previewOverride: BuildOfficialReferenceIntegrityDigest,

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u4e0e\u5b98\u65b9\u8f93\u51fa\u5bf9\u7167"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.MultiModal,

            "\u6df1\u5ea6\u6d4b\u8bd5",

            "\u534f\u8bae\u517c\u5bb9\u3001\u9519\u8bef\u900f\u4f20\u4e0e\u7f13\u5b58\u9694\u79bb",

            "\u591a\u6a21\u6001",

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u56fe\u7247 / \u6587\u4ef6\u900f\u4f20"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.CacheMechanism,

            "\u6df1\u5ea6\u6d4b\u8bd5",

            "\u534f\u8bae\u517c\u5bb9\u3001\u9519\u8bef\u900f\u4f20\u4e0e\u7f13\u5b58\u9694\u79bb",

            "\u7f13\u5b58\u547d\u4e2d",

            previewOverride: BuildCacheMechanismDigest,

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u91cd\u590d Prompt \u547d\u4e2d\u7f13\u5b58"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.CacheIsolation,

            "\u6df1\u5ea6\u6d4b\u8bd5",

            "\u534f\u8bae\u517c\u5bb9\u3001\u9519\u8bef\u900f\u4f20\u4e0e\u7f13\u5b58\u9694\u79bb",

            "\u7f13\u5b58\u9694\u79bb",

            previewOverride: BuildCacheIsolationDigest,

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u8de8\u8d26\u6237\u9694\u79bb\u6821\u9a8c"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.Embeddings,

            "\u975E\u804A\u5929 API",

            "embeddings / images / audio / moderation",

            "Embeddings",

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u5411\u91cf\u5316\u80FD\u529B"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.Images,

            "\u975E\u804A\u5929 API",

            "embeddings / images / audio / moderation",

            "Images",

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u751F\u56FE\u80FD\u529B"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.AudioTranscription,

            "\u975E\u804A\u5929 API",

            "embeddings / images / audio / moderation",

            "Audio Transcription",

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u97F3\u9891\u8F6C\u5199\u80FD\u529B"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.AudioSpeech,

            "\u975E\u804A\u5929 API",

            "embeddings / images / audio / moderation",

            "Audio Speech / TTS",

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u6587\u672C\u8F6C\u8BED\u97F3\u80FD\u529B"));

        AddScenarioChartItemIfPresent(

            items,

            ref nextOrder,

            scenarios,

            ProxyProbeScenarioKind.Moderation,

            "\u975E\u804A\u5929 API",

            "embeddings / images / audio / moderation",

            "Moderation",

            detailOverride: scenario => BuildScenarioChartDetail(scenario, "\u5185\u5BB9\u5BA1\u6838\u80FD\u529B"));

    }



    private IReadOnlyList<ProxySingleCapabilityChartItem> BuildSupplementalPhaseSingleCapabilityChartItems(ProxyDiagnosticsResult result)
    {
        var items = BuildFinalSingleCapabilityChartItems(result).ToList();
        var nextOrder = (items.Count == 0 ? 0 : items.Max(item => item.Order)) + 1;
        AppendPendingSupplementalCapabilityChartItems(items, ref nextOrder, skipExisting: true);

        return items;
    }

    private void AppendPendingSupplementalCapabilityChartItems(
        ICollection<ProxySingleCapabilityChartItem> items,
        ref int nextOrder)
        => AppendPendingSupplementalCapabilityChartItems(items, ref nextOrder, skipExisting: false);

    private void AppendPendingSupplementalCapabilityChartItems(
        ICollection<ProxySingleCapabilityChartItem> items,
        ref int nextOrder,
        bool skipExisting)
    {
        var executionPlan = GetActiveSingleExecutionPlan();
        var order = nextOrder;
        bool HasExisting(string label)
            => skipExisting && items.Any(item => string.Equals(item.Name, label, StringComparison.Ordinal));

        void AddPending(
            string sectionName,
            string sectionHint,
            string label,
            string detailText,
            string previewText)
        {
            if (HasExisting(label))
            {
                return;
            }

            items.Add(CreateFinalScenarioPlaceholderItem(
                order++,
                sectionName,
                sectionHint,
                label,
                "未运行",
                detailText,
                previewText));
        }

        AddPending(
            "增强测试",
            "长流保持、独立吞吐与内容完整性",
            "独立吞吐",
            "等待开始独立吞吐测试",
            "将进行 3 轮 tok/s 独立测速");

        if (executionPlan.EnableLongStreamingTest)
        {
            AddPending(
                "增强测试",
                "长流保持与内容完整性",
                "长流稳定",
                "等待开始长流简测",
                "将在基础与深度探针结束后继续执行");
        }

        if (executionPlan.EnableStreamingIntegrityTest)
        {
            AddPending(
                "增强测试",
                "长流保持与内容完整性",
                "流式完整性",
                "等待基础流式完成后比对",
                "将对比流式与非流式输出完整性");
        }

        if (executionPlan.EnableProtocolCompatibilityTest)
        {
            AddPending(
                "深度测试",
                "协议兼容、错误透传与缓存隔离",
                "System Prompt",
                "等待补充探针",
                "将检查 system 角色映射");
            AddPending(
                "深度测试",
                "协议兼容、错误透传与缓存隔离",
                "Function Calling",
                "等待补充探针",
                "将检查工具调用协议兼容");
        }

        if (executionPlan.EnableErrorTransparencyTest)
        {
            AddPending(
                "深度测试",
                "协议兼容、错误透传与缓存隔离",
                "错误透传",
                "等待补充探针",
                "将检查上游状态码与错误信息透传");
        }

        if (executionPlan.EnableOfficialReferenceIntegrityTest)
        {
            AddPending(
                "深度测试",
                "协议兼容、错误透传与缓存隔离",
                "官方对照完整性",
                "等待补充探针",
                "将对照官方参考端输出");
        }

        if (executionPlan.EnableMultiModalTest)
        {
            AddPending(
                "深度测试",
                "协议兼容、错误透传与缓存隔离",
                "多模态",
                "等待补充探针",
                "将检查图片/文件透传能力");
        }

        if (executionPlan.EnableCacheMechanismTest)
        {
            AddPending(
                "深度测试",
                "协议兼容、错误透传与缓存隔离",
                "缓存命中",
                "等待补充探针",
                "将检查重复 Prompt 的缓存命中");
        }

        if (executionPlan.EnableCacheIsolationTest)
        {
            AddPending(
                "深度测试",
                "协议兼容、错误透传与缓存隔离",
                "缓存隔离",
                "等待补充探针",
                "将检查跨账户缓存隔离");
        }

        if (executionPlan.Mode == ProxySingleExecutionMode.Deep)
        {
            foreach (var definition in GetConfiguredCapabilityMatrixDefinitions())
            {
                AddPending(
                    "\u975E\u804A\u5929 API",
                    "embeddings / images / audio / moderation",
                    definition.Name,
                    "\u7B49\u5F85\u975E\u804A\u5929 API \u80FD\u529B\u77E9\u9635",
                    $"\u5C06\u68C0\u67E5 {definition.Path}");
            }
        }

        if (executionPlan.MultiModelBenchmarkModels.Count > 0)
        {
            foreach (var model in executionPlan.MultiModelBenchmarkModels)
            {
                AddPending(
                    "多模型测速",
                    "串行单流 tok/s 对比",
                    model,
                    "等待开始多模型串行测速",
                    "将比较该模型的单流输出 tok/s");
            }
        }

        nextOrder = order;
    }

    private static int ResolveNextSingleCapabilityOrder(IReadOnlyCollection<ProxySingleCapabilityChartItem> items)
        => (items.Count == 0 ? 0 : items.Max(item => item.Order)) + 1;

    private void ShowFinalSingleProxyChart(ProxyDiagnosticsResult result)
    {
        SetProxyChartRetryMode(ProxyChartRetryMode.Single, GetSingleProxyChartRetryButtonText());
        var items = BuildFinalSingleCapabilityChartItems(result);
        var completedCount = items.Count(item => item.IsCompleted);
        var totalCount = items.Count;
        var chartResult = _proxySingleCapabilityChartRenderService.Render(
            result.BaseUrl,
            result.EffectiveModel ?? result.RequestedModel,
            items,
            completedCount,
            totalCount,
            result.Summary,
            ResolvePreferredSingleChartWidth());

        SetProxyChartSnapshot(
            ProxyChartViewMode.SingleLatency,
            new ProxyChartDialogSnapshot(
                GetSingleProxyChartTitle(isLive: false),
                GetActiveSingleExecutionPlan().Mode == ProxySingleExecutionMode.Deep
                    ? "这里按基础能力、增强测试、非聊天 API、深度测试分区展示本次深度单次诊断结果；如果你想看同一地址的多次波动趋势，可以切换到稳定趋势图。"
                    : "这里按基础能力、增强测试、非聊天 API、深度测试分区展示本次基础单次诊断结果；如果你想看同一地址的多次波动趋势，可以切换到稳定趋势图。",
                $"目标：{result.BaseUrl}\n" +
                $"累计次数：{_proxySingleChartRuns.Count}\n" +
                $"总判定：{result.Verdict ?? "待复核"}\n" +
                $"建议用途：{result.Recommendation ?? "请结合稳定性结果继续判断。"}\n" +
                $"已展示检测项：{completedCount}/{totalCount}\n" +
                $"普通对话：{FormatMilliseconds(result.ChatLatency)} / 独立吞吐：{FormatTokensPerSecond(result.ThroughputBenchmarkResult?.MedianOutputTokensPerSecond, result.ThroughputBenchmarkResult?.OutputTokenCountEstimated == true, result.ThroughputBenchmarkResult?.CompletedSampleCount ?? 1)} / TTFT：{FormatMilliseconds(result.StreamFirstTokenLatency)}",
                BuildDialogCapabilityMatrix(result),
                BuildDialogCapabilityDetail(result),
                "基础能力用于看核心 API 通断；增强测试用于看长流与内容完整性；非聊天 API 用于看 embeddings / images / audio / moderation；深度测试用于看协议兼容、错误透传、缓存与官方对照。",
                chartResult.Summary,
                $"当前{GetSingleProxyExecutionDisplayName()}图表正在生成。",
                chartResult.ChartImage),
            activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

    private void StartProxySeriesChartLiveSession(int requestedRounds, int delayMilliseconds)
    {
        ResetProxyTrendChartAutoOpenSuppression();
        SetProxyChartRetryMode(ProxyChartRetryMode.Stability, "追加重试 5 轮");
        var baseUrl = string.IsNullOrWhiteSpace(ProxyBaseUrl) ? "（未填写）" : ProxyBaseUrl.Trim();
        var placeholderRecords = BuildLiveSeriesChartEntries(Array.Empty<ProxyDiagnosticsResult>(), baseUrl, includePlaceholder: true);
        var chartResult = _proxyTrendChartRenderService.Render(placeholderRecords, baseUrl);

        SetProxyChartSnapshot(
            ProxyChartViewMode.StabilityTrend,
            new ProxyChartDialogSnapshot(
                "稳定性巡检实时图表",
                "弹窗已提前打开；每完成一轮，稳定性、普通延迟和 TTFT 会立即刷新到图里。",
                $"目标：{baseUrl}\n" +
                $"计划轮次：{requestedRounds}\n" +
                $"间隔：{delayMilliseconds} ms\n" +
                "当前状态：等待第 1 轮结果。",
                "最近一轮能力矩阵：尚未开始。\n累计通过：/models 0/0；普通对话 0/0；流式对话 0/0；Responses 0/0；结构化输出 0/0",
                "最近一轮能力明细：尚未开始。",
                "每完成一轮，图里会立刻加一个样本点，方便你观察延迟和 TTFT 是否抖动。",
                $"稳定性巡检已启动，计划 {requestedRounds} 轮。",
                "正在等待第 1 轮结果...",
                chartResult.ChartImage),
            activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

    private void UpdateProxySeriesChartLive(
        IReadOnlyList<ProxyDiagnosticsResult> rounds,
        int requestedRounds,
        int delayMilliseconds)
    {
        var baseUrl = rounds.FirstOrDefault()?.BaseUrl ?? ProxyBaseUrl.Trim();
        var chartEntries = BuildLiveSeriesChartEntries(rounds, baseUrl, includePlaceholder: rounds.Count == 0);
        var chartResult = _proxyTrendChartRenderService.Render(chartEntries, baseUrl);
        var latestRound = rounds.OrderByDescending(item => item.CheckedAt).FirstOrDefault();
        var fullSuccessCount = rounds.Count(IsFullSuccess);
        var avgChatLatency = Average(rounds.Select(round => round.ChatLatency?.TotalMilliseconds));
        var avgTtft = Average(rounds.Select(round => round.StreamFirstTokenLatency?.TotalMilliseconds));
        var modelsPassed = rounds.Count(round => round.ModelsRequestSucceeded);
        var chatPassed = rounds.Count(round => round.ChatRequestSucceeded);
        var streamPassed = rounds.Count(round => round.StreamRequestSucceeded);
        var responsesPassed = rounds.Count(round => FindScenario(GetScenarioResults(round), ProxyProbeScenarioKind.Responses)?.Success == true);
        var structuredPassed = rounds.Count(round => FindScenario(GetScenarioResults(round), ProxyProbeScenarioKind.StructuredOutput)?.Success == true);

        SetProxyChartSnapshot(
            ProxyChartViewMode.StabilityTrend,
            new ProxyChartDialogSnapshot(
                "稳定性巡检实时图表",
                "弹窗正在按轮次实时刷新；如果某一轮抖动明显，你会立刻在图里看到折线变化。",
                $"目标：{baseUrl}\n" +
                $"已完成：{rounds.Count}/{requestedRounds}\n" +
                $"间隔：{delayMilliseconds} ms\n" +
                $"当前完整通过：{fullSuccessCount}/{rounds.Count}\n" +
                $"当前平均普通延迟：{FormatMillisecondsValue(avgChatLatency)} / 当前平均 TTFT：{FormatMillisecondsValue(avgTtft)}",
                latestRound is null
                    ? "最近一轮能力矩阵：尚未开始。"
                    : $"最近一轮能力矩阵（{latestRound.CheckedAt:HH:mm:ss}）\n" +
                      BuildDialogCapabilityMatrix(latestRound) + "\n\n" +
                      $"累计通过：/models {modelsPassed}/{rounds.Count}；普通对话 {chatPassed}/{rounds.Count}；流式对话 {streamPassed}/{rounds.Count}；Responses {responsesPassed}/{rounds.Count}；结构化输出 {structuredPassed}/{rounds.Count}",
                latestRound is null
                    ? "最近一轮能力明细：尚未开始。"
                    : $"最近一轮能力明细\n{BuildDialogCapabilityDetail(latestRound)}",
                "实时巡检更适合看“这一轮是不是突然变慢、TTFT 是否飙高、成功项是否缺失”。",
                latestRound is null
                    ? $"稳定性巡检已启动，计划 {requestedRounds} 轮。"
                    : $"已完成 {rounds.Count}/{requestedRounds} 轮，最近一轮结论：{latestRound.Verdict ?? latestRound.Summary}",
                "正在等待稳定性巡检图表数据...",
                chartResult.ChartImage),
            activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

    private void ShowFinalProxySeriesChart(ProxyStabilityResult result)
    {
        SetProxyChartRetryMode(ProxyChartRetryMode.Stability, "追加重试 5 轮");
        var latestRound = result.RoundResults.OrderByDescending(item => item.CheckedAt).FirstOrDefault();
        var chartEntries = BuildLiveSeriesChartEntries(result.RoundResults, result.BaseUrl, includePlaceholder: false);
        var chartResult = _proxyTrendChartRenderService.Render(chartEntries, result.BaseUrl);

        SetProxyChartSnapshot(
            ProxyChartViewMode.StabilityTrend,
            new ProxyChartDialogSnapshot(
                "稳定性巡检结果图表",
                "这里保留本次巡检的逐轮结果，而不是历史趋势，方便直接看这一轮巡检内部是否波动。",
                $"目标：{result.BaseUrl}\n" +
                $"健康度：{result.HealthScore}/100（{result.HealthLabel}）\n" +
                $"完成轮次：{result.CompletedRounds}/{result.RequestedRounds}\n" +
                $"平均普通延迟：{FormatMilliseconds(result.AverageChatLatency)} / 平均 TTFT：{FormatMilliseconds(result.AverageStreamFirstTokenLatency)}\n" +
                $"摘要：{result.Summary}",
                latestRound is null
                    ? "最近一轮能力矩阵：无"
                    : $"最近一轮能力矩阵（{latestRound.CheckedAt:HH:mm:ss}）\n{BuildDialogCapabilityMatrix(latestRound)}",
                latestRound is null
                    ? "最近一轮能力明细：无"
                    : $"最近一轮能力明细\n{BuildDialogCapabilityDetail(latestRound)}",
                "图里每个点都是一轮巡检结果，适合看这一批轮次内部是否波动。",
                $"稳定性巡检完成：健康度 {result.HealthScore}/100（{result.HealthLabel}）。",
                "当前稳定性巡检图表正在生成。",
                chartResult.ChartImage),
            activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

    private void StartProxyBatchChartLiveSession(IReadOnlyList<ProxyBatchTargetEntry> entries)
    {
        ResetProxyTrendChartAutoOpenSuppression();
        SetProxyChartRetryMode(ProxyChartRetryMode.Batch, "追加整组重试 5 轮");
        var placeholderRows = MaterializeLiveBatchRows(
            new Dictionary<string, ProxyBatchProbeRow>(StringComparer.OrdinalIgnoreCase),
            entries);
        _currentProxyBatchLiveRows = placeholderRows.ToArray();
        _currentProxyBatchLiveTargetCount = entries.Count;
        var completedRuns = _proxyBatchChartRuns.Count;
        var currentRunNumber = completedRuns + 1;
        var aggregateRows = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns, placeholderRows)).ToArray();
        var chartItems = CreateProxyBatchComparisonChartItems(aggregateRows);
        var chartResult = _proxyBatchComparisonChartRenderService.Render(
            chartItems,
            ResolvePreferredBatchChartWidth());

        SetProxyChartSnapshot(
            ProxyChartViewMode.BatchComparison,
            new ProxyChartDialogSnapshot(
                "入口组检测实时图表",
                "弹窗会先把全部入口占位显示出来；同站点组里尚未轮到的入口会标记“等待同组其他入口测试中”。",
                completedRuns == 0
                    ? $"计划测试：{entries.Count} 个 URL\n当前状态：准备开始第 1 轮整组。\n目的：实时比较哪个入口更稳、更快。"
                    : $"历史已完成：{completedRuns} 轮整组\n当前状态：准备开始第 {currentRunNumber} 轮整组\n本轮计划：{entries.Count} 个 URL。",
                BuildProxyBatchCapabilitySummaryText(aggregateRows, completedRuns == 0 ? "预占位摘要" : "历史 + 当前预占位摘要"),
                BuildProxyBatchCapabilityDetailText(aggregateRows, completedRuns == 0 ? "预占位明细" : "历史 + 当前预占位明细"),
                "实时图适合看：当前推荐项有没有切换、平均延迟、独立吞吐和 TTFT 会不会被新一轮结果拉偏、基础能力与长流增强项是否同步变差。",
                $"入口组检测已启动，当前第 {currentRunNumber} 轮整组，共 {entries.Count} 个 URL；尚未轮到的同组入口会先显示等待占位。",
                "正在等待入口组结果返回...",
                chartResult.ChartImage),
            activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

    private void UpdateProxyBatchChartLive(IReadOnlyList<ProxyBatchProbeRow> rows, int totalTargets)
    {
        if (rows.Count == 0)
        {
            return;
        }

        _currentProxyBatchLiveRows = rows.ToArray();
        _currentProxyBatchLiveTargetCount = totalTargets;
        var aggregateRows = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns, rows)).ToArray();
        var chartItems = CreateProxyBatchComparisonChartItems(aggregateRows);
        var chartResult = _proxyBatchComparisonChartRenderService.Render(
            chartItems,
            ResolvePreferredBatchChartWidth());
        var best = aggregateRows[0];
        var completedRuns = _proxyBatchChartRuns.Count;
        var currentRunNumber = completedRuns + 1;
        var visibleTargets = rows.Count(row => !row.IsPlaceholder);
        var completedTargets = CountCompletedLiveBatchRows(rows);

        SetProxyChartSnapshot(
            ProxyChartViewMode.BatchComparison,
            new ProxyChartDialogSnapshot(
                "入口组检测实时图表",
                "弹窗正在把历史轮次和本轮已返回结果一起累计比较；当前推荐项会随着新结果即时变化。",
                $"已完成整组：{completedRuns} 轮；当前第 {currentRunNumber} 轮进行中\n" +
                $"本轮已有阶段结果：{visibleTargets}/{totalTargets}\n" +
                $"整条 URL 已完成：{completedTargets}/{totalTargets}\n" +
                $"当前推荐：{best.Entry.Name}\n" +
                $"推荐地址：{best.Entry.BaseUrl}\n" +
                $"推荐原因：平均普通对话 {FormatMillisecondsValue(best.AverageChatLatencyMs)}，独立吞吐 {FormatTokensPerSecond(best.AverageBenchmarkTokensPerSecond)}，平均 TTFT {FormatMillisecondsValue(best.AverageTtftMs)}，综合分 {best.CompositeScore:F1}，基础/增强拆分见右侧摘要。",
                BuildProxyBatchCapabilitySummaryText(aggregateRows, "多入口实时累计摘要"),
                BuildProxyBatchCapabilityDetailText(aggregateRows, "多入口实时累计明细"),
                "如果排行榜前几名频繁互换，通常说明入口组存在波动，或者基础能力与增强长流在不同轮次表现不一致。",
                $"入口组检测进行中：第 {currentRunNumber} 轮已有 {visibleTargets}/{totalTargets} 个入口出现结果，整条完成 {completedTargets}/{totalTargets}，当前推荐 {best.Entry.Name}。",
                "正在等待入口组累计图表数据...",
                chartResult.ChartImage),
            activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

}
