using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static double Rate(int successCount, int totalCount)
        => totalCount == 0 ? 0 : successCount * 100d / totalCount;

    private static TimeSpan? AverageTimeSpan(IEnumerable<TimeSpan> values)
    {
        var list = values.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var averageMilliseconds = list.Average(value => value.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(averageMilliseconds);
    }

    private static int CalculateHealthScore(
        double fullSuccessRate,
        double streamSuccessRate,
        TimeSpan? averageChatLatency,
        TimeSpan? averageTtft,
        int maxConsecutiveFailures)
    {
        var chatLatencyScore = ScoreLatency(averageChatLatency, 600, 6_000);
        var ttftScore = ScoreLatency(averageTtft, 300, 4_000);
        var failurePenalty = Math.Min(maxConsecutiveFailures * 8, 32);

        var rawScore =
            (fullSuccessRate * 0.5) +
            (streamSuccessRate * 0.2) +
            (chatLatencyScore * 0.15) +
            (ttftScore * 0.15) -
            failurePenalty;

        return (int)Math.Clamp(Math.Round(rawScore), 0, 100);
    }

    private static double ScoreLatency(TimeSpan? latency, double bestMilliseconds, double worstMilliseconds)
    {
        if (latency is null)
        {
            return 0;
        }

        var value = latency.Value.TotalMilliseconds;
        if (value <= bestMilliseconds)
        {
            return 100;
        }

        if (value >= worstMilliseconds)
        {
            return 0;
        }

        var ratio = (value - bestMilliseconds) / (worstMilliseconds - bestMilliseconds);
        return 100 - (ratio * 100);
    }

    private static string LabelHealth(int healthScore)
        => healthScore switch
        {
            >= 85 => "很稳",
            >= 70 => "稳定",
            >= 50 => "一般",
            >= 30 => "波动",
            _ => "不稳定"
        };

    private sealed record EdgeObservation(
        string? CdnProvider,
        string EdgeSignature,
        string CdnSummary);
}
