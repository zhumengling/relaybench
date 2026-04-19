using System.Text.RegularExpressions;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static readonly Regex LongStreamSegmentRegex = new(@"\[(?<segment>\d{3})\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ProxyStreamingStabilityResult> RunLongStreamingTestAsync(
        ProxyEndpointSettings settings,
        int requestedSegmentCount,
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
        var chatPath = BuildApiPath(baseUri, "chat/completions");
        var segmentCount = Math.Clamp(requestedSegmentCount, 24, 240);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, chatPath)
            {
                Content = new StringContent(BuildLongStreamingPayload(normalizedSettings.Model, segmentCount), System.Text.Encoding.UTF8, "application/json")
            };

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
                    $"POST {chatPath} 返回 {(int)response.StatusCode} {response.ReasonPhrase}。{bodySample}",
                    bodySample,
                    requestId,
                    traceId);
            }

            var streamingOutcome = await ReadStreamingResponseAsync(response, System.Diagnostics.Stopwatch.StartNew(), TryParseChatStreamContent, cancellationToken);
            var observedSegments = LongStreamSegmentRegex.Matches(streamingOutcome.FullText)
                .Select(static match => int.TryParse(match.Groups["segment"].Value, out var parsed) ? parsed : -1)
                .Where(static value => value > 0)
                .ToArray();
            var expectedSequence = Enumerable.Range(1, segmentCount).ToArray();
            var sequenceIntegrityPassed = observedSegments.Length >= segmentCount &&
                                          observedSegments.Take(segmentCount).SequenceEqual(expectedSequence);
            var actualSegmentCount = observedSegments.Length;
            var success = streamingOutcome.ReceivedDone && sequenceIntegrityPassed && actualSegmentCount >= segmentCount;

            var summary =
                success
                    ? $"长流稳定简测通过：{actualSegmentCount}/{segmentCount} 段，DONE 正常，流速 {FormatStreamingSpeed(streamingOutcome.OutputTokensPerSecond)}。"
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
}
