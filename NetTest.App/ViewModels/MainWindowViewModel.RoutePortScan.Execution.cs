using NetTest.App.Infrastructure;
using NetTest.App.Services;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task RunRouteCoreAsync()
    {
        var routePlan = BuildRouteRunPlan();
        var outcome = await ExecuteRouteRoundAsync(routePlan);
        UpdateGlobalTaskProgress("\u6536\u5C3E\u4E2D", 96d);

        DashboardCards[5].Status = outcome.Result.Error is null ? "完成" : "需复核";
        DashboardCards[5].Detail = outcome.MapResult.HasMap ? outcome.MapResult.Summary : outcome.Result.Summary;
        StatusMessage = outcome.MapResult.HasMap ? outcome.MapResult.Summary : outcome.Result.Summary;
        AppendHistory("路由", "路由 / MTR 检测", RouteSummary);
    }

    private async Task RunRouteContinuousCoreAsync()
    {
        var routePlan = BuildRouteRunPlan();
        var durationSeconds = ParseBoundedInt(RouteContinuousDurationSecondsText, fallback: 60, min: 10, max: 3_600);
        var intervalMilliseconds = ParseBoundedInt(RouteContinuousIntervalMsText, fallback: 500, min: 0, max: 60_000);
        var startedAt = DateTimeOffset.Now;
        var endsAt = startedAt.AddSeconds(durationSeconds);
        RouteRunRoundOutcome? lastOutcome = null;
        var completedRounds = 0;
        _routeContinuousCancellationSource = new CancellationTokenSource();
        BeginContinuousRouteAggregation();
        RefreshRouteContinuousCommandStates();

        try
        {
            while (DateTimeOffset.Now < endsAt && !_routeContinuousCancellationSource.IsCancellationRequested)
            {
                completedRounds++;
                var remainingSeconds = Math.Max(0, (int)Math.Ceiling((endsAt - DateTimeOffset.Now).TotalSeconds));
                StatusMessage = $"持续运行第 {completedRounds} 轮，剩余约 {remainingSeconds} 秒...";
                UpdateGlobalTaskProgressForContinuousRouteWindow(startedAt, endsAt, $"\u7B2C {completedRounds} \u8F6E");

                lastOutcome = await ExecuteRouteRoundAsync(
                    routePlan,
                    isContinuousMode: true,
                    currentRound: completedRounds,
                    plannedDurationSeconds: durationSeconds,
                    continuousEndsAt: endsAt,
                    continuousIntervalMilliseconds: intervalMilliseconds,
                    cancellationToken: _routeContinuousCancellationSource.Token);

                DashboardCards[5].Status = "持续运行中";
                DashboardCards[5].Detail = $"已完成 {completedRounds} 轮，剩余约 {Math.Max(0, (int)Math.Ceiling((endsAt - DateTimeOffset.Now).TotalSeconds))} 秒";
                UpdateGlobalTaskProgressForContinuousRouteWindow(startedAt, endsAt, $"\u7B2C {completedRounds} \u8F6E");

                if (DateTimeOffset.Now >= endsAt || _routeContinuousCancellationSource.IsCancellationRequested)
                {
                    break;
                }

                StatusMessage = $"第 {completedRounds} 轮已完成，等待 {intervalMilliseconds} ms 后开始下一轮...";
                DashboardCards[5].Detail = $"已完成 {completedRounds} 轮，{intervalMilliseconds} ms 后开始下一轮";
                UpdateGlobalTaskProgressForContinuousRouteWindow(startedAt, endsAt, "\u7B49\u5F85\u4E2D");
                await Task.Delay(intervalMilliseconds, _routeContinuousCancellationSource.Token);
            }

            var continuousSummary = $"持续运行：共运行 {durationSeconds} 秒，轮间隔 {intervalMilliseconds} ms，完成 {completedRounds} 轮。";
            if (lastOutcome is null)
            {
                RouteSummary = continuousSummary + "\n未生成路由结果。";
                DashboardCards[5].Status = "需复核";
                DashboardCards[5].Detail = RouteSummary;
                StatusMessage = RouteSummary;
                AppendHistory("路由", "路由 / MTR 持续运行", RouteSummary);
                return;
            }

            UpdateGlobalTaskProgress("\u6536\u5C3E\u4E2D", 96d);
            RouteSummary = continuousSummary + "\n" + RouteSummary;
            RouteMapSummary = continuousSummary + "\n" + RouteMapSummary;
            DashboardCards[5].Status = lastOutcome.Result.Error is null ? "完成" : "需复核";
            if (_routeContinuousCancellationSource.IsCancellationRequested)
            {
                DashboardCards[5].Detail = $"{continuousSummary} 已手动停止，最后一轮：{lastOutcome.Result.Summary}";
                StatusMessage = $"{continuousSummary} 已手动停止。";
            }
            else
            {
                DashboardCards[5].Detail = $"{continuousSummary} 最后一轮：{lastOutcome.Result.Summary}";
                StatusMessage = $"{continuousSummary} 最后一轮已结束。";
            }
            AppendHistory("路由", "路由 / MTR 持续运行", RouteSummary);
        }
        catch (OperationCanceledException) when (_routeContinuousCancellationSource?.IsCancellationRequested == true)
        {
            var cancelSummary = $"持续运行已停止：轮间隔 {intervalMilliseconds} ms，共完成 {completedRounds} 轮。";
            RouteSummary = cancelSummary + "\n" + RouteSummary;
            RouteMapSummary = cancelSummary + "\n" + RouteMapSummary;
            DashboardCards[5].Status = "已停止";
            DashboardCards[5].Detail = cancelSummary;
            StatusMessage = cancelSummary;
            AppendHistory("路由", "路由 / MTR 持续运行", RouteSummary);
        }
        finally
        {
            ResetRouteContinuousLiveState();
        }
    }

    private async Task DetectPortScanEngineCoreAsync()
    {
        UpdateGlobalTaskProgress("\u68C0\u6D4B\u4E2D", 68d);
        var result = await _portScanDiagnosticsService.DetectAsync();
        UpdateGlobalTaskProgress("\u6574\u7406\u4E2D", 92d);
        ApplyPortScanResult(result);
        DashboardCards[6].Status = result.IsEngineAvailable ? "就绪" : "缺失";
        DashboardCards[6].Detail = result.Summary;
        StatusMessage = result.Summary;
        AppendHistory("端口扫描", "端口扫描引擎检测", PortScanSummary);
    }

    private async Task RunPortScanCoreAsync()
    {
        BeginPortScanLiveExecution(PortScanTarget);
        var progress = new Progress<PortScanProgressUpdate>(HandlePortScanProgressUpdate);
        var result = await _portScanDiagnosticsService.RunAsync(
            PortScanTarget,
            SelectedPortScanProfileKey,
            PortScanCustomPortsText,
            progress);

        UpdateGlobalTaskProgress("\u6536\u5C3E\u4E2D", 96d);
        ApplyPortScanResult(result);
        DashboardCards[6].Status = result.ScanSucceeded ? "完成" : result.IsEngineAvailable ? "需复核" : "缺失";
        DashboardCards[6].Detail = result.Summary;
        StatusMessage = result.Summary;
        AppendHistory("端口扫描", "本地端口扫描", PortScanSummary);
    }

    private async Task RunPortScanBatchCoreAsync()
    {
        var targets = GetPendingPortScanBatchTargets();
        if (targets.Count == 0)
        {
            PortScanBatchSummary = "请先在“批量目标”框中输入目标，每行一个。";
            StatusMessage = PortScanBatchSummary;
            DashboardCards[6].Status = "待运行";
            DashboardCards[6].Detail = PortScanBatchSummary;
            return;
        }

        PortScanBatchRows.Clear();
        _lastPortScanBatchResults.Clear();
        _portScanBatchResultLookup.Clear();
        _portScanBatchManualSelectionActive = false;
        SetSelectedPortScanBatchRowFromCode(null);
        RefreshPortScanBatchSummary();
        RefreshPortScanExportSummary();
        RefreshPortScanExportCommands();

        var maxParallelism = GetPortScanBatchConcurrency();
        DashboardCards[6].Status = "批量扫描中";
        DashboardCards[6].Detail = $"准备扫描 {targets.Count} 个目标，批量并发 {maxParallelism}";
        UpdateGlobalTaskProgress("\u51C6\u5907\u4E2D", 10d);

        List<PortScanBatchRowViewModel> rows = [];
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            PortScanBatchRowViewModel row = new()
            {
                Target = target,
                Status = "待运行",
                Summary = $"队列 {index + 1}/{targets.Count}"
            };

            rows.Add(row);
            PortScanBatchRows.Add(row);
        }

        RefreshPortScanBatchSummary();
        if (rows.Count > 0)
        {
            SetSelectedPortScanBatchRowFromCode(rows[0]);
        }

        using SemaphoreSlim semaphore = new(maxParallelism);
        List<Task<PortScanBatchTaskOutcome>> runningTasks = [];
        for (var index = 0; index < rows.Count; index++)
        {
            runningTasks.Add(RunPortScanBatchTargetAsync(rows[index], index + 1, rows.Count, semaphore));
        }

        var completedCount = 0;
        while (runningTasks.Count > 0)
        {
            var finishedTask = await Task.WhenAny(runningTasks);
            runningTasks.Remove(finishedTask);
            var outcome = await finishedTask;
            completedCount++;

            if (outcome.Error is not null)
            {
                outcome.Row.Status = "异常";
                outcome.Row.Summary = "运行时异常";
                outcome.Row.Error = outcome.Error.Message;
                outcome.Row.CheckedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
                AppendPortScanLog(DateTimeOffset.Now, $"批量目标 {outcome.Row.Target} 扫描异常：{outcome.Error.Message}");
            }
            else if (outcome.Result is not null)
            {
                _lastPortScanBatchResults.Add(outcome.Result);
                _portScanBatchResultLookup[outcome.Result.Target] = outcome.Result;
                ApplyPortScanBatchRow(outcome.Row, outcome.Result);

                if (!_portScanBatchManualSelectionActive || ReferenceEquals(SelectedPortScanBatchRow, outcome.Row))
                {
                    SetSelectedPortScanBatchRowFromCode(outcome.Row);
                }
                else
                {
                    RefreshPortScanBatchSummary();
                    RefreshPortScanExportSummary();
                    RefreshPortScanExportCommands();
                }
            }

            DashboardCards[6].Detail = $"批量进度 {completedCount}/{targets.Count}：{outcome.Row.Target}";
            UpdateGlobalTaskProgressForBatchPortScan(completedCount, targets.Count);
            RefreshPortScanBatchSummary();
            RefreshPortScanExportSummary();
            RefreshPortScanExportCommands();
        }

        var failureCount = PortScanBatchRows.Count(row => row.Status is "失败" or "需复核" or "异常");
        var summary = $"批量端口扫描完成：{PortScanBatchRows.Count} 个目标，异常/复核 {failureCount} 个，开放端点 {PortScanBatchRows.Sum(row => row.OpenEndpointCount)} 个。";
        UpdateGlobalTaskProgress("\u6536\u5C3E\u4E2D", 96d);
        DashboardCards[6].Status = failureCount == 0 ? "完成" : "需复核";
        DashboardCards[6].Detail = summary;
        StatusMessage = summary;
        RefreshPortScanBatchSummary();
        RefreshPortScanExportSummary();
        RefreshPortScanExportCommands();
        AppendHistory("端口扫描", "批量端口扫描", PortScanBatchSummary);
    }

    private async Task<PortScanBatchTaskOutcome> RunPortScanBatchTargetAsync(
        PortScanBatchRowViewModel row,
        int sequence,
        int total,
        SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            row.Status = "扫描中";
            row.Summary = $"队列 {sequence}/{total}，正在扫描";
            row.Error = string.Empty;
            AppendPortScanLog(DateTimeOffset.Now, $"批量目标开始：{row.Target}（{sequence}/{total}）");

            var target = row.Target;
            var profileKey = SelectedPortScanProfileKey;
            var customPortsText = PortScanCustomPortsText;

            var result = await Task.Run(() => _portScanDiagnosticsService.RunAsync(
                target,
                profileKey,
                customPortsText,
                progress: null));

            AppendPortScanLog(result.CheckedAt, $"批量目标完成：{target}，开放端点 {result.OpenEndpointCount} 个");
            return new PortScanBatchTaskOutcome(row, result, null);
        }
        catch (Exception ex)
        {
            return new PortScanBatchTaskOutcome(row, null, ex);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private Task ExportPortScanCsvCoreAsync()
    {
        if (!CanExportPortScanResults())
        {
            PortScanExportSummary = "暂无可导出结果。";
            StatusMessage = PortScanExportSummary;
            return Task.CompletedTask;
        }

        var snapshot = BuildPortScanExportSnapshot();
        var filePath = _portScanExportService.ExportCsv(snapshot);
        PortScanExportSummary = $"CSV 已导出：{filePath}";
        DashboardCards[6].Detail = PortScanExportSummary;
        StatusMessage = PortScanExportSummary;
        SaveState();
        return Task.CompletedTask;
    }

    private Task ExportPortScanExcelCoreAsync()
    {
        if (!CanExportPortScanResults())
        {
            PortScanExportSummary = "暂无可导出结果。";
            StatusMessage = PortScanExportSummary;
            return Task.CompletedTask;
        }

        var snapshot = BuildPortScanExportSnapshot();
        var filePath = _portScanExportService.ExportExcel(snapshot);
        PortScanExportSummary = $"Excel 已导出：{filePath}";
        DashboardCards[6].Detail = PortScanExportSummary;
        StatusMessage = PortScanExportSummary;
        SaveState();
        return Task.CompletedTask;
    }

    private static void ApplyPortScanBatchRow(PortScanBatchRowViewModel row, PortScanResult result)
    {
        row.Status = result.ScanSucceeded ? "完成" : result.Error is null ? "需复核" : "失败";
        row.OpenEndpointCount = result.OpenEndpointCount;
        row.OpenPortCount = result.OpenPortCount;
        row.ResolvedAddresses = result.ResolvedAddresses.Count == 0 ? "--" : string.Join(", ", result.ResolvedAddresses);
        row.Summary = result.Summary;
        row.Error = result.Error ?? string.Empty;
        row.CheckedAt = result.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void LoadExtendedState(AppStateSnapshot snapshot)
    {
        _selectedStunTransportKey = StunServerPresetCatalog.ResolveTransportKey(snapshot.StunTransportKey, snapshot.StunServer);
        OnPropertyChanged(nameof(SelectedStunTransportKey));
        StunServer = NormalizeStunServerHost(snapshot.StunServer);
        RefreshStunServerOptions(syncCurrentHost: true);
        RouteTarget = string.IsNullOrWhiteSpace(snapshot.RouteTarget) ? "chatgpt.com" : snapshot.RouteTarget;
        SelectedRouteResolverKey = ResolveRouteResolverKey(snapshot.RouteResolverKey);
        RouteMaxHopsText = string.IsNullOrWhiteSpace(snapshot.RouteMaxHopsText) || string.Equals(snapshot.RouteMaxHopsText.Trim(), "12", StringComparison.Ordinal)
            ? "20"
            : snapshot.RouteMaxHopsText;
        RouteTimeoutMsText = string.IsNullOrWhiteSpace(snapshot.RouteTimeoutMsText) ? "900" : snapshot.RouteTimeoutMsText;
        RouteSamplesPerHopText = string.IsNullOrWhiteSpace(snapshot.RouteSamplesPerHopText) ? "3" : snapshot.RouteSamplesPerHopText;
        RouteContinuousDurationSecondsText = string.IsNullOrWhiteSpace(snapshot.RouteContinuousDurationSecondsText) ? "60" : snapshot.RouteContinuousDurationSecondsText;
        RouteContinuousIntervalMsText = string.IsNullOrWhiteSpace(snapshot.RouteContinuousIntervalMsText) ? "500" : snapshot.RouteContinuousIntervalMsText;
        PortScanTarget = snapshot.PortScanTarget ?? string.Empty;
        SelectedPortScanProfileKey = ResolvePortScanProfileKey(snapshot.PortScanProfileKey);
        PortScanCustomPortsText = snapshot.PortScanCustomPortsText ?? string.Empty;
        PortScanBatchTargetsText = snapshot.PortScanBatchTargetsText ?? string.Empty;
        PortScanBatchConcurrencyText = string.IsNullOrWhiteSpace(snapshot.PortScanBatchConcurrencyText) ? "3" : snapshot.PortScanBatchConcurrencyText;
        RefreshPortScanBatchSummary();
        RefreshPortScanExportSummary();
    }

    private void ApplyExtendedStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.StunTransportKey = SelectedStunTransportKey;
        snapshot.StunServer = StunServer;
        snapshot.RouteTarget = RouteTarget;
        snapshot.RouteResolverKey = SelectedRouteResolverKey;
        snapshot.RouteMaxHopsText = RouteMaxHopsText;
        snapshot.RouteTimeoutMsText = RouteTimeoutMsText;
        snapshot.RouteSamplesPerHopText = RouteSamplesPerHopText;
        snapshot.RouteContinuousDurationSecondsText = RouteContinuousDurationSecondsText;
        snapshot.RouteContinuousIntervalMsText = RouteContinuousIntervalMsText;
        snapshot.PortScanTarget = PortScanTarget;
        snapshot.PortScanProfileKey = SelectedPortScanProfileKey;
        snapshot.PortScanCustomPortsText = PortScanCustomPortsText;
        snapshot.PortScanBatchTargetsText = PortScanBatchTargetsText;
        snapshot.PortScanBatchConcurrencyText = PortScanBatchConcurrencyText;
    }

    private sealed record PortScanBatchTaskOutcome(
        PortScanBatchRowViewModel Row,
        PortScanResult? Result,
        Exception? Error);

    private RouteRunPlan BuildRouteRunPlan()
        => new(
            ParseBoundedInt(RouteMaxHopsText, fallback: 20, min: 1, max: 30),
            ParseBoundedInt(RouteTimeoutMsText, fallback: 900, min: 250, max: 5_000),
            ParseBoundedInt(RouteSamplesPerHopText, fallback: 3, min: 1, max: 10));

    private async Task<RouteRunRoundOutcome> ExecuteRouteRoundAsync(
        RouteRunPlan routePlan,
        bool isContinuousMode = false,
        int currentRound = 1,
        int plannedDurationSeconds = 0,
        DateTimeOffset? continuousEndsAt = null,
        int continuousIntervalMilliseconds = 500,
        CancellationToken cancellationToken = default)
    {
        PrepareRouteLiveExecution(isContinuousMode, currentRound, plannedDurationSeconds, continuousEndsAt);
        var progress = new Progress<string>(HandleRouteProgressMessage);

        try
        {
            var result = await _routeDiagnosticsService.RunAsync(
                RouteTarget,
                routePlan.MaxHops,
                routePlan.TimeoutMilliseconds,
                routePlan.SamplesPerHop,
                "icmp",
                SelectedRouteResolverKey,
                null,
                progress,
                cancellationToken);

            _isRouteLiveExecutionActive = false;
            if (!isContinuousMode)
            {
                ApplyRouteResult(result);
            }

            UpdateGlobalTaskProgress("\u7ED8\u56FE\u4E2D", 88d);
            var mapResult = await _routeMapRenderService.RenderAsync(result, progress, cancellationToken);
            UpdateGlobalTaskProgress("\u6536\u5C3E\u4E2D", 94d);
            if (isContinuousMode)
            {
                DisplayRouteMapResult(mapResult, persistState: false, appendModuleOutput: false);
            }
            else
            {
                ApplyRouteMapResult(mapResult);
            }

            if (isContinuousMode)
            {
                MarkContinuousRoundCompleted(result, mapResult, continuousIntervalMilliseconds);
            }

            return new RouteRunRoundOutcome(result, mapResult);
        }
        finally
        {
            _isRouteLiveExecutionActive = false;
            if (!isContinuousMode)
            {
                ResetRouteContinuousLiveState();
            }
        }
    }

    private sealed record RouteRunPlan(int MaxHops, int TimeoutMilliseconds, int SamplesPerHop);

    private sealed record RouteRunRoundOutcome(RouteDiagnosticsResult Result, RouteMapRenderResult MapResult);
}
