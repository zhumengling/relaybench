using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static async Task<StreamingProbeOutcome> ReadStreamingResponseAsync(
        HttpResponseMessage response,
        Stopwatch stopwatch,
        Func<string, string?> streamContentParser,
        Action<StreamingReadLiveProgress>? liveReporter,
        CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        StringBuilder previewBuilder = new();
        StringBuilder fullTextBuilder = new();
        TimeSpan? firstTokenLatency = null;
        var chunkCount = 0;
        var receivedDone = false;
        var outputTokenCount = (int?)null;
        var lastChunkElapsed = (TimeSpan?)null;
        List<double> chunkGaps = [];
        object syncRoot = new();
        var reportingCompleted = 0;

        void ReportLiveSnapshot()
        {
            if (liveReporter is null)
            {
                return;
            }

            string previewText;
            string fullText;
            TimeSpan? currentFirstTokenLatency;
            int currentChunkCount;
            bool currentReceivedDone;
            int? currentOutputTokenCount;

            lock (syncRoot)
            {
                previewText = previewBuilder.ToString();
                fullText = fullTextBuilder.ToString();
                currentFirstTokenLatency = firstTokenLatency;
                currentChunkCount = chunkCount;
                currentReceivedDone = receivedDone;
                currentOutputTokenCount = outputTokenCount;
            }

            var elapsedNow = stopwatch.Elapsed;
            var generationDuration = currentFirstTokenLatency.HasValue && elapsedNow > currentFirstTokenLatency.Value
                ? elapsedNow - currentFirstTokenLatency.Value
                : currentFirstTokenLatency.HasValue
                    ? TimeSpan.Zero
                    : (TimeSpan?)null;
            var outputMetrics = BuildOutputMetrics(fullText, currentOutputTokenCount, elapsedNow, generationDuration);

            liveReporter(new StreamingReadLiveProgress(
                elapsedNow,
                currentFirstTokenLatency,
                previewText,
                fullText,
                currentChunkCount,
                currentReceivedDone,
                outputMetrics.OutputTokenCount,
                outputMetrics.OutputTokenCountEstimated,
                outputMetrics.OutputCharacterCount ?? 0,
                outputMetrics.OutputTokensPerSecond,
                outputMetrics.EndToEndTokensPerSecond));
        }

        using var liveReportCancellation = liveReporter is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? liveReportTask = null;
        if (liveReporter is not null)
        {
            ReportLiveSnapshot();
            liveReportTask = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
                try
                {
                    while (await timer.WaitForNextTickAsync(liveReportCancellation!.Token))
                    {
                        ReportLiveSnapshot();
                        if (Volatile.Read(ref reportingCompleted) == 1)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (liveReportCancellation!.IsCancellationRequested)
                {
                }
            }, CancellationToken.None);
        }

        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var payload = line[5..].Trim();
                if (payload == "[DONE]")
                {
                    lock (syncRoot)
                    {
                        receivedDone = true;
                    }
                    break;
                }

                var currentElapsed = stopwatch.Elapsed;
                string? deltaContent = null;
                try
                {
                    deltaContent = streamContentParser(payload);
                }
                catch
                {
                    deltaContent = null;
                }

                lock (syncRoot)
                {
                    chunkCount++;
                    if (lastChunkElapsed.HasValue)
                    {
                        chunkGaps.Add((currentElapsed - lastChunkElapsed.Value).TotalMilliseconds);
                    }

                    lastChunkElapsed = currentElapsed;
                    outputTokenCount ??= TryExtractOutputTokenCount(payload);

                    if (deltaContent is not null)
                    {
                        if (firstTokenLatency is null)
                        {
                            firstTokenLatency = currentElapsed;
                        }

                        if (previewBuilder.Length < 240)
                        {
                            previewBuilder.Append(deltaContent);
                        }

                        if (fullTextBuilder.Length < 64_000)
                        {
                            var remaining = 64_000 - fullTextBuilder.Length;
                            if (deltaContent.Length <= remaining)
                            {
                                fullTextBuilder.Append(deltaContent);
                            }
                            else
                            {
                                fullTextBuilder.Append(deltaContent[..remaining]);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref reportingCompleted, 1);
            if (liveReportCancellation is not null)
            {
                liveReportCancellation.Cancel();
            }

            if (liveReportTask is not null)
            {
                try
                {
                    await liveReportTask;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
        }

        stopwatch.Stop();
        ReportLiveSnapshot();
        var fullText = fullTextBuilder.ToString();
        var generationDuration = firstTokenLatency.HasValue && stopwatch.Elapsed > firstTokenLatency.Value
            ? stopwatch.Elapsed - firstTokenLatency.Value
            : firstTokenLatency.HasValue
                ? TimeSpan.Zero
                : (TimeSpan?)null;
        var outputMetrics = BuildOutputMetrics(fullText, outputTokenCount, stopwatch.Elapsed, generationDuration);

        return new StreamingProbeOutcome(
            firstTokenLatency,
            stopwatch.Elapsed,
            previewBuilder.ToString(),
            fullText,
            chunkCount,
            receivedDone,
            outputMetrics.OutputTokenCount,
            outputMetrics.OutputTokenCountEstimated,
            outputMetrics.OutputCharacterCount ?? 0,
            outputMetrics.GenerationDuration,
            outputMetrics.OutputTokensPerSecond,
            outputMetrics.EndToEndTokensPerSecond,
            chunkGaps.Count == 0 ? null : chunkGaps.Max(),
            chunkGaps.Count == 0 ? null : chunkGaps.Average());
    }

    private static IReadOnlyList<string> ExtractInterestingHeaders(HttpResponseMessage response)
    {
        List<string> lines = [];

        foreach (var headerName in InterestingHeaderNames)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                lines.Add($"{headerName}: {string.Join(", ", values)}");
                continue;
            }

            if (response.Content.Headers.TryGetValues(headerName, out values))
            {
                lines.Add($"{headerName}: {string.Join(", ", values)}");
            }
        }

        return lines;
    }

    private static string ExtractBodySample(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "未返回响应体。";
        }

        var normalized = content
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Trim();

        return normalized.Length <= 220 ? normalized : normalized[..220] + "...";
    }

    private static string? BuildLooseSuccessPreview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var preview =
                TryExtractResponsesText(document.RootElement) ??
                TryExtractChatLikeText(document.RootElement);

            if (!string.IsNullOrWhiteSpace(preview))
            {
                return preview;
            }
        }
        catch
        {
        }

        var sample = ExtractBodySample(content);
        return string.Equals(sample, "未返回响应体。", StringComparison.Ordinal)
            ? null
            : sample;
    }

    private static string? TryExtractChatLikeText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }

                if (message.TryGetProperty("content", out content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            return item.GetString();
                        }

                        if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                        {
                            return textElement.GetString();
                        }
                    }
                }
            }
        }

        return null;
    }

    private static bool MatchProbeExpectation(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return false;
        }

        var normalized = new string(preview
            .Where(character => char.IsLetterOrDigit(character))
            .Select(char.ToLowerInvariant)
            .ToArray());

        return normalized.Contains("proxyok", StringComparison.Ordinal);
    }

}
