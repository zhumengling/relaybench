using System.ComponentModel;
using System.Text;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _proxyBatchQuickCompareCompleted;
    private string _batchDeepTestSummary = "先勾选候选项，再开始深测。";

    public bool ProxyBatchQuickCompareCompleted
    {
        get => _proxyBatchQuickCompareCompleted;
        private set
        {
            if (SetProperty(ref _proxyBatchQuickCompareCompleted, value))
            {
                RefreshBatchSelectionState();
            }
        }
    }

    public bool CanRunSelectedBatchDeepTests
        => ProxyBatchQuickCompareCompleted &&
           !IsBusy &&
           GetSelectedBatchRankingRows().Length > 0;

    public string BatchSelectionSummary
    {
        get
        {
            if (!ProxyBatchQuickCompareCompleted || ProxyBatchRankingRows.Count == 0)
            {
                return "先维护入口组，再开始快测。";
            }

            var selectedRows = GetSelectedBatchRankingRows();
            var checkedRows = ProxyBatchRankingRows.Count(row => row.IsSelected);
            if (selectedRows.Length == 0)
            {
                return checkedRows > 0
                    ? $"已勾选 {checkedRows} 项，但当前都不可深测：{GetBatchSelectionBlockingReason() ?? "请重新快测或重选。"}"
                    : $"快测完成，共 {ProxyBatchRankingRows.Count} 项；当前未勾选候选项。";
            }

            var preview = string.Join("、", selectedRows.Take(3).Select(row => $"#{row.Rank} {row.EntryName}"));
            if (selectedRows.Length > 3)
            {
                preview += " 等";
            }

            return checkedRows == selectedRows.Length
                ? $"已勾选 {selectedRows.Length}/{ProxyBatchRankingRows.Count} 项：{preview}"
                : $"已勾选 {checkedRows} 项，其中 {selectedRows.Length} 项可继续深测：{preview}";
        }
    }

    public string BatchDeepTestSummary
    {
        get => _batchDeepTestSummary;
        private set => SetProperty(ref _batchDeepTestSummary, value);
    }

    private bool CanRunSelectedBatchDeepTestsAction()
        => CanRunSelectedBatchDeepTests;

    private void PrepareForProxyBatchQuickCompare()
    {
        ProxyBatchQuickCompareCompleted = false;
        BatchDeepTestSummary = "快测进行中；完成后再勾选候选项。";
        ResetBatchDeepComparisonState();
        RefreshBatchSelectionState();
    }

    private void RefreshProxyBatchRankingRows(IReadOnlyList<ProxyBatchAggregateRow> aggregateRows)
    {
        foreach (var existingRow in ProxyBatchRankingRows)
        {
            existingRow.PropertyChanged -= OnProxyBatchRankingRowPropertyChanged;
        }

        ProxyBatchRankingRows.Clear();

        foreach (var item in aggregateRows.Select((row, index) => new { Row = row, Index = index }))
        {
            var row = new ProxyBatchRankingRowViewModel
            {
                IsSelected = false,
                Rank = item.Index + 1,
                EntryName = item.Row.Entry.Name,
                BaseUrl = item.Row.Entry.BaseUrl,
                Model = string.IsNullOrWhiteSpace(item.Row.Entry.Model) ? "（沿用当前模型）" : item.Row.Entry.Model,
                QuickVerdict = BuildBatchStabilityLabel(item.Row),
                QuickMetrics = BuildBatchRankingQuickMetrics(item.Row),
                CapabilitySummary = BuildBatchRankingCapabilitySummary(item.Row),
                DeepStatus = "未开始",
                DeepSummary = string.Empty,
                DeepCheckedAt = "--",
                ApiKey = item.Row.Entry.ApiKey
            };

            row.PropertyChanged += OnProxyBatchRankingRowPropertyChanged;
            ProxyBatchRankingRows.Add(row);
        }

        BatchDeepTestSummary = aggregateRows.Count == 0
            ? "暂无可深测候选项。"
            : "快测完成，勾选候选项后可深测。";
        ResetBatchDeepComparisonState();

        RefreshBatchSelectionState();
    }

    private async Task RunSelectedBatchDeepTestsAsync()
    {
        var selectedRows = GetSelectedBatchRankingRows();
        if (selectedRows.Length == 0)
        {
            var checkedRows = ProxyBatchRankingRows.Count(row => row.IsSelected);
            StatusMessage = checkedRows > 0
                ? GetBatchSelectionBlockingReason() ?? "当前勾选项里没有可继续深度测试的网址，请先重新快速对比或重新勾选。"
                : "请先在排行榜列表里勾选要继续深度测试的候选站点。";
            return;
        }

        await ExecuteProxyBusyActionAsync(
            $"正在对 {selectedRows.Length} 个候选站点执行深度测试...",
            async cancellationToken =>
        {
            var executionPlan = BuildDeepProxySingleExecutionPlan();
            StartBatchDeepComparisonLiveSession(selectedRows, executionPlan);
            UpdateGlobalTaskProgress("\u51C6\u5907\u4E2D", 8d);

            for (var index = 0; index < selectedRows.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = selectedRows[index];
                PrepareBatchDeepRowForExecution(row, index, selectedRows.Length);
                StatusMessage = $"正在执行候选深度测试 {index + 1}/{selectedRows.Length}：{row.EntryName}";
                UpdateGlobalTaskProgress(index, selectedRows.Length, $"\u7B2C {index + 1}/{selectedRows.Length} \u9879");

                try
                {
                    var progress = new Progress<ProxyDiagnosticsLiveProgress>(value => UpdateBatchDeepChartLive(row, value));
                    var result = await RunSingleProxyDiagnosticsAsync(
                        BuildBatchProxySettings(row),
                        progress,
                        executionPlan,
                        updateSingleChartPhases: false,
                        cancellationToken: cancellationToken);
                    ApplyBatchDeepTestResult(row, result, executionPlan);
                    UpdateGlobalTaskProgress(index + 1, selectedRows.Length, $"\u7B2C {index + 1}/{selectedRows.Length} \u9879");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ApplyBatchDeepChartFailure(row, ex);
                    UpdateGlobalTaskProgress(index + 1, selectedRows.Length, $"\u7B2C {index + 1}/{selectedRows.Length} \u9879");
                }
            }

            BatchDeepTestSummary = BuildBatchDeepTestSummary(_batchDeepChartStates);
            RefreshBatchDeepComparisonDialog(activate: true);
            StatusMessage = $"已完成 {selectedRows.Length} 个候选站点的深度测试。";
            AppendHistory("接口", "入口组候选深度测试", BatchDeepTestSummary);
            AppendModuleOutput("入口组候选深度测试", BatchSelectionSummary, BatchDeepTestSummary);
        },
        "\u6279\u91CF\u6DF1\u5EA6\u6D4B\u8BD5",
        "\u51C6\u5907\u4E2D",
        6d);
    }

    private ProxyEndpointSettings BuildBatchProxySettings(ProxyBatchRankingRowViewModel row)
        => BuildProxySettings(row.BaseUrl, row.ApiKey, row.Model);

    private void ApplyBatchDeepTestResult(
        ProxyBatchRankingRowViewModel row,
        ProxyDiagnosticsResult result,
        ProxySingleExecutionPlan executionPlan)
        => ApplyBatchDeepChartResult(row, result, executionPlan);

    private string BuildBatchDeepResultSummary(ProxyDiagnosticsResult result, ProxySingleExecutionPlan executionPlan)
    {
        var scenarios = GetScenarioResults(result);
        List<string> parts =
        [
            $"结论 {result.Verdict ?? "待复核"}",
            $"摘要 {result.Summary}"
        ];

        if (!string.IsNullOrWhiteSpace(result.PrimaryIssue))
        {
            parts.Add($"主要问题 {result.PrimaryIssue}");
        }

        if (executionPlan.EnableProtocolCompatibilityTest)
        {
            var systemPrompt = FindScenario(scenarios, ProxyProbeScenarioKind.SystemPromptMapping);
            var functionCalling = FindScenario(scenarios, ProxyProbeScenarioKind.FunctionCalling);
            parts.Add($"协议兼容 System Prompt {FormatScenarioStatus(systemPrompt)} / Function Calling {FormatScenarioStatus(functionCalling)}");
        }

        if (executionPlan.EnableErrorTransparencyTest)
        {
            parts.Add($"错误透传 {FormatScenarioStatus(FindScenario(scenarios, ProxyProbeScenarioKind.ErrorTransparency))}");
        }

        if (executionPlan.EnableStreamingIntegrityTest)
        {
            parts.Add($"流式完整性 {FormatScenarioStatus(FindScenario(scenarios, ProxyProbeScenarioKind.StreamingIntegrity))}");
        }

        if (executionPlan.EnableOfficialReferenceIntegrityTest)
        {
            parts.Add($"官方对照 {FormatScenarioStatus(FindScenario(scenarios, ProxyProbeScenarioKind.OfficialReferenceIntegrity))}");
        }

        if (executionPlan.EnableMultiModalTest)
        {
            parts.Add($"多模态 {FormatScenarioStatus(FindScenario(scenarios, ProxyProbeScenarioKind.MultiModal))}");
        }

        if (executionPlan.EnableCacheMechanismTest)
        {
            parts.Add($"缓存命中 {FormatScenarioStatus(FindScenario(scenarios, ProxyProbeScenarioKind.CacheMechanism))}");
        }

        if (executionPlan.EnableCacheIsolationTest)
        {
            parts.Add($"缓存隔离 {FormatScenarioStatus(FindScenario(scenarios, ProxyProbeScenarioKind.CacheIsolation))}");
        }

        return string.Join("；", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private string BuildBatchDeepTestSummary(IReadOnlyList<ProxyBatchRankingRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return "完成快速对比后，请在排行榜列表中勾选候选项，再发起深度测试。";
        }

        StringBuilder builder = new();
        builder.AppendLine($"已完成 {rows.Count} 个候选站点的深度测试（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）。");

        foreach (var row in rows.OrderBy(item => item.Rank))
        {
            builder.AppendLine($"#{row.Rank} {row.EntryName}：{row.DeepStatus}");
            builder.AppendLine(row.DeepSummary);
        }

        return builder.ToString().TrimEnd();
    }

    private ProxyBatchRankingRowViewModel[] GetSelectedBatchRankingRows()
    {
        var runnableKeys = GetCurrentRunnableBatchTargetKeys();
        return ProxyBatchRankingRows
            .Where(row => row.IsSelected && runnableKeys.Contains(BuildBatchRankingRowKey(row)))
            .OrderBy(row => row.Rank)
            .ToArray();
    }

    private string? GetBatchSelectionBlockingReason()
    {
        try
        {
            var plan = BuildProxyBatchPlan(requireRunnable: false);
            return plan.SourceEntries.Count > 0 && plan.Targets.Count == 0
                ? "当前入口组里的所有网址都已关闭“加入测试”。"
                : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private HashSet<string> GetCurrentRunnableBatchTargetKeys()
    {
        try
        {
            return BuildProxyBatchPlan(requireRunnable: false)
                .Targets
                .Select(BuildBatchTargetKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    private static string BuildBatchRankingRowKey(ProxyBatchRankingRowViewModel row)
        => string.Join("|", row.EntryName, row.BaseUrl, row.Model, row.ApiKey);

    private static string BuildBatchTargetKey(ProxyBatchTargetEntry entry)
        => string.Join("|", entry.Name, entry.BaseUrl, entry.Model, entry.ApiKey);

    private void OnProxyBatchRankingRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ProxyBatchRankingRowViewModel.IsSelected), StringComparison.Ordinal))
        {
            RefreshBatchSelectionState();
        }
    }

    private void RefreshBatchSelectionState()
    {
        OnPropertyChanged(nameof(BatchSelectionSummary));
        OnPropertyChanged(nameof(CanRunSelectedBatchDeepTests));
        RunSelectedBatchDeepTestsCommand?.RaiseCanExecuteChanged();
    }

    private static string BuildBatchRankingQuickMetrics(ProxyBatchAggregateRow row)
        => $"平均普通 {FormatMillisecondsValue(row.AverageChatLatencyMs)} | 平均 TTFT {FormatMillisecondsValue(row.AverageTtftMs)} | 独立吞吐（3 轮中位数） {FormatTokensPerSecond(row.AverageBenchmarkTokensPerSecond)}";

    private static string BuildBatchRankingCapabilitySummary(ProxyBatchAggregateRow row)
        => $"{BuildBatchStabilityLabel(row)} | 综合分 {row.CompositeScore:F1} | 能力均值 {FormatBatchDisplayedCapabilityAverage(row)} | {BuildBatchCapabilityBreakdown(row, includeDeepHint: false)}";
}
