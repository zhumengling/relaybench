using System.Text.RegularExpressions;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static readonly Regex LongStreamSegmentRegex = new(@"\[(?<segment>\d{3})\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ProxyStreamingStabilityResult> RunLongStreamingTestAsync(
        ProxyEndpointSettings settings,
        int requestedSegmentCount,
        CancellationToken cancellationToken = default)
        => await RunLongStreamingTestCoreAsync(settings, requestedSegmentCount, liveReporter: null, cancellationToken);

    private async Task<ProxyStreamingStabilityResult> RunLongStreamingTestCoreAsync(
        ProxyEndpointSettings settings,
        int requestedSegmentCount,
        Action<StreamingReadLiveProgress>? liveReporter,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return new ProxyStreamingStabilityResult(
                DateTimeOffset.Now,
                settings.BaseUrl,
                settings.Model,
                false,
                false,
                requestedSegmentCount,
                0,
                false,
                0,
                null,
                null,
                null,
                null,
                null,
                false,
                null,
                null,
                null,
                "长流稳定简测参数校验失败。",
                error,
                null);
        }

        using var client = CreateClient(baseUri, normalizedSettings);
        var transport = await ResolveConversationProbeTransportAsync(
            client,
            baseUri,
            normalizedSettings.Model,
            baselineResult: null,
            cancellationToken);
        var segmentCount = Math.Clamp(requestedSegmentCount, 24, 240);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, transport.Path)
            {
                Content = new StringContent(
                    BuildConversationWirePayload(transport.WireApi, BuildLongStreamingPayload(normalizedSettings.Model, segmentCount)),
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
                return new ProxyStreamingStabilityResult(
                    DateTimeOffset.Now,
                    baseUri.ToString(),
                    normalizedSettings.Model,
                    false,
                    false,
                    segmentCount,
                    0,
                    false,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    null,
                    null,
                    null,
                    $"长流稳定简测失败，状态码 {(int)response.StatusCode}。",
                    $"POST {transport.Path} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。{bodySample}",
                    bodySample,
                    requestId,
                    traceId);
            }

            var streamingOutcome = await ReadStreamingResponseAsync(
                response,
                System.Diagnostics.Stopwatch.StartNew(),
                transport.StreamContentParser,
                liveReporter,
                cancellationToken,
                transport.StreamDoneDetector);
            var observedSegments = LongStreamSegmentRegex.Matches(streamingOutcome.FullText)
                .Select(static match => int.TryParse(match.Groups["segment"].Value, out var parsed) ? parsed : -1)
                .Where(static value => value > 0)
                .ToArray();
            var expectedSequence = Enumerable.Range(1, segmentCount).ToArray();
            var sequenceIntegrityPassed = observedSegments.Length >= segmentCount &&
                                          observedSegments.Take(segmentCount).SequenceEqual(expectedSequence);
            var practicalSequenceIntegrityPassed = sequenceIntegrityPassed ||
                                                   HasPracticalLongStreamSequenceIntegrity(observedSegments, segmentCount);
            var actualSegmentCount = observedSegments.Length;
            var success = streamingOutcome.ReceivedDone && practicalSequenceIntegrityPassed;

            var summary =
                success
                    ? $"长流稳定简测通过：{actualSegmentCount}/{segmentCount} 段，DONE 正常，严格顺序={(sequenceIntegrityPassed ? "通过" : "轻微偏差")}，流速 {FormatStreamingSpeed(streamingOutcome.OutputTokensPerSecond)}。"
                    : $"长流稳定简测未通过：实际 {actualSegmentCount}/{segmentCount} 段，DONE={(streamingOutcome.ReceivedDone ? "是" : "否")}，顺序校验={(sequenceIntegrityPassed ? "通过" : "失败")}。";

            return new ProxyStreamingStabilityResult(
                DateTimeOffset.Now,
                baseUri.ToString(),
                normalizedSettings.Model,
                success,
                streamingOutcome.ReceivedDone,
                segmentCount,
                actualSegmentCount,
                sequenceIntegrityPassed,
                streamingOutcome.ChunkCount,
                streamingOutcome.FirstTokenLatency,
                streamingOutcome.Duration,
                streamingOutcome.OutputTokensPerSecond,
                streamingOutcome.EndToEndTokensPerSecond,
                streamingOutcome.OutputTokenCount,
                streamingOutcome.OutputTokenCountEstimated,
                streamingOutcome.OutputCharacterCount,
                streamingOutcome.MaxChunkGapMilliseconds,
                streamingOutcome.AverageChunkGapMilliseconds,
                summary,
                success ? null : "长流输出的段数或结束标记异常。",
                streamingOutcome.Preview,
                requestId,
                traceId);
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            return new ProxyStreamingStabilityResult(
                DateTimeOffset.Now,
                baseUri.ToString(),
                normalizedSettings.Model,
                false,
                false,
                segmentCount,
                0,
                false,
                0,
                null,
                null,
                null,
                null,
                null,
                false,
                null,
                null,
                null,
                "长流稳定简测执行失败。",
                ex.Message,
                null);
        }
    }

    private static string FormatStreamingSpeed(double? value)
        => value is null ? "--" : $"{value:F1} tok/s";

    private static bool HasPracticalLongStreamSequenceIntegrity(
        IReadOnlyList<int> observedSegments,
        int expectedSegmentCount)
    {
        if (expectedSegmentCount <= 0 || observedSegments.Count == 0)
        {
            return false;
        }

        var inRangeSegments = observedSegments
            .Where(segment => segment >= 1 && segment <= expectedSegmentCount)
            .ToArray();
        if (inRangeSegments.Length == 0)
        {
            return false;
        }

        var requiredCount = (int)Math.Ceiling(expectedSegmentCount * 0.90d);
        var distinctCoverage = inRangeSegments.Distinct().Count();
        if (distinctCoverage < requiredCount || inRangeSegments.Length < requiredCount)
        {
            return false;
        }

        for (var index = 1; index < inRangeSegments.Length; index++)
        {
            if (inRangeSegments[index] < inRangeSegments[index - 1])
            {
                return false;
            }
        }

        return true;
    }
}
