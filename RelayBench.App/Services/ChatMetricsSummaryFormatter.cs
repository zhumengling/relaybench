using RelayBench.Core.Models;

namespace RelayBench.App.Services;

public static class ChatMetricsSummaryFormatter
{
    public static string Format(ChatMessageMetrics metrics)
    {
        var ttft = metrics.FirstTokenLatency is null
            ? "TTFT --"
            : $"TTFT {metrics.FirstTokenLatency.Value.TotalMilliseconds:F0} ms";
        return $"{metrics.WireApi} | {metrics.Elapsed.TotalMilliseconds:F0} ms | {ttft} | {FormatOutputThroughput(metrics)}";
    }

    private static string FormatOutputThroughput(ChatMessageMetrics metrics)
    {
        if (metrics.OutputTokenCount > 0 || metrics.TokensPerSecond.HasValue)
        {
            var count = metrics.OutputTokenCount > 0
                ? $"{metrics.OutputTokenCount} tok"
                : "-- tok";
            var speed = metrics.TokensPerSecond is null
                ? "-- tok/s"
                : $"{metrics.TokensPerSecond.Value:F1} tok/s";
            return $"{count} | {speed}";
        }

        return metrics.CharactersPerSecond is null
            ? "-- chars/s"
            : $"{metrics.CharactersPerSecond.Value:F1} chars/s";
    }
}
