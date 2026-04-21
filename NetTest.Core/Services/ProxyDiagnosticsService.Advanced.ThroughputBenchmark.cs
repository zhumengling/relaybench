using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    public async Task<ProxyThroughputBenchmarkResult> RunThroughputBenchmarkAsync(
        ProxyEndpointSettings settings,
        int requestedSampleCount = 3,
        int requestedSegmentCount = 40,
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
            samples.Add(await RunLongStreamingTestAsync(normalizedSettings, segmentCount, cancellationToken));
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
