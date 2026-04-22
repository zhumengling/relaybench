using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task<IReadOnlyList<ProxyBatchProbeRow>> ProbeBatchEntriesAsync(
        IReadOnlyList<ProxyBatchTargetEntry> entries,
        int timeoutSeconds,
        bool enableLongStreamingTest,
        int longStreamSegmentCount,
        IProgress<string>? progress,
        IProgress<ProxyBatchProbeRow>? rowProgress,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<ProxyBatchProbeRow>();
        }

        var buckets = BuildProxyBatchExecutionBuckets(entries);
        var concurrency = Math.Clamp(buckets.Count, 1, 4);
        using SemaphoreSlim gate = new(concurrency, concurrency);
        var completed = 0;
        var rows = new ProxyBatchProbeRow[entries.Count];

        var tasks = buckets.Select(async bucket =>
        {
            var enteredGate = false;
            await gate.WaitAsync(cancellationToken);
            enteredGate = true;
            try
            {
                foreach (var indexedEntry in bucket.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows[indexedEntry.Index] = await ProbeSingleBatchEntryAsync(
                        indexedEntry.Entry,
                        entries.Count,
                        timeoutSeconds,
                        enableLongStreamingTest,
                        longStreamSegmentCount,
                        progress,
                        rowProgress,
                        () => Interlocked.Increment(ref completed),
                        cancellationToken);
                }
            }
            finally
            {
                if (enteredGate)
                {
                    gate.Release();
                }
            }
        });

        await Task.WhenAll(tasks);
        return rows;
    }

    private async Task<ProxyBatchProbeRow> ProbeSingleBatchEntryAsync(
        ProxyBatchTargetEntry entry,
        int totalEntryCount,
        int timeoutSeconds,
        bool enableLongStreamingTest,
        int longStreamSegmentCount,
        IProgress<string>? progress,
        IProgress<ProxyBatchProbeRow>? rowProgress,
        Func<int> incrementCompletedCount,
        CancellationToken cancellationToken)
    {
        var settings = new ProxyEndpointSettings(
            entry.BaseUrl,
            entry.ApiKey,
            entry.Model,
            ProxyIgnoreTlsErrors,
            timeoutSeconds);
        var baselineProgress = new Progress<ProxyDiagnosticsLiveProgress>(liveProgress =>
        {
            rowProgress?.Report(BuildLiveProxyBatchProbeRow(entry, liveProgress));
        });

        progress?.Report($"正在探测 {entry.Name}：基础兼容性诊断...");
        var result = await _proxyDiagnosticsService.RunAsync(
            settings,
            baselineProgress,
            cancellationToken,
            streamThroughputSampleCount: 3);
        progress?.Report($"正在探测 {entry.Name}：独立吞吐测试（3 轮）...");
        var throughputSettings = string.IsNullOrWhiteSpace(result.EffectiveModel)
            ? settings
            : settings with { Model = result.EffectiveModel };
        var throughputBaseResult = result;
        var throughputLiveProgress = new Progress<ProxyThroughputBenchmarkLiveProgress>(liveProgress =>
        {
            progress?.Report(liveProgress.Summary);
            rowProgress?.Report(CreateProxyBatchProbeRow(
                entry,
                BuildLiveThroughputDiagnosticsResult(throughputBaseResult, liveProgress),
                ProxyBatchProbeStage.Throughput));
        });
        var throughputBenchmark = await _proxyDiagnosticsService.RunThroughputBenchmarkAsync(
            throughputSettings,
            liveProgress: throughputLiveProgress,
            cancellationToken: cancellationToken);
        result = result with { ThroughputBenchmarkResult = throughputBenchmark };
        rowProgress?.Report(CreateProxyBatchProbeRow(
            entry,
            result,
            enableLongStreamingTest ? ProxyBatchProbeStage.Throughput : ProxyBatchProbeStage.Completed));
        if (enableLongStreamingTest)
        {
            progress?.Report($"正在探测 {entry.Name}：长流稳定简测（{longStreamSegmentCount} 段）...");
            var longStreamingSettings = string.IsNullOrWhiteSpace(result.EffectiveModel)
                ? settings
                : settings with { Model = result.EffectiveModel };
            var longStreamingResult = await _proxyDiagnosticsService.RunLongStreamingTestAsync(
                longStreamingSettings,
                longStreamSegmentCount,
                cancellationToken);
            result = result with { LongStreamingResult = longStreamingResult };
        }

        var row = CreateProxyBatchProbeRow(entry, result, ProxyBatchProbeStage.Completed);
        var done = incrementCompletedCount();
        progress?.Report($"正在探测 {done}/{totalEntryCount}：{entry.Name}");
        rowProgress?.Report(row);
        return row;
    }

    private static IReadOnlyList<ProxyBatchExecutionBucket> BuildProxyBatchExecutionBuckets(
        IReadOnlyList<ProxyBatchTargetEntry> entries)
    {
        List<(string Key, List<ProxyBatchIndexedTargetEntry> Entries)> buckets = [];
        Dictionary<string, int> bucketIndexByKey = new(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var bucketKey = GetProxyBatchExecutionBucketKey(entry, index);
            if (!bucketIndexByKey.TryGetValue(bucketKey, out var bucketIndex))
            {
                bucketIndex = buckets.Count;
                bucketIndexByKey[bucketKey] = bucketIndex;
                buckets.Add((bucketKey, []));
            }

            buckets[bucketIndex].Entries.Add(new ProxyBatchIndexedTargetEntry(index, entry));
        }

        return buckets
            .Select(bucket => new ProxyBatchExecutionBucket(bucket.Key, bucket.Entries))
            .ToArray();
    }

    private static string GetProxyBatchExecutionBucketKey(ProxyBatchTargetEntry entry, int index)
    {
        if (!string.IsNullOrWhiteSpace(entry.SiteGroupName))
        {
            return $"group:{entry.SiteGroupName.Trim()}";
        }

        return $"entry:{index}";
    }

    private static bool IsFullSuccess(ProxyDiagnosticsResult result)
        => result.ModelsRequestSucceeded && result.ChatRequestSucceeded && result.StreamRequestSucceeded;

    private static ProxyBatchProbeRow BuildLiveProxyBatchProbeRow(
        ProxyBatchTargetEntry entry,
        ProxyDiagnosticsLiveProgress progress)
    {
        var liveResult = BuildLiveProxyBatchResult(progress);
        return new ProxyBatchProbeRow(
            entry,
            liveResult,
            ComputeProxyBatchScore(liveResult),
            ProxyBatchProbeStage.Baseline,
            progress.CompletedScenarioCount,
            progress.TotalScenarioCount);
    }

    private static ProxyBatchProbeRow CreatePendingProxyBatchProbeRow(
        ProxyBatchTargetEntry entry,
        string pendingMessage)
    {
        var pendingResult = BuildPendingProxyBatchResult(entry, pendingMessage);
        return new ProxyBatchProbeRow(
            entry,
            pendingResult,
            0,
            ProxyBatchProbeStage.Baseline,
            0,
            GetOrderedScenarioDefinitions().Length,
            true,
            pendingMessage);
    }

    private static ProxyBatchProbeRow CreateProxyBatchProbeRow(
        ProxyBatchTargetEntry entry,
        ProxyDiagnosticsResult result,
        ProxyBatchProbeStage stage)
    {
        var completedBaselineScenarioCount = ResolveBatchExecutedCapabilityCount(result);
        return new ProxyBatchProbeRow(
            entry,
            result,
            ComputeProxyBatchScore(result),
            stage,
            completedBaselineScenarioCount,
            GetOrderedScenarioDefinitions().Length);
    }

    private static ProxyDiagnosticsResult BuildPendingProxyBatchResult(
        ProxyBatchTargetEntry entry,
        string pendingMessage)
        => new(
            DateTimeOffset.Now,
            entry.BaseUrl,
            entry.Model,
            null,
            false,
            null,
            0,
            Array.Empty<string>(),
            null,
            false,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            null,
            pendingMessage,
            null,
            ScenarioResults: Array.Empty<ProxyProbeScenarioResult>(),
            Verdict: pendingMessage,
            Recommendation: pendingMessage,
            PrimaryIssue: pendingMessage);

    private static ProxyDiagnosticsResult BuildLiveThroughputDiagnosticsResult(
        ProxyDiagnosticsResult result,
        ProxyThroughputBenchmarkLiveProgress progress)
        => result with
        {
            ThroughputBenchmarkResult = BuildLiveThroughputBenchmarkResult(progress),
            Verdict = "\u8FDB\u884C\u4E2D",
            PrimaryIssue = progress.Summary
        };

    private static ProxyThroughputBenchmarkResult BuildLiveThroughputBenchmarkResult(ProxyThroughputBenchmarkLiveProgress progress)
        => new(
            progress.ReportedAt,
            progress.BaseUrl,
            progress.Model,
            progress.RequestedSampleCount,
            progress.CompletedSampleCount,
            progress.SuccessfulSampleCount,
            progress.SegmentCount,
            progress.LiveMedianOutputTokensPerSecond ?? progress.CurrentOutputTokensPerSecond,
            progress.LiveAverageOutputTokensPerSecond ?? progress.CurrentOutputTokensPerSecond,
            progress.LiveMinimumOutputTokensPerSecond ?? progress.CurrentOutputTokensPerSecond,
            progress.LiveMaximumOutputTokensPerSecond ?? progress.CurrentOutputTokensPerSecond,
            progress.CurrentEndToEndTokensPerSecond,
            progress.CurrentOutputTokenCount,
            progress.CurrentOutputTokenCountEstimated,
            progress.Summary,
            null,
            Array.Empty<ProxyStreamingStabilityResult>(),
            IsLive: true,
            CurrentSampleIndex: progress.CurrentSampleIndex,
            CurrentSampleElapsed: progress.CurrentSampleElapsed,
            CurrentOutputTokenCount: progress.CurrentOutputTokenCount,
            CurrentOutputTokenCountEstimated: progress.CurrentOutputTokenCountEstimated,
            CurrentOutputTokensPerSecond: progress.CurrentOutputTokensPerSecond,
            CurrentEndToEndTokensPerSecond: progress.CurrentEndToEndTokensPerSecond);

    private static ProxyDiagnosticsResult BuildLiveProxyBatchResult(ProxyDiagnosticsLiveProgress progress)
    {
        var scenarioResults = progress.ScenarioResults.ToArray();
        var modelsScenario = FindScenario(scenarioResults, ProxyProbeScenarioKind.Models);
        var chatScenario = FindScenario(scenarioResults, ProxyProbeScenarioKind.ChatCompletions);
        var streamScenario = FindScenario(scenarioResults, ProxyProbeScenarioKind.ChatCompletionsStream);

        return new ProxyDiagnosticsResult(
            progress.ReportedAt,
            progress.BaseUrl,
            progress.RequestedModel,
            progress.EffectiveModel,
            modelsScenario?.Success == true,
            modelsScenario?.StatusCode,
            progress.ModelCount,
            progress.SampleModels,
            modelsScenario?.Latency,
            chatScenario?.Success == true,
            chatScenario?.StatusCode,
            chatScenario?.Latency,
            chatScenario?.Preview,
            streamScenario?.Success == true,
            streamScenario?.StatusCode,
            streamScenario?.FirstTokenLatency,
            streamScenario?.Duration,
            streamScenario?.Preview,
            $"基础兼容性进行中：已完成 {progress.CompletedScenarioCount}/{progress.TotalScenarioCount}；最新 {progress.CurrentScenarioResult.DisplayName} / {progress.CurrentScenarioResult.Summary}",
            progress.CurrentScenarioResult.Success ? null : progress.CurrentScenarioResult.Error,
            ScenarioResults: scenarioResults,
            PrimaryFailureKind: progress.CurrentScenarioResult.FailureKind,
            PrimaryFailureStage: progress.CurrentScenarioResult.FailureStage,
            Verdict: "进行中",
            PrimaryIssue: $"{progress.CurrentScenarioResult.DisplayName}：{progress.CurrentScenarioResult.CapabilityStatus}",
            RequestId: progress.CurrentScenarioResult.RequestId,
            TraceId: progress.CurrentScenarioResult.TraceId);
    }

    private static int ComputeProxyBatchScore(ProxyDiagnosticsResult result)
    {
        var score = 0;
        var responsesScenario = result.ScenarioResults?.FirstOrDefault(item => item.Scenario == ProxyProbeScenarioKind.Responses);
        var structuredOutputScenario = result.ScenarioResults?.FirstOrDefault(item => item.Scenario == ProxyProbeScenarioKind.StructuredOutput);

        if (result.ModelsRequestSucceeded)
        {
            score += 18;
        }

        if (result.ChatRequestSucceeded)
        {
            score += 24;
        }

        if (result.StreamRequestSucceeded)
        {
            score += 24;
        }

        if (responsesScenario?.Success == true)
        {
            score += 18;
        }

        if (structuredOutputScenario?.Success == true)
        {
            score += 8;
        }

        if ((result.ResolvedAddresses?.Count ?? 0) <= 1 && string.IsNullOrWhiteSpace(result.CdnProvider))
        {
            score += 2;
        }

        if (string.IsNullOrWhiteSpace(result.Error))
        {
            score += 8;
        }

        if (result.PrimaryFailureKind is ProxyFailureKind.AuthRejected or ProxyFailureKind.TlsHandshakeFailure or ProxyFailureKind.Timeout)
        {
            score -= 12;
        }
        else if (result.PrimaryFailureKind is ProxyFailureKind.UnsupportedEndpoint or ProxyFailureKind.SemanticMismatch)
        {
            score -= 6;
        }

        if ((result.ResolvedAddresses?.Count ?? 0) >= 4)
        {
            score -= 2;
        }

        score -= GetLatencyPenalty(result.ChatLatency?.TotalMilliseconds, 900, 1_800, 4_000, 2, 6, 12);
        score -= GetLatencyPenalty(result.StreamFirstTokenLatency?.TotalMilliseconds, 600, 1_500, 3_000, 2, 5, 10);
        return Math.Clamp(score, 0, 100);
    }

    private static int GetLatencyPenalty(
        double? milliseconds,
        double mildThreshold,
        double mediumThreshold,
        double highThreshold,
        int mildPenalty,
        int mediumPenalty,
        int highPenalty)
    {
        if (milliseconds is null)
        {
            return 0;
        }

        if (milliseconds.Value >= highThreshold)
        {
            return highPenalty;
        }

        if (milliseconds.Value >= mediumThreshold)
        {
            return mediumPenalty;
        }

        return milliseconds.Value >= mildThreshold ? mildPenalty : 0;
    }

    private static string BuildProxyBatchCardStatus(IReadOnlyList<ProxyBatchProbeRow> rows)
        => BuildProxyBatchCardStatus(OrderBatchAggregateRows(BuildProxyBatchAggregateRows(new[] { rows })).ToArray());

    private static string BuildProxyBatchCardStatus(IReadOnlyList<ProxyBatchAggregateRow> rows)
    {
        if (rows.Count == 0)
        {
            return "未运行";
        }

        return BuildBatchStabilityLabel(rows[0]) switch
        {
            "稳定" => "入口组稳定",
            "可用" => "需复核",
            _ => "失败"
        };
    }

    private static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "未填写";
        }

        var text = apiKey.Trim();
        if (text.Length <= 10)
        {
            return new string('*', Math.Max(4, text.Length));
        }

        return $"{text[..6]}...{text[^4..]}";
    }
}
