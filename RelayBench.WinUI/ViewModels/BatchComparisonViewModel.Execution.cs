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
    [RelayCommand]
    private async Task StartBatchAsync()
    {
        var runMode = SelectedRunMode;
        // Determine which sites to run: use site editor entries where IsIncluded == true
        var rawIncludedCount = SiteEditor.Sites.Count(static site => site.IsIncluded);
        var includedSites = BuildResolvedIncludedSites(requireCredentials: true, out var planError).ToList();
        if (!string.IsNullOrWhiteSpace(planError))
        {
            StatusText = planError;
            return;
        }

        // Fallback: if no site editor entries, use the legacy single-site fields
        if (includedSites.Count == 0)
        {
            if (rawIncludedCount > 0)
            {
                StatusText = "入口组里没有可运行的有效接口地址";
                return;
            }

            if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey))
            {
                StatusText = "请在编辑器添加Site，或填写接口地址和 API 密钥";
                return;
            }
            includedSites.Add(new BatchSiteEntry(BaseUrl.Trim(), ApiKey.Trim(), Model.Trim()));
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TotalSites = includedSites.Count;
        CompletedSites = 0;
        Progress = 0;
        ActiveConnections = 0;
        IsRateLimited = false;
        TopSiteScore = 0;
        TopSiteName = "0";
        TopLatencyDisplay = "0 ms";
        TopSuccessRateDisplay = "0.0%";
        TopThroughputDisplay = "0 tok/s";
        StatusText = runMode == BatchRunMode.Deep
            ? "正在运行批量深度评测..."
            : "正在运行批量快速评测...";
        ResetRealtimeBatchVisuals();

        var plannedSteps = GetPlannedStepCount(runMode);
        DeepTestQueue.Clear();
        RunTimeline.Clear();
        _lastTimelineKey = null;
        AddTimelineItem("批量评测", SelectedRunModeText, QueuePlanText, "Info");
        foreach (var site in includedSites)
        {
            var queueItem = new DeepTestQueueItem(site.DisplayName, DeepTestStatus.Pending, 0, 0)
            {
                CurrentStage = "排队中",
                RoundText = $"0/{plannedSteps}",
                LatestResult = "等待开始",
                ScoreText = "0",
                ProgressValue = 0,
                ProgressMaximum = plannedSteps,
            };
            InitializeTestBadges(queueItem, runMode);
            DeepTestQueue.Add(queueItem);
        }

        var siteResults = new List<BatchSiteRunSnapshot>();

        try
        {
            for (int i = 0; i < includedSites.Count && !_cts.Token.IsCancellationRequested; i++)
            {
                var site = includedSites[i];
                var settings = new ProxyEndpointSettings(
                    site.BaseUrl.Trim(), site.ApiKey.Trim(), site.Model.Trim(),
                    IgnoreTlsErrors: site.TlsIgnore, TimeoutSeconds: site.Timeout);

                // Mark current item as Active
                DeepTestQueue[i].Status = DeepTestStatus.Active;
                BatchSiteRunResult result;
                try
                {
                    result = runMode == BatchRunMode.Deep
                        ? await RunDeepSiteAsync(site, settings, DeepTestQueue[i], i, _cts.Token)
                        : await RunQuickSiteAsync(site, settings, DeepTestQueue[i], i, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = BuildFailedSiteResult(site.DisplayName);
                    CompleteFailedQueueItem(DeepTestQueue[i], ex.Message);
                }

                CompletedSites = i + 1;
                Progress = (double)CompletedSites / TotalSites * 100;
                UpdateEstimatedRemaining(sw.Elapsed, CompletedSites, TotalSites);

                siteResults.Add(ToRunSnapshot(result, site, DeepTestQueue[i]));
                UpdateRealtimeBatchResults(siteResults);
                UpdateTopCandidate(result);

                StatusText = $"已完成 {CompletedSites}/{TotalSites}";
            }

            StatusText = runMode == BatchRunMode.Deep ? "批量深度评测完成" : "批量快速评测完成";
            await RecordRunHistoryAsync(siteResults, sw.Elapsed, cancelled: false, runMode);
        }
        catch (OperationCanceledException)
        {
            StatusText = "批量评测已Cancel";
            // Still update rankings with partial results
            if (siteResults.Count > 0)
            {
                UpdateRealtimeBatchResults(siteResults);
            }
            await RecordRunHistoryAsync(siteResults, sw.Elapsed, cancelled: true, runMode);
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            sw.Stop();
            IsRunning = false;
            ActiveConnections = 0;
            IsRateLimited = false;
            _cts = null;
        }
    }

    private async Task<BatchSiteRunResult> RunQuickSiteAsync(
        BatchSiteEntry site,
        ProxyEndpointSettings settings,
        DeepTestQueueItem item,
        int queueIndex,
        CancellationToken cancellationToken)
    {
        var plannedSteps = GetPlannedStepCount(BatchRunMode.Quick);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        StartQueueItem(item, "快速基准", plannedSteps);
        ActiveConnections = 1;
        IsRateLimited = false;

        var liveProgress = new Progress<ProxyDiagnosticsLiveProgress>(progress =>
            ApplySiteProbeLiveProgress(queueIndex, "快速基准", 0, BaselineScenarioCount, plannedSteps, progress));
        var result = await _diagnosticsRunner(settings, liveProgress, cancellationToken);
        ApplyScenarioBadges(item, result.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>(), includeDeepBadges: false);
        ApplyQueueStage(
            item,
            "独立吞吐",
            BaselineScenarioCount,
            plannedSteps,
            $"基准完成 · {BuildCapabilityMatrixSummary(result)}",
            sw.Elapsed);

        result = await RunThroughputBenchmarkStepAsync(
            site,
            settings,
            result,
            item,
            BaselineScenarioCount,
            plannedSteps,
            sw,
            cancellationToken);

        sw.Stop();
        var summary = BuildBaseSiteResult(site.DisplayName, result, sw.Elapsed);
        CompleteQueueItem(item, summary, "结果汇总", plannedSteps, sw.Elapsed);
        return summary;
    }

    private async Task<BatchSiteRunResult> RunDeepSiteAsync(
        BatchSiteEntry site,
        ProxyEndpointSettings settings,
        DeepTestQueueItem item,
        int queueIndex,
        CancellationToken cancellationToken)
    {
        var plannedSteps = GetPlannedStepCount(BatchRunMode.Deep);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        StartQueueItem(item, "基准验证", plannedSteps);
        ActiveConnections = 1;
        IsRateLimited = false;

        var baselineProgress = new Progress<ProxyDiagnosticsLiveProgress>(progress =>
            ApplySiteProbeLiveProgress(queueIndex, "基准验证", 0, BaselineScenarioCount, plannedSteps, progress));
        var baseline = await _diagnosticsRunner(settings, baselineProgress, cancellationToken);
        ApplyScenarioBadges(item, baseline.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>(), includeDeepBadges: true);
        ApplyQueueStage(
            item,
            "独立吞吐",
            BaselineScenarioCount,
            plannedSteps,
            $"基准完成 · {BuildCapabilityMatrixSummary(baseline)}",
            sw.Elapsed);

        var measuredBaseline = await RunThroughputBenchmarkStepAsync(
            site,
            settings,
            baseline,
            item,
            BaselineScenarioCount,
            plannedSteps,
            sw,
            cancellationToken);
        ApplyScenarioBadges(item, measuredBaseline.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>(), includeDeepBadges: true);

        ApplyQueueStage(
            item,
            "能力深测",
            QuickPlannedStepCount,
            plannedSteps,
            BuildCapabilityMatrixSummary(measuredBaseline),
            sw.Elapsed);

        var supplementalProgress = new Progress<ProxyDiagnosticsLiveProgress>(progress =>
            ApplySupplementalProbeLiveProgress(
                queueIndex,
                "能力深测",
                QuickPlannedStepCount,
                plannedSteps,
                progress));
        var deepProbeResult = await _supplementalScenarioRunner(
            settings,
            measuredBaseline,
            supplementalProgress,
            cancellationToken);
        ApplyScenarioBadges(item, deepProbeResult.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>(), includeDeepBadges: true);

        ApplyQueueStage(
            item,
            "稳定复测",
            DeepCapabilityStepCount,
            plannedSteps,
            BuildCapabilityMatrixSummary(deepProbeResult),
            sw.Elapsed);

        var baseSummary = BuildBaseSiteResult(site.DisplayName, deepProbeResult, sw.Elapsed);

        var stabilityRound = 0;
        var stabilityRoundProgress = new Progress<ProxyDiagnosticsResult>(roundResult =>
        {
            stabilityRound++;
            var roundSummary = BuildBaseSiteResult(site.DisplayName, roundResult, sw.Elapsed);
            var currentRound = stabilityRound;
            var applied = TryApplyLiveQueueUpdate(item, () =>
            {
                item.PassCount = baseSummary.PassCount + currentRound;
                item.FailCount = Math.Max(0, baseSummary.TotalCount - baseSummary.PassCount);
                UpdateBadge(
                    item,
                    "ST",
                    $"{currentRound}/{DeepStabilityRounds}",
                    $"稳定复测：已完成第 {currentRound}/{DeepStabilityRounds} 轮。最新结果：{FormatSiteLatestResult(roundSummary)}",
                    "Running");
                ApplyQueueStageCore(
                    item,
                    "稳定复测",
                    DeepCapabilityStepCount + currentRound,
                    plannedSteps,
                    $"第 {currentRound}/{DeepStabilityRounds} 轮 · {FormatSiteLatestResult(roundSummary)}",
                    sw.Elapsed);
                StatusText = $"{site.DisplayName}: 稳定复测 {currentRound}/{DeepStabilityRounds}";
            });
            if (applied)
            {
                AddQueueTimelineItem(
                    item,
                    $"稳定复测 {currentRound}/{DeepStabilityRounds}",
                    FormatSiteLatestResult(roundSummary),
                    roundSummary.SuccessRate >= 99 ? "Pass" : "Warn");
            }
        });

        var stability = await _stabilityRunner(
            settings,
            DeepStabilityRounds,
            0,
            null,
            stabilityRoundProgress,
            cancellationToken);
        ApplyStabilityBadge(item, stability);
        ApplyQueueStage(
            item,
            "结果汇总",
            DeepCapabilityStepCount + Math.Max(stability.CompletedRounds, stabilityRound),
            plannedSteps,
            $"稳定 {stability.FullSuccessCount}/{stability.CompletedRounds} · 健康 {stability.HealthScore}/100",
            sw.Elapsed);
        ActiveConnections = 0;
        IsRateLimited = false;

        sw.Stop();
        var deepSummary = BuildDeepSiteResult(site.DisplayName, baseSummary, stability, sw.Elapsed);
        CompleteQueueItem(
            item,
            deepSummary,
            "结果汇总",
            plannedSteps,
            sw.Elapsed,
            $"{BuildCapabilityMatrixSummary(deepProbeResult)} | 稳定 {stability.HealthScore}/100");
        return deepSummary;
    }

    private static int GetPlannedStepCount(BatchRunMode runMode)
        => runMode == BatchRunMode.Deep
            ? DeepPlannedStepCount
            : QuickPlannedStepCount;

    private static BatchSiteRunResult BuildBaseSiteResult(
        string siteName,
        ProxyDiagnosticsResult result,
        TimeSpan elapsed)
    {
        var (passCount, totalCount, successRate) = ResolveSuccessMetrics(result);
        var latencyMs = ResolveLatencyMs(result, elapsed);
        var ttftMs = ResolveTtftMs(result);
        var throughput = ResolveThroughput(result);
        var score = ComputeCompositeScore(latencyMs, throughput, successRate);

        return new BatchSiteRunResult(
            siteName,
            latencyMs,
            ttftMs,
            throughput,
            successRate,
            score,
            passCount,
            Math.Max(totalCount, 1),
            elapsed);
    }

    private static BatchSiteRunResult BuildDeepSiteResult(
        string siteName,
        BatchSiteRunResult baseline,
        ProxyStabilityResult stability,
        TimeSpan elapsed)
    {
        var latencyMs = stability.AverageChatLatency?.TotalMilliseconds ?? baseline.LatencyMs;
        var ttftMs = stability.AverageStreamFirstTokenLatency?.TotalMilliseconds ?? baseline.TtftMs;
        var throughput = baseline.Throughput;
        var successRate = stability.CompletedRounds > 0
            ? stability.FullSuccessRate
            : baseline.SuccessRate;
        var score = Math.Clamp(
            Math.Round((baseline.Score * 0.65) + (stability.HealthScore * 0.35), 1),
            0,
            100);
        var totalCount = baseline.TotalCount +
                         Math.Max(stability.RequestedRounds, stability.CompletedRounds);
        var passCount = baseline.PassCount + stability.FullSuccessCount;

        return new BatchSiteRunResult(
            siteName,
            latencyMs,
            ttftMs,
            throughput,
            successRate,
            score,
            passCount,
            Math.Max(totalCount, 1),
            elapsed);
    }

    private static BatchSiteRunResult BuildFailedSiteResult(string siteName)
        => new(siteName, 0, null, 0, 0, 0, 0, 1, TimeSpan.Zero);

    private static BatchSiteRunSnapshot ToRunSnapshot(
        BatchSiteRunResult result,
        BatchSiteEntry? site,
        DeepTestQueueItem? queueItem)
    {
        var cacheBadge = queueItem?.TestBadges.FirstOrDefault(static badge =>
            string.Equals(badge.Label, "Cch", StringComparison.OrdinalIgnoreCase));
        var cacheState = cacheBadge is null || string.IsNullOrWhiteSpace(cacheBadge.Value)
            ? "--"
            : cacheBadge.Value;
        var capability = result.TotalCount > 0 ? $"{result.PassCount}/{result.TotalCount}" : "--";
        var protocol = string.IsNullOrWhiteSpace(site?.ProtocolSummary) ? "未探测" : site.ProtocolSummary;
        var latest = queueItem?.LatestResult ?? FormatSiteLatestResult(result);
        var verdict = ResolveBatchSnapshotVerdict(
            result.Score,
            result.SuccessRate,
            result.PassCount,
            result.TotalCount,
            latest);
        var secondary = BuildBatchSnapshotSecondary(
            result.LatencyMs,
            result.TtftMs,
            result.Throughput,
            result.SuccessRate,
            result.Score,
            capability,
            cacheState,
            protocol);

        return new BatchSiteRunSnapshot(
            result.Name,
            result.LatencyMs,
            result.TtftMs,
            result.Throughput,
            result.SuccessRate,
            result.Score,
            result.PassCount,
            result.TotalCount,
            capability,
            cacheState,
            protocol,
            latest,
            verdict,
            secondary,
            1);
    }

    private async Task<ProxyDiagnosticsResult> RunThroughputBenchmarkStepAsync(
        BatchSiteEntry site,
        ProxyEndpointSettings settings,
        ProxyDiagnosticsResult baselineResult,
        DeepTestQueueItem item,
        double phaseOffset,
        int plannedSteps,
        System.Diagnostics.Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var throughputProgress = new Progress<ProxyThroughputBenchmarkLiveProgress>(progress =>
        {
            var requestedSamples = Math.Max(progress.RequestedSampleCount, 1);
            var completedSamples = Math.Clamp(progress.CompletedSampleCount, 0, requestedSamples);
            var phaseProgress = completedSamples / (double)requestedSamples;
            var currentSampleText = progress.CurrentSampleIndex <= 0
                ? $"{completedSamples}/{requestedSamples}"
                : $"{progress.CurrentSampleIndex}/{requestedSamples}";

            TryApplyLiveQueueUpdate(item, () =>
            {
                ApplyQueueStageCore(
                    item,
                    $"独立吞吐 {currentSampleText}",
                    phaseOffset + phaseProgress,
                    plannedSteps,
                    progress.Summary,
                    stopwatch.Elapsed);
                StatusText = $"{site.DisplayName}: 独立吞吐 {currentSampleText}";
            });
        });

        var benchmark = await _throughputBenchmarkRunner(
            settings,
            baselineResult,
            throughputProgress,
            cancellationToken);

        if (benchmark is null)
        {
            UpdateBadge(
                item,
                "TP",
                "SK",
                "独立吞吐：测试替身未启用，未执行 tok/s 采样。",
                "Off");
            ApplyQueueStage(
                item,
                "独立吞吐",
                phaseOffset + ThroughputBenchmarkStepCount,
                plannedSteps,
                "独立吞吐未启用",
                stopwatch.Elapsed);
            return baselineResult;
        }

        var measuredResult = baselineResult with { ThroughputBenchmarkResult = benchmark };
        ApplyThroughputBadge(item, benchmark);
        ApplyQueueStage(
            item,
            "独立吞吐",
            phaseOffset + ThroughputBenchmarkStepCount,
            plannedSteps,
            BuildThroughputBenchmarkDigest(benchmark),
            stopwatch.Elapsed);
        return measuredResult;
    }

    private void StartQueueItem(DeepTestQueueItem item, string stage, int plannedSteps)
    {
        item.Status = DeepTestStatus.Active;
        item.CurrentStage = stage;
        item.StartedAtText = DateTimeOffset.Now.ToString("HH:mm:ss");
        item.ElapsedText = "00:00";
        item.RoundText = $"0/{plannedSteps}";
        item.LatestResult = "启动中";
        item.ProgressMaximum = plannedSteps;
        item.ProgressValue = 0;
        item.ScoreText = "0";
        AddQueueTimelineItem(item, stage, "开始执行", "Running");
    }

    private bool TryApplyLiveQueueUpdate(DeepTestQueueItem item, Action update)
    {
        lock (_queueProgressGate)
        {
            if (item.Status == DeepTestStatus.Completed)
            {
                return false;
            }

            update();
            return true;
        }
    }

    private bool ApplyQueueStage(
        DeepTestQueueItem item,
        string stage,
        double progressValue,
        int plannedSteps,
        string latestResult,
        TimeSpan elapsed)
    {
        var applied = TryApplyLiveQueueUpdate(
            item,
            () => ApplyQueueStageCore(item, stage, progressValue, plannedSteps, latestResult, elapsed));
        if (applied)
        {
            AddQueueTimelineItem(item, stage, latestResult, "Running");
        }

        return applied;
    }

    private static void ApplyQueueStageCore(
        DeepTestQueueItem item,
        string stage,
        double progressValue,
        int plannedSteps,
        string latestResult,
        TimeSpan elapsed)
    {
        item.Status = DeepTestStatus.Active;
        item.CurrentStage = stage;
        item.RoundText = $"{Math.Min(progressValue, plannedSteps):F0}/{plannedSteps}";
        item.LatestResult = latestResult;
        item.ProgressMaximum = plannedSteps;
        item.ProgressValue = Math.Clamp(progressValue, 0, plannedSteps);
        item.ElapsedText = FormatDuration(elapsed);
    }

    private void CompleteQueueItem(
        DeepTestQueueItem item,
        BatchSiteRunResult result,
        string stage,
        int plannedSteps,
        TimeSpan elapsed,
        string? latestResult = null)
    {
        lock (_queueProgressGate)
        {
            item.PassCount = result.PassCount;
            item.FailCount = Math.Max(0, result.TotalCount - result.PassCount);
            item.Status = DeepTestStatus.Completed;
            item.CurrentStage = stage;
            item.RoundText = $"{plannedSteps}/{plannedSteps}";
            item.LatestResult = latestResult ?? FormatSiteLatestResult(result);
            item.ScoreText = $"{result.Score:F1}";
            item.ProgressMaximum = plannedSteps;
            item.ProgressValue = plannedSteps;
            item.ElapsedText = FormatDuration(elapsed);
        }

        AddQueueTimelineItem(item, stage, latestResult ?? FormatSiteLatestResult(result), "Pass");
    }

    private void CompleteFailedQueueItem(DeepTestQueueItem item, string error)
    {
        var summary = string.IsNullOrWhiteSpace(error) ? "评测失败" : $"评测失败：{error}";
        lock (_queueProgressGate)
        {
            item.Status = DeepTestStatus.Completed;
            item.CurrentStage = "失败";
            item.LatestResult = summary;
            item.ScoreText = "0";
            item.ProgressValue = item.ProgressMaximum;
            item.ElapsedText = item.ElapsedText == "--" ? "00:00" : item.ElapsedText;
        }

        AddQueueTimelineItem(item, "失败", summary, "Fail");
    }

    private void AddTimelineItem(string siteName, string stage, string summary, string tone)
    {
        var normalizedSite = NormalizeTimelineText(siteName, "--", 34);
        var normalizedStage = NormalizeTimelineText(stage, "--", 28);
        var normalizedSummary = NormalizeTimelineText(summary, "--", 96);
        var normalizedTone = string.IsNullOrWhiteSpace(tone) ? "Info" : tone.Trim();
        var key = $"{normalizedSite}|{normalizedStage}|{normalizedSummary}|{normalizedTone}";
        if (string.Equals(_lastTimelineKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _lastTimelineKey = key;
        RunTimeline.Insert(0, new BatchRunTimelineItem(
            normalizedSite,
            normalizedStage,
            normalizedSummary,
            normalizedTone));

        while (RunTimeline.Count > MaxTimelineItems)
        {
            RunTimeline.RemoveAt(RunTimeline.Count - 1);
        }
    }

    private void AddQueueTimelineItem(
        DeepTestQueueItem item,
        string stage,
        string summary,
        string tone,
        bool includeGlobal = true)
    {
        var normalizedSite = NormalizeTimelineText(item.SiteName, "--", 34);
        var normalizedStage = NormalizeTimelineText(stage, "--", 28);
        var normalizedSummary = NormalizeTimelineText(summary, "--", 96);
        var normalizedTone = string.IsNullOrWhiteSpace(tone) ? "Info" : tone.Trim();
        var latest = item.TimelineItems.FirstOrDefault();
        if (latest is not null &&
            string.Equals(latest.Stage, normalizedStage, StringComparison.Ordinal) &&
            string.Equals(latest.Summary, normalizedSummary, StringComparison.Ordinal) &&
            string.Equals(latest.Tone, normalizedTone, StringComparison.Ordinal))
        {
            return;
        }

        item.TimelineItems.Insert(0, new BatchRunTimelineItem(
            normalizedSite,
            normalizedStage,
            normalizedSummary,
            normalizedTone));
        while (item.TimelineItems.Count > MaxSiteTimelineItems)
        {
            item.TimelineItems.RemoveAt(item.TimelineItems.Count - 1);
        }

        if (includeGlobal)
        {
            AddTimelineItem(normalizedSite, normalizedStage, normalizedSummary, normalizedTone);
        }
    }

    private static string NormalizeTimelineText(string? value, string fallback, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(1, maxLength - 1)] + "…";
    }

    private void ApplySiteProbeLiveProgress(
        int queueIndex,
        string phase,
        double phaseOffset,
        double phaseWeight,
        int plannedSteps,
        ProxyDiagnosticsLiveProgress progress)
    {
        if (queueIndex < 0 || queueIndex >= DeepTestQueue.Count)
        {
            return;
        }

        var item = DeepTestQueue[queueIndex];
        var scenario = progress.CurrentScenarioResult;
        var passed = progress.ScenarioResults.Count(static scenarioResult => scenarioResult.Success);
        var failed = progress.ScenarioResults.Count(static scenarioResult => !scenarioResult.Success);
        var scenarioProgress = progress.TotalScenarioCount <= 0
            ? 0
            : progress.CompletedScenarioCount / (double)progress.TotalScenarioCount;
        var displayCompleted = Math.Clamp(
            phaseOffset + Math.Min(progress.CompletedScenarioCount, phaseWeight),
            0,
            plannedSteps);
        var applied = TryApplyLiveQueueUpdate(item, () =>
        {
            item.Status = DeepTestStatus.Active;
            item.PassCount = passed;
            item.FailCount = failed;
            item.CurrentStage = $"{phase} · {scenario.DisplayName}";
            item.RoundText = $"{displayCompleted:F0}/{plannedSteps}";
            item.LatestResult = scenario.Summary;
            item.ProgressMaximum = plannedSteps;
            item.ProgressValue = Math.Clamp(phaseOffset + (scenarioProgress * phaseWeight), 0, plannedSteps);

            StatusText = $"{item.SiteName}: {phase} {displayCompleted:F0}/{plannedSteps}";
        });
        if (applied && progress.CompletedScenarioCount > 0)
        {
            AddQueueTimelineItem(
                item,
                $"{phase} {displayCompleted:F0}/{plannedSteps}",
                scenario.Summary,
                scenario.Success ? "Pass" : "Warn");
        }
    }

    private void ApplySupplementalProbeLiveProgress(
        int queueIndex,
        string phase,
        double phaseOffset,
        int plannedSteps,
        ProxyDiagnosticsLiveProgress progress)
    {
        if (queueIndex < 0 || queueIndex >= DeepTestQueue.Count)
        {
            return;
        }

        var item = DeepTestQueue[queueIndex];
        var scenario = progress.CurrentScenarioResult;
        var scenarios = progress.ScenarioResults;
        var supplementalCompleted = CountCompletedDeepSupplementalScenarios(scenarios);
        var displayCompleted = Math.Clamp(phaseOffset + supplementalCompleted, 0, plannedSteps);
        var passed = scenarios.Count(static scenarioResult => scenarioResult.Success);
        var failed = scenarios.Count(static scenarioResult => !scenarioResult.Success);

        var applied = TryApplyLiveQueueUpdate(item, () =>
        {
            item.Status = DeepTestStatus.Active;
            item.PassCount = passed;
            item.FailCount = failed;
            item.CurrentStage = $"{phase} · {scenario.DisplayName}";
            item.RoundText = $"{displayCompleted:F0}/{plannedSteps}";
            item.LatestResult = BuildCapabilityMatrixSummary(scenarios);
            item.ProgressMaximum = plannedSteps;
            item.ProgressValue = displayCompleted;
            ApplyScenarioBadges(item, scenarios, includeDeepBadges: true);
            StatusText = $"{item.SiteName}: {phase} {supplementalCompleted}/{DeepSupplementalScenarioCount}";
        });
        if (applied && supplementalCompleted > 0)
        {
            AddQueueTimelineItem(
                item,
                $"{phase} {supplementalCompleted}/{DeepSupplementalScenarioCount}",
                BuildCapabilityMatrixSummary(scenarios),
                failed == 0 ? "Pass" : "Warn");
        }
    }

    private void UpdateEstimatedRemaining(TimeSpan elapsed, int completedSites, int totalSites)
    {
        if (completedSites <= 0 || totalSites <= completedSites)
        {
            EstimatedRemaining = "0s";
            return;
        }

        var averageSeconds = elapsed.TotalSeconds / completedSites;
        var remaining = TimeSpan.FromSeconds(averageSeconds * (totalSites - completedSites));
        EstimatedRemaining = remaining.TotalMinutes >= 1
            ? $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s"
            : $"{remaining.Seconds}s";
    }

    private void UpdateTopCandidate(BatchSiteRunResult result)
    {
        if (result.Score <= TopSiteScore)
        {
            return;
        }

        TopSiteScore = result.Score;
        TopSiteName = result.Name;
        TopLatencyDisplay = $"{result.LatencyMs:F0} ms";
        TopSuccessRateDisplay = $"{result.SuccessRate:F1}%";
        TopThroughputDisplay = $"{result.Throughput:F1} tok/s";
    }

}
