using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

internal enum ProxyChartRetryMode
{
    None,
    Single,
    Stability,
    Batch
}

public sealed partial class MainWindowViewModel
{
    private const int StabilityRetryAppendRounds = 5;
    private const int BatchRetryAppendRounds = 5;

    private bool CanRetryProxyChart()
        => !IsBusy && HasProxyChartRetryAction;

    private void SetProxyChartRetryMode(ProxyChartRetryMode mode, string buttonText)
    {
        var modeChanged = _proxyChartRetryMode != mode;
        _proxyChartRetryMode = mode;

        if (ProxyChartRetryButtonText != buttonText)
        {
            ProxyChartRetryButtonText = buttonText;
        }

        if (modeChanged)
        {
            OnPropertyChanged(nameof(HasProxyChartRetryAction));
        }

        RetryProxyChartCommand.RaiseCanExecuteChanged();
    }

    private async Task RetryProxyChartAsync()
    {
        switch (_proxyChartRetryMode)
        {
            case ProxyChartRetryMode.Single:
                await RetrySingleProxyChartAsync();
                break;
            case ProxyChartRetryMode.Stability:
                await RetryProxyStabilityChartAsync();
                break;
            case ProxyChartRetryMode.Batch:
                await RetryProxyBatchChartAsync();
                break;
            default:
                StatusMessage = "当前图表不支持重试。";
                break;
        }
    }

    private async Task RetrySingleProxyChartAsync()
    {
        await ExecuteProxyBusyActionAsync($"正在重试{GetSingleProxyExecutionDisplayName()}...", async cancellationToken =>
        {
            var executionPlan = BuildProxySingleExecutionPlan(_lastProxySingleExecutionMode);
            _currentProxySingleExecutionPlan = executionPlan;
            StartSingleProxyChartLiveSession();
            var progress = new Progress<ProxyDiagnosticsLiveProgress>(UpdateSingleProxyChartLive);
            var result = await RunSingleProxyDiagnosticsAsync(progress, executionPlan, cancellationToken: cancellationToken);
            _proxySingleChartRuns.Add(result);
            ApplyProxyResult(result);
            ShowFinalSingleProxyChart(result);

            DashboardCards[3].Status = result.Verdict ?? "待复核";
            DashboardCards[3].Detail = $"已累计 {_proxySingleChartRuns.Count} 次；最新 {result.PrimaryIssue ?? result.Summary}";
            StatusMessage = $"{GetSingleProxyExecutionDisplayName()}重试完成，已累计 {_proxySingleChartRuns.Count} 次结果。";
            AppendHistory(
                "中转站",
                _lastProxySingleExecutionMode == ProxySingleExecutionMode.Deep ? "深度单次诊断重试" : "基础单次诊断重试",
                ProxySummary);
        });
    }

    private async Task RetryProxyStabilityChartAsync()
    {
        await ExecuteProxyBusyActionAsync("正在追加稳定性重试 5 轮...", async cancellationToken =>
        {
            _proxyChartRequestedRounds = Math.Max(_proxyChartRequestedRounds, _proxyStabilityChartRounds.Count) + StabilityRetryAppendRounds;
            ResetProxyTrendChartAutoOpenSuppression();
            AutoOpenProxyTrendChartIfAllowed();

            for (var retryIndex = 0; retryIndex < StabilityRetryAppendRounds; retryIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentRoundNumber = _proxyStabilityChartRounds.Count + 1;
                ProxyChartDialogStatusSummary = $"正在追加稳定性重试 {retryIndex + 1}/{StabilityRetryAppendRounds}（累计第 {currentRoundNumber}/{_proxyChartRequestedRounds} 轮）...";
                ProxyChartDialogEmptyStateText = "正在执行追加稳定性重试...";

                var liveProgress = new Progress<ProxyDiagnosticsLiveProgress>(progress =>
                {
                    ProxyChartDialogCapabilitySummary = BuildLiveCapabilityMatrix(
                        progress.ScenarioResults,
                        progress.CompletedScenarioCount,
                        progress.TotalScenarioCount);
                    ProxyChartDialogCapabilityDetail = BuildLiveCapabilityDetail(
                        progress.ScenarioResults,
                        progress.CompletedScenarioCount,
                        progress.TotalScenarioCount,
                        progress.ModelCount,
                        progress.SampleModels);
                    ProxyChartDialogStatusSummary =
                        $"追加稳定性重试 {retryIndex + 1}/{StabilityRetryAppendRounds}：第 {currentRoundNumber}/{_proxyChartRequestedRounds} 轮 - {progress.CurrentScenarioResult.DisplayName} / {progress.CurrentScenarioResult.CapabilityStatus}";
                });

                var roundResult = await _proxyDiagnosticsService.RunAsync(BuildProxySettings(), liveProgress, cancellationToken);
                _proxyStabilityChartRounds.Add(roundResult);
                UpdateProxySeriesChartLive(_proxyStabilityChartRounds, _proxyChartRequestedRounds, _proxyChartDelayMilliseconds);
            }

            var stabilityResult = _proxyDiagnosticsService.BuildStabilitySnapshot(
                BuildProxySettings(),
                _proxyChartRequestedRounds,
                _proxyChartDelayMilliseconds,
                _proxyStabilityChartRounds);

            ApplyProxyStabilityResult(stabilityResult);
            ShowFinalProxySeriesChart(stabilityResult);

            DashboardCards[3].Status = stabilityResult.HealthLabel;
            DashboardCards[3].Detail = $"已累计 {stabilityResult.CompletedRounds} 轮，健康度 {stabilityResult.HealthScore}/100。";
            StatusMessage = $"已追加 5 轮稳定性重试，当前累计 {stabilityResult.CompletedRounds} 轮，健康度 {stabilityResult.HealthScore}/100。";
            AppendHistory("中转站", "稳定性巡检追加重试", ProxyStabilitySummary);
        });
    }

    private async Task RetryProxyBatchChartAsync()
    {
        if (_lastProxyBatchPlan is null || _lastProxyBatchPlan.Targets.Count == 0)
        {
            StatusMessage = "当前没有可追加重试的入口组，请先运行一次入口组检测。";
            return;
        }

        await ExecuteProxyBusyActionAsync("正在追加入口组整组重试 5 轮...", async cancellationToken =>
        {
            var plan = _lastProxyBatchPlan;
            var timeoutSeconds = ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 120);
            var enableLongStreamingTest = ProxyBatchEnableLongStreamingTest;
            var longStreamSegmentCount = GetProxyLongStreamSegmentCount();
            ResetProxyTrendChartAutoOpenSuppression();
            AutoOpenProxyTrendChartIfAllowed();

            for (var retryIndex = 0; retryIndex < BatchRetryAppendRounds; retryIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<ProxyBatchProbeRow> liveRows = [];
                var currentRunNumber = _proxyBatchChartRuns.Count + 1;
                ProxyChartDialogStatusSummary = $"正在追加整组重试 {retryIndex + 1}/{BatchRetryAppendRounds}（累计第 {currentRunNumber} 轮整组）...";
                ProxyChartDialogEmptyStateText = "正在执行入口组追加重试...";

                var progress = new Progress<string>(message =>
                {
                    StatusMessage = $"入口组追加重试 {retryIndex + 1}/{BatchRetryAppendRounds}：{message}";
                    ProxyChartDialogStatusSummary = $"入口组追加重试 {retryIndex + 1}/{BatchRetryAppendRounds}：{message}";
                });
                var rowProgress = new Progress<ProxyBatchProbeRow>(row =>
                {
                    liveRows.Add(row);
                    UpdateProxyBatchChartLive(liveRows, plan.Targets.Count);
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
            }

            var aggregateRows = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns)).ToArray();
            var best = aggregateRows[0];

        DashboardCards[3].Status = BuildProxyBatchCardStatus(aggregateRows);
        DashboardCards[3].Detail =
            $"入口组累计 {_proxyBatchChartRuns.Count} 轮，推荐 {best.Entry.Name}（平均普通延迟 {FormatMillisecondsValue(best.AverageChatLatencyMs)} / 平均 TTFT {FormatMillisecondsValue(best.AverageTtftMs)} / 独立吞吐 {FormatTokensPerSecond(best.AverageBenchmarkTokensPerSecond)} / 综合分 {best.CompositeScore:F1}）。";
            StatusMessage = $"入口组已追加 5 轮整组重试，当前累计 {_proxyBatchChartRuns.Count} 轮，推荐 {best.Entry.Name}。";
            AppendHistory("中转站", "中转站入口组追加重试", ProxyBatchSummary);
        });
    }
}
