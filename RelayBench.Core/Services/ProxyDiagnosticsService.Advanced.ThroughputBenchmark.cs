using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private const int ThroughputBenchmarkTargetOutputTokens = 900;
    private const int ThroughputBenchmarkMinimumOutputTokens = 512;
    private const int ThroughputBenchmarkMaximumOutputTokens = 1400;
    private const int ThroughputBenchmarkShortTargetMinimumTokens = 96;
    private const int ThroughputBenchmarkShortTargetMaximumTokens = 256;
    private const double ThroughputBenchmarkMaximumPlausibleTokensPerSecond = 10_000d;

    public async Task<ProxyThroughputBenchmarkResult> RunThroughputBenchmarkAsync(
        ProxyEndpointSettings settings,
        int requestedSampleCount = 3,
        int requestedSegmentCount = 0,
        ProxyDiagnosticsResult? baselineResult = null,
        IProgress<ProxyThroughputBenchmarkLiveProgress>? liveProgress = null,
        CancellationToken cancellationToken = default)
    {
        var sampleCount = Math.Clamp(requestedSampleCount, 1, 3);
        var targetOutputTokens = ResolveThroughputBenchmarkTargetOutputTokens(requestedSegmentCount);
        var sampleTimeout = ResolveThroughputBenchmarkSampleTimeout(settings.TimeoutSeconds, targetOutputTokens);

        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return new ProxyThroughputBenchmarkResult(
                DateTimeOffset.Now,
                settings.BaseUrl,
                settings.Model,
                sampleCount,
                0,
                0,
                targetOutputTokens,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                "独立吞吐测试参数校验失败。",
                error,
                Array.Empty<ProxyStreamingStabilityResult>());
        }

        var throughputSettings = normalizedSettings with
        {
            TimeoutSeconds = Math.Max(
                normalizedSettings.TimeoutSeconds,
                (int)Math.Ceiling(sampleTimeout.TotalSeconds))
        };
        List<ProxyStreamingStabilityResult> samples = [];
        using var client = CreateClient(baseUri, throughputSettings);
        var transport = await ResolveConversationProbeTransportAsync(
            client,
            baseUri,
            throughputSettings.Model,
            baselineResult,
            cancellationToken);
        client.Timeout = Timeout.InfiniteTimeSpan;
        for (var index = 0; index < sampleCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSampleIndex = index + 1;
            liveProgress?.Report(BuildThroughputBenchmarkLiveProgress(
                baseUri.ToString(),
                throughputSettings.Model,
                sampleCount,
                targetOutputTokens,
                samples,
                currentSampleIndex,
                new StreamingReadLiveProgress(
                    TimeSpan.Zero,
                    null,
                    string.Empty,
                    string.Empty,
                    0,
                    false,
                    null,
                    false,
                    0,
                    null,
                    null)));
            cancellationToken.ThrowIfCancellationRequested();
            ProxyStreamingStabilityResult sample;
            var sampleStopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var firstTokenTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstTokenTimeoutSource.CancelAfter(sampleTimeout);
            try
            {
                sample = await RunThroughputBenchmarkSampleAsync(
                    client,
                    baseUri,
                    throughputSettings,
                    transport,
                    targetOutputTokens,
                    liveSnapshot =>
                    {
                        if (liveSnapshot.FirstTokenLatency.HasValue &&
                            !firstTokenTimeoutSource.IsCancellationRequested)
                        {
                            firstTokenTimeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
                        }

                        liveProgress?.Report(BuildThroughputBenchmarkLiveProgress(
                            baseUri.ToString(),
                            throughputSettings.Model,
                            sampleCount,
                            targetOutputTokens,
                            samples,
                            currentSampleIndex,
                            liveSnapshot));
                    },
                    firstTokenTimeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                sampleStopwatch.Stop();
                sample = BuildThroughputBenchmarkTimeoutSample(
                    baseUri.ToString(),
                    throughputSettings.Model,
                    targetOutputTokens,
                    sampleStopwatch.Elapsed);
            }

            samples.Add(sample);
            liveProgress?.Report(BuildCompletedThroughputBenchmarkLiveProgress(
                baseUri.ToString(),
                throughputSettings.Model,
                sampleCount,
                targetOutputTokens,
                samples,
                currentSampleIndex));
        }

        var successfulSamples = samples
            .Where(static sample => sample.Success && ResolveStableThroughput(sample).HasValue)
            .ToArray();
        var outputSamples = successfulSamples
            .Select(static sample => ResolveStableThroughput(sample))
            .Where(static sample => sample.HasValue)
            .Select(static sample => sample!.Value)
            .ToArray();
        var endToEndSamples = successfulSamples
            .Select(static sample => sample.EndToEndTokensPerSecond)
            .Where(static sample => sample.HasValue)
            .Select(static sample => sample!.Value)
            .ToArray();
        var outputTokenCounts = successfulSamples
            .Select(static sample => sample.OutputTokenCount)
            .Where(static count => count is > 0)
            .Select(static count => count!.Value)
            .ToArray();

        var medianOutput = Median(outputSamples);
        var averageOutput = outputSamples.Length == 0 ? (double?)null : outputSamples.Average();
        var minimumOutput = outputSamples.Length == 0 ? (double?)null : outputSamples.Min();
        var maximumOutput = outputSamples.Length == 0 ? (double?)null : outputSamples.Max();
        var medianEndToEnd = Median(endToEndSamples);
        var averageOutputCount = outputTokenCounts.Length == 0
            ? null
            : (int?)Math.Round(outputTokenCounts.Average());
        var outputEstimated = successfulSamples.Any(static sample => sample.OutputTokenCountEstimated);
        var requestId = successfulSamples
            .Select(static sample => sample.RequestId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        var traceId = successfulSamples
            .Select(static sample => sample.TraceId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

        var summary = successfulSamples.Length == 0
            ? $"独立吞吐测试未通过：0/{sampleCount} 轮拿到可用样本。"
            : $"独立吞吐测试：{successfulSamples.Length}/{sampleCount} 轮可用，首 token 后结果 {FormatThroughput(medianOutput)}，区间 {FormatRange(minimumOutput, maximumOutput)}。";

        var errorMessage = successfulSamples.Length == 0
            ? samples.Select(static sample => sample.Error).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
              "独立吞吐测试未拿到可用样本。"
            : null;

        return new ProxyThroughputBenchmarkResult(
            DateTimeOffset.Now,
            baseUri.ToString(),
            throughputSettings.Model,
            sampleCount,
            samples.Count,
            successfulSamples.Length,
            targetOutputTokens,
            medianOutput,
            averageOutput,
            minimumOutput,
            maximumOutput,
            medianEndToEnd,
            averageOutputCount,
            outputEstimated,
            summary,
            errorMessage,
            samples,
            requestId,
            traceId);
    }

    private async Task<ProxyStreamingStabilityResult> RunThroughputBenchmarkSampleAsync(
        HttpClient client,
        Uri baseUri,
        ProxyEndpointSettings settings,
        ConversationProbeTransport transport,
        int targetOutputTokens,
        Action<StreamingReadLiveProgress>? liveReporter,
        CancellationToken cancellationToken)
    {
        var path = transport.Path;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(
                    BuildConversationWirePayload(
                        transport.WireApi,
                        BuildThroughputStreamingPayload(settings.Model, targetOutputTokens)),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
            transport.RequestConfigurer?.Invoke(request);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var headers = ExtractInterestingHeaders(response);
            var requestId = ExtractRequestId(headers);
            var traceId = ExtractTraceId(headers);
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var bodySample = ExtractBodySample(content);
                stopwatch.Stop();
                return new ProxyStreamingStabilityResult(
                    DateTimeOffset.Now,
                    baseUri.ToString(),
                    settings.Model,
                    false,
                    false,
                    targetOutputTokens,
                    0,
                    false,
                    0,
                    null,
                    stopwatch.Elapsed,
                    null,
                    null,
                    null,
                    false,
                    null,
                    null,
                    null,
                    $"独立吞吐样本失败，状态码 {(int)response.StatusCode}。",
                    $"POST {path} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。{bodySample}",
                    bodySample,
                    requestId,
                    traceId);
            }

            var streamingOutcome = await ReadStreamingResponseAsync(
                response,
                stopwatch,
                transport.StreamContentParser,
                liveReporter,
                cancellationToken,
                transport.StreamDoneDetector);
            var observedOutputTokens = streamingOutcome.OutputTokenCount ?? 0;
            var stableThroughput = ResolveThroughputBenchmarkSpeed(streamingOutcome);
            var outputIsReliable = IsReliableThroughputOutput(
                observedOutputTokens,
                streamingOutcome.OutputCharacterCount,
                targetOutputTokens);
            var success = streamingOutcome.FirstTokenLatency.HasValue &&
                          stableThroughput.HasValue &&
                          outputIsReliable &&
                          (observedOutputTokens > 0 || streamingOutcome.OutputCharacterCount > 0);
            return new ProxyStreamingStabilityResult(
                DateTimeOffset.Now,
                baseUri.ToString(),
                settings.Model,
                success,
                streamingOutcome.ReceivedDone,
                targetOutputTokens,
                observedOutputTokens,
                success,
                streamingOutcome.ChunkCount,
                streamingOutcome.FirstTokenLatency,
                streamingOutcome.Duration,
                stableThroughput,
                streamingOutcome.EndToEndTokensPerSecond,
                streamingOutcome.OutputTokenCount,
                streamingOutcome.OutputTokenCountEstimated,
                streamingOutcome.OutputCharacterCount,
                streamingOutcome.MaxChunkGapMilliseconds,
                streamingOutcome.AverageChunkGapMilliseconds,
                success
                    ? $"独立吞吐样本完成：输出 {observedOutputTokens} token，首 token 后速度 {FormatThroughput(stableThroughput)}。"
                    : BuildThroughputSampleFailureSummary(observedOutputTokens, targetOutputTokens, stableThroughput),
                success ? null : BuildThroughputSampleFailureError(observedOutputTokens, targetOutputTokens, stableThroughput),
                streamingOutcome.Preview,
                requestId,
                traceId);
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            stopwatch.Stop();
            return new ProxyStreamingStabilityResult(
                DateTimeOffset.Now,
                baseUri.ToString(),
                settings.Model,
                false,
                false,
                targetOutputTokens,
                0,
                false,
                0,
                null,
                stopwatch.Elapsed,
                null,
                null,
                null,
                false,
                null,
                null,
                null,
                "独立吞吐样本执行失败。",
                ex.Message,
                null);
        }
    }

    private static ProxyStreamingStabilityResult BuildThroughputBenchmarkTimeoutSample(
        string baseUrl,
        string model,
        int targetOutputTokens,
        TimeSpan elapsed)
        => new(
            DateTimeOffset.Now,
            baseUrl,
            model,
            false,
            false,
            targetOutputTokens,
            0,
            false,
            0,
            null,
            elapsed,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            "独立吞吐样本超时。",
            $"等待首 token 超过 {elapsed.TotalSeconds:F1}s，已跳过本轮。",
            null);

    private static ProxyThroughputBenchmarkLiveProgress BuildThroughputBenchmarkLiveProgress(
        string baseUrl,
        string model,
        int requestedSampleCount,
        int segmentCount,
        IReadOnlyList<ProxyStreamingStabilityResult> completedSamples,
        int currentSampleIndex,
        StreamingReadLiveProgress liveSnapshot)
    {
        var completedSuccessfulSamples = completedSamples
            .Where(static sample => sample.Success && ResolveStableThroughput(sample).HasValue)
            .ToArray();
        var liveOutputSamples = completedSuccessfulSamples
            .Select(static sample => ResolveStableThroughput(sample)!.Value)
            .ToList();
        var liveThroughput = ResolveThroughputBenchmarkSpeed(liveSnapshot);
        if (liveThroughput.HasValue)
        {
            liveOutputSamples.Add(liveThroughput.Value);
        }

        var liveMedian = Median(liveOutputSamples);
        var liveAverage = liveOutputSamples.Count == 0 ? (double?)null : liveOutputSamples.Average();
        var liveMinimum = liveOutputSamples.Count == 0 ? (double?)null : liveOutputSamples.Min();
        var liveMaximum = liveOutputSamples.Count == 0 ? (double?)null : liveOutputSamples.Max();
        var currentTokenText = liveSnapshot.OutputTokenCount?.ToString() ?? "--";
        var currentSpeedText = FormatThroughput(liveThroughput);
        var samplingElapsed = ResolveThroughputSamplingElapsed(liveSnapshot);
        var phaseText = IsReliableThroughputOutput(
            liveSnapshot.OutputTokenCount ?? 0,
            liveSnapshot.OutputCharacterCount,
            segmentCount)
            ? "\u91C7\u6837\u4E2D"
            : "\u9884\u70ED\u4E2D";
        var summary = liveSnapshot.OutputTokensPerSecond.HasValue || liveSnapshot.OutputTokenCount.HasValue
            ? $"\u72EC\u7ACB\u541E\u5410{phaseText}\uFF1A\u7B2C {currentSampleIndex}/{requestedSampleCount} \u8F6E\uFF0C\u751F\u6210\u901F\u7387 {currentSpeedText}\uFF0C\u5DF2\u8F93\u51FA {currentTokenText}/{segmentCount} token\uFF0C\u751F\u6210\u7528\u65F6 {samplingElapsed.TotalSeconds:F1}s\u3002"
            : $"\u72EC\u7ACB\u541E\u5410\u8FDB\u884C\u4E2D\uFF1A\u7B2C {currentSampleIndex}/{requestedSampleCount} \u8F6E\u5DF2\u53D1\u9001\u8BF7\u6C42\uFF0C\u7B49\u5F85\u9996 token...";

        return new ProxyThroughputBenchmarkLiveProgress(
            DateTimeOffset.Now,
            baseUrl,
            model,
            requestedSampleCount,
            completedSamples.Count,
            completedSuccessfulSamples.Length,
            currentSampleIndex,
            segmentCount,
            samplingElapsed,
            liveSnapshot.OutputTokenCount,
            liveSnapshot.OutputTokenCountEstimated,
            liveThroughput,
            liveSnapshot.EndToEndTokensPerSecond,
            liveMedian,
            liveAverage,
            liveMinimum,
            liveMaximum,
            summary,
            liveSnapshot.Preview);
    }

    private static ProxyThroughputBenchmarkLiveProgress BuildCompletedThroughputBenchmarkLiveProgress(
        string baseUrl,
        string model,
        int requestedSampleCount,
        int segmentCount,
        IReadOnlyList<ProxyStreamingStabilityResult> completedSamples,
        int currentSampleIndex)
    {
        var completedSuccessfulSamples = completedSamples
            .Where(static sample => sample.Success && ResolveStableThroughput(sample).HasValue)
            .ToArray();
        var outputSamples = completedSuccessfulSamples
            .Select(static sample => ResolveStableThroughput(sample)!.Value)
            .ToArray();
        var currentSample = completedSamples[^1];
        var currentThroughput = ResolveStableThroughput(currentSample);
        var currentSampleElapsed = ResolveThroughputSamplingElapsed(currentSample);
        var currentTokenText = currentSample.OutputTokenCount?.ToString() ?? "--";
        var currentSpeedText = FormatThroughput(currentThroughput);
        var summary = currentSample.Success
            ? $"\u72EC\u7ACB\u541E\u5410\u8FDB\u884C\u4E2D\uFF1A\u7B2C {currentSampleIndex}/{requestedSampleCount} \u8F6E\u5B8C\u6210\uFF0C\u672C\u8F6E\u751F\u6210\u901F\u7387 {currentSpeedText}\uFF0C\u8F93\u51FA {currentTokenText} token\u3002"
            : $"\u72EC\u7ACB\u541E\u5410\u8FDB\u884C\u4E2D\uFF1A\u7B2C {currentSampleIndex}/{requestedSampleCount} \u8F6E\u5B8C\u6210\uFF0C\u672C\u8F6E\u6837\u672C\u504F\u77ED\u6216\u5F02\u5E38\u3002";

        return new ProxyThroughputBenchmarkLiveProgress(
            DateTimeOffset.Now,
            baseUrl,
            model,
            requestedSampleCount,
            completedSamples.Count,
            completedSuccessfulSamples.Length,
            currentSampleIndex,
            segmentCount,
            currentSampleElapsed,
            currentSample.OutputTokenCount,
            currentSample.OutputTokenCountEstimated,
            currentThroughput,
            currentSample.EndToEndTokensPerSecond,
            Median(outputSamples),
            outputSamples.Length == 0 ? (double?)null : outputSamples.Average(),
            outputSamples.Length == 0 ? (double?)null : outputSamples.Min(),
            outputSamples.Length == 0 ? (double?)null : outputSamples.Max(),
            summary,
            currentSample.Preview);
    }

    private static int ResolveThroughputBenchmarkTargetOutputTokens(int requestedSegmentCount)
    {
        if (requestedSegmentCount <= 0)
        {
            return ThroughputBenchmarkTargetOutputTokens;
        }

        if (requestedSegmentCount < ThroughputBenchmarkMinimumOutputTokens)
        {
            return Math.Clamp(
                requestedSegmentCount * 4,
                ThroughputBenchmarkShortTargetMinimumTokens,
                ThroughputBenchmarkShortTargetMaximumTokens);
        }

        return Math.Clamp(
            requestedSegmentCount,
            ThroughputBenchmarkMinimumOutputTokens,
            ThroughputBenchmarkMaximumOutputTokens);
    }

    private static TimeSpan ResolveThroughputBenchmarkSampleTimeout(
        int requestedTimeoutSeconds,
        int targetOutputTokens)
    {
        var requested = Math.Max(requestedTimeoutSeconds, 1);
        if (targetOutputTokens < ThroughputBenchmarkMinimumOutputTokens)
        {
            return TimeSpan.FromSeconds(Math.Clamp(requested, 5, 12));
        }

        var generationBudget = (int)Math.Ceiling(targetOutputTokens / 10d) + 30;
        return TimeSpan.FromSeconds(Math.Clamp(Math.Max(requested, generationBudget), 60, 180));
    }

    private static bool IsReliableThroughputOutput(
        int outputTokenCount,
        int outputCharacterCount,
        int targetOutputTokens)
        => outputTokenCount > 0 || outputCharacterCount > 0;

    private static double? ResolveStableThroughput(ProxyStreamingStabilityResult sample)
        => ResolveStableThroughput(sample.OutputTokensPerSecond, sample.EndToEndTokensPerSecond);

    private static double? ResolveStableThroughput(double? outputTokensPerSecond, double? endToEndTokensPerSecond)
        => outputTokensPerSecond ?? endToEndTokensPerSecond;

    private static double? ResolveThroughputBenchmarkSpeed(StreamingProbeOutcome streamingOutcome)
    {
        if (streamingOutcome.OutputTokenCount is > 0 &&
            streamingOutcome.GenerationDuration is { } generationDuration &&
            generationDuration > TimeSpan.Zero)
        {
            var calculated = streamingOutcome.OutputTokenCount.Value / generationDuration.TotalSeconds;
            if (IsPlausibleThroughputSample(calculated))
            {
                return calculated;
            }
        }

        return IsPlausibleThroughputSample(streamingOutcome.EndToEndTokensPerSecond)
            ? streamingOutcome.EndToEndTokensPerSecond
            : null;
    }

    private static double? ResolveThroughputBenchmarkSpeed(StreamingReadLiveProgress liveSnapshot)
    {
        if (IsPlausibleThroughputSample(liveSnapshot.OutputTokensPerSecond))
        {
            return liveSnapshot.OutputTokensPerSecond;
        }

        var samplingElapsed = ResolveThroughputSamplingElapsed(liveSnapshot);
        if (liveSnapshot.OutputTokenCount is > 0 && samplingElapsed >= TimeSpan.FromMilliseconds(750))
        {
            var calculated = liveSnapshot.OutputTokenCount.Value / samplingElapsed.TotalSeconds;
            if (IsPlausibleThroughputSample(calculated))
            {
                return calculated;
            }
        }

        return IsPlausibleThroughputSample(liveSnapshot.EndToEndTokensPerSecond)
            ? liveSnapshot.EndToEndTokensPerSecond
            : null;
    }

    private static bool IsPlausibleThroughputSample(double? value)
        => value is > 0 and <= ThroughputBenchmarkMaximumPlausibleTokensPerSecond &&
           !double.IsNaN(value.Value) &&
           !double.IsInfinity(value.Value);

    private static TimeSpan ResolveThroughputSamplingElapsed(StreamingReadLiveProgress liveSnapshot)
    {
        if (!liveSnapshot.FirstTokenLatency.HasValue)
        {
            return TimeSpan.Zero;
        }

        return liveSnapshot.Elapsed > liveSnapshot.FirstTokenLatency.Value
            ? liveSnapshot.Elapsed - liveSnapshot.FirstTokenLatency.Value
            : TimeSpan.Zero;
    }

    private static TimeSpan ResolveThroughputSamplingElapsed(ProxyStreamingStabilityResult sample)
    {
        if (!sample.FirstTokenLatency.HasValue || !sample.TotalDuration.HasValue)
        {
            return sample.TotalDuration ?? TimeSpan.Zero;
        }

        return sample.TotalDuration.Value > sample.FirstTokenLatency.Value
            ? sample.TotalDuration.Value - sample.FirstTokenLatency.Value
            : TimeSpan.Zero;
    }

    private static string BuildThroughputSampleFailureSummary(
        int observedOutputTokens,
        int targetOutputTokens,
        double? stableThroughput)
    {
        if (stableThroughput is null)
        {
            return "独立吞吐样本未拿到可用流式输出。";
        }

        return $"独立吞吐样本未纳入结果：输出 {observedOutputTokens}/{targetOutputTokens} token，速度样本不可用。";
    }

    private static string BuildThroughputSampleFailureError(
        int observedOutputTokens,
        int targetOutputTokens,
        double? stableThroughput)
    {
        if (stableThroughput is null)
        {
            return "未观察到首 token 或可用输出速度。";
        }

        return $"输出样本速度不可用：{observedOutputTokens}/{targetOutputTokens} token。";
    }

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values
            .OrderBy(static value => value)
            .ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static string FormatThroughput(double? value)
        => value.HasValue ? $"{value.Value:F1} tok/s" : "--";

    private static string FormatRange(double? minimum, double? maximum)
        => minimum.HasValue && maximum.HasValue
            ? $"{minimum.Value:F1}-{maximum.Value:F1} tok/s"
            : "--";
}
