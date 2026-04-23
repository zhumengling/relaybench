using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static async Task<ProxyProbeScenarioResult> SampleStreamingThroughputAsync(
        HttpClient client,
        string path,
        string payload,
        ProxyProbeScenarioResult primaryResult,
        string displayName,
        Func<string, string?> streamContentParser,
        Func<string?, bool>? semanticMatcher,
        int requestedSampleCount,
        CancellationToken cancellationToken)
    {
        var clampedSampleCount = Math.Clamp(requestedSampleCount, 1, 3);
        if (clampedSampleCount <= 1 || !primaryResult.Success)
        {
            return primaryResult;
        }

        List<ProxyProbeScenarioResult> samples = [primaryResult];
        for (var sampleIndex = 1; sampleIndex < clampedSampleCount; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sampleResult = await ProbeStreamingScenarioAsync(
                client,
                path,
                payload,
                ProxyProbeScenarioKind.ChatCompletionsStream,
                displayName,
                streamContentParser,
                semanticMatcher,
                cancellationToken);
            samples.Add(sampleResult);
        }

        return AverageStreamingThroughputSamples(primaryResult, samples);
    }

    private static ProxyProbeScenarioResult AverageStreamingThroughputSamples(
        ProxyProbeScenarioResult primaryResult,
        IReadOnlyList<ProxyProbeScenarioResult> sampleResults)
    {
        var validSamples = sampleResults
            .Where(static result =>
                result.Success &&
                (result.OutputTokensPerSecond.HasValue || result.EndToEndTokensPerSecond.HasValue))
            .ToArray();

        if (validSamples.Length == 0)
        {
            return primaryResult;
        }

        var averagedOutputTokensPerSecond = AverageNullable(validSamples.Select(static result => result.OutputTokensPerSecond));
        var averagedEndToEndTokensPerSecond = AverageNullable(validSamples.Select(static result => result.EndToEndTokensPerSecond));

        return primaryResult with
        {
            OutputTokensPerSecond = averagedOutputTokensPerSecond ?? primaryResult.OutputTokensPerSecond,
            EndToEndTokensPerSecond = averagedEndToEndTokensPerSecond ?? primaryResult.EndToEndTokensPerSecond,
            OutputTokensPerSecondSampleCount = validSamples.Length
        };
    }

    private static double? AverageNullable(IEnumerable<double?> values)
    {
        var materialized = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();

        if (materialized.Length == 0)
        {
            return null;
        }

        return materialized.Average();
    }
}
