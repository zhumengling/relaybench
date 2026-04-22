using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    public async Task<ProxyThroughputBenchmarkResult> RunThroughputBenchmarkAsync(
        ProxyEndpointSettings settings,
        int requestedSampleCount = 3,
        int requestedSegmentCount = 40,
        IProgress<ProxyThroughputBenchmarkLiveProgress>? liveProgress = null,
        CancellationToken cancellationToken = default)
    {
        var sampleCount = Math.Clamp(requestedSampleCount, 1, 3);
        var segmentCount = Math.Clamp(requestedSegmentCount, 24, 120);

        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return new ProxyThroughputBenchmarkResult(
                DateTimeOffset.Now,
                settings.BaseUrl,
                settings.Model,
                sampleCount,
                0,
                0,
                segmentCount,
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

        List<ProxyStreamingStabilityResult> samples = [];
        for (var index = 0; index < sampleCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSampleIndex = index + 1;
            var sample = await RunLongStreamingTestCoreAsync(
                normalizedSettings,
                segmentCount,
                liveSnapshot =>
                {
                    liveProgress?.Report(BuildThroughputBenchmarkLiveProgress(
                        baseUri.ToString(),
                        normalizedSettings.Model,
                        sampleCount,
                        segmentCount,
                        samples,
                        currentSampleIndex,
                        liveSnapshot));
                },
                cancellationToken);
            samples.Add(sample);
            liveProgress?.Report(BuildCompletedThroughputBenchmarkLiveProgress(
                baseUri.ToString(),
                normalizedSettings.Model,
                sampleCount,
                segmentCount,
                samples,
                currentSampleIndex));
        }

        var successfulSamples = samples
            .Where(static sample => sample.Success && sample.OutputTokensPerSecond.HasValue)
            .ToArray();
        var outputSamples = successfulSamples
            .Select(static sample => sample.OutputTokensPerSecond)
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
            ? $"独立吞吐测试未通过：0/{sampleCount} 轮成功。"
            : $"独立吞吐测试：{successfulSamples.Length}/{sampleCount} 轮成功，中位数 {FormatThroughput(medianOutput)}，区间 {FormatRange(minimumOutput, maximumOutput)}。";

        var errorMessage = successfulSamples.Length == 0
            ? samples.Select(static sample => sample.Error).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
              "独立吞吐测试未拿到可用样本。"
            : null;

        return new ProxyThroughputBenchmarkResult(
            DateTimeOffset.Now,
            baseUri.ToString(),
            normalizedSettings.Model,
            sampleCount,
            samples.Count,
            successfulSamples.Length,
            segmentCount,
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
            .Where(static sample => sample.Success && sample.OutputTokensPerSecond.HasValue)
            .ToArray();
        var liveOutputSamples = completedSuccessfulSamples
            .Select(static sample => sample.OutputTokensPerSecond!.Value)
            .ToList();
        if (liveSnapshot.OutputTokensPerSecond.HasValue)
        {
            liveOutputSamples.Add(liveSnapshot.OutputTokensPerSecond.Value);
        }

        var liveMedian = Median(liveOutputSamples);
        var liveAverage = liveOutputSamples.Count == 0 ? (double?)null : liveOutputSamples.Average();
        var liveMinimum = liveOutputSamples.Count == 0 ? (double?)null : liveOutputSamples.Min();
        var liveMaximum = liveOutputSamples.Count == 0 ? (double?)null : liveOutputSamples.Max();
        var currentTokenText = liveSnapshot.OutputTokenCount?.ToString() ?? "--";
        var currentSpeedText = FormatThroughput(liveSnapshot.OutputTokensPerSecond);
        var summary = liveSnapshot.OutputTokensPerSecond.HasValue || liveSnapshot.OutputTokenCount.HasValue
            ? $"\u72EC\u7ACB\u541E\u5410\u8FDB\u884C\u4E2D\uFF1A\u7B2C {currentSampleIndex}/{requestedSampleCount} \u8F6E\uFF0C\u5F53\u524D {currentSpeedText}\uFF0C\u5DF2\u8F93\u51FA {currentTokenText} token\uFF0C\u7528\u65F6 {liveSnapshot.Elapsed.TotalSeconds:F1}s\u3002"
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
            liveSnapshot.Elapsed,
            liveSnapshot.OutputTokenCount,
            liveSnapshot.OutputTokenCountEstimated,
            liveSnapshot.OutputTokensPerSecond,
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
            .Where(static sample => sample.Success && sample.OutputTokensPerSecond.HasValue)
            .ToArray();
        var outputSamples = completedSuccessfulSamples
            .Select(static sample => sample.OutputTokensPerSecond!.Value)
            .ToArray();
        var currentSample = completedSamples[^1];
        var currentTokenText = currentSample.OutputTokenCount?.ToString() ?? "--";
        var currentSpeedText = FormatThroughput(currentSample.OutputTokensPerSecond);
        var summary = currentSample.Success
            ? $"\u72EC\u7ACB\u541E\u5410\u8FDB\u884C\u4E2D\uFF1A\u7B2C {currentSampleIndex}/{requestedSampleCount} \u8F6E\u5B8C\u6210\uFF0C\u672C\u8F6E {currentSpeedText}\uFF0C\u8F93\u51FA {currentTokenText} token\u3002"
            : $"\u72EC\u7ACB\u541E\u5410\u8FDB\u884C\u4E2D\uFF1A\u7B2C {currentSampleIndex}/{requestedSampleCount} \u8F6E\u5B8C\u6210\uFF0C\u672C\u8F6E\u5F02\u5E38\u3002";

        return new ProxyThroughputBenchmarkLiveProgress(
            DateTimeOffset.Now,
            baseUrl,
            model,
            requestedSampleCount,
            completedSamples.Count,
            completedSuccessfulSamples.Length,
            currentSampleIndex,
            segmentCount,
            currentSample.TotalDuration ?? TimeSpan.Zero,
            currentSample.OutputTokenCount,
            currentSample.OutputTokenCountEstimated,
            currentSample.OutputTokensPerSecond,
            currentSample.EndToEndTokensPerSecond,
            Median(outputSamples),
            outputSamples.Length == 0 ? (double?)null : outputSamples.Average(),
            outputSamples.Length == 0 ? (double?)null : outputSamples.Min(),
            outputSamples.Length == 0 ? (double?)null : outputSamples.Max(),
            summary,
            currentSample.Preview);
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
