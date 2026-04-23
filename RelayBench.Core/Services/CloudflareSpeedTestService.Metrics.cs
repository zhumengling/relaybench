using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class CloudflareSpeedTestService
{
    private static double? Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sortedValues = values.OrderBy(static value => value).ToArray();
        var index = (sortedValues.Length - 1) * percentile;
        var remainder = index % 1;

        if (remainder == 0)
        {
            return sortedValues[(int)Math.Round(index)];
        }

        var lower = sortedValues[(int)Math.Floor(index)];
        var upper = sortedValues[(int)Math.Ceiling(index)];
        return lower + ((upper - lower) * remainder);
    }

    private static double? CalculateJitter(IReadOnlyList<double> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var totalDelta = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            totalDelta += Math.Abs(points[index] - points[index - 1]);
        }

        return totalDelta / (points.Count - 1);
    }

    private static double? ComputeLoadedLatencyIncrease(
        double? idleLatency,
        double? downloadLoadedLatency,
        double? uploadLoadedLatency)
    {
        if (idleLatency is null)
        {
            return null;
        }

        var loadedLatency = new[] { downloadLoadedLatency, uploadLoadedLatency }
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty(double.NaN)
            .Max();

        return double.IsNaN(loadedLatency) ? null : Math.Max(0, loadedLatency - idleLatency.Value);
    }

    private static int ComputeGptImpactScore(
        double? idleLatency,
        double? idleJitter,
        double? packetLossRatio,
        double? downloadBitsPerSecond,
        double? uploadBitsPerSecond,
        double? loadedLatencyIncrease)
    {
        var score = 100;
        score -= PenaltyFromThresholds(idleLatency, [20, 50, 100, 200, 500], [0, 8, 20, 35, 55, 70]);
        score -= PenaltyFromThresholds(idleJitter, [10, 20, 50, 100], [0, 5, 15, 25, 35]);
        score -= PenaltyFromThresholds(packetLossRatio, [0.005, 0.01, 0.03, 0.05], [0, 6, 16, 28, 40]);
        score -= PenaltyFromThresholds(loadedLatencyIncrease, [20, 50, 100, 250], [0, 8, 18, 30, 40]);
        score -= RewardPenaltyFromMinimum(downloadBitsPerSecond, [50e6, 20e6, 5e6, 1e6], [0, 5, 15, 25, 35]);
        score -= RewardPenaltyFromMinimum(uploadBitsPerSecond, [10e6, 5e6, 2e6, 0.5e6], [0, 4, 10, 20, 30]);
        return (int)Math.Clamp(score, 0, 100);
    }

    private static int PenaltyFromThresholds(double? value, IReadOnlyList<double> thresholds, IReadOnlyList<int> penalties)
    {
        if (value is null)
        {
            return penalties[^1];
        }

        for (var index = 0; index < thresholds.Count; index++)
        {
            if (value.Value <= thresholds[index])
            {
                return penalties[index];
            }
        }

        return penalties[^1];
    }

    private static int RewardPenaltyFromMinimum(double? value, IReadOnlyList<double> minimums, IReadOnlyList<int> penalties)
    {
        if (value is null)
        {
            return penalties[^1];
        }

        for (var index = 0; index < minimums.Count; index++)
        {
            if (value.Value >= minimums[index])
            {
                return penalties[index];
            }
        }

        return penalties[^1];
    }

    private static string LabelGptImpact(int score)
        => score switch
        {
            >= 85 => "优秀",
            >= 70 => "良好",
            >= 50 => "一般",
            >= 30 => "较弱",
            _ => "较差"
        };

    private static string FormatBandwidth(double? bitsPerSecond)
    {
        if (bitsPerSecond is null)
        {
            return "--";
        }

        return bitsPerSecond.Value switch
        {
            >= 1_000_000_000 => $"{bitsPerSecond.Value / 1_000_000_000d:F2} Gbps",
            >= 1_000_000 => $"{bitsPerSecond.Value / 1_000_000d:F1} Mbps",
            >= 1_000 => $"{bitsPerSecond.Value / 1_000d:F1} Kbps",
            _ => $"{bitsPerSecond.Value:F0} bps"
        };
    }

    private static string FormatMs(double? milliseconds)
        => milliseconds is null ? "--" : $"{milliseconds.Value:F1} ms";

    private static string FormatRatio(double? value)
        => value is null ? "--" : $"{value.Value * 100:F1}%";

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            >= 1_000_000_000 => $"{bytes / 1_000_000_000d:F2} GB",
            >= 1_000_000 => $"{bytes / 1_000_000d:F1} MB",
            >= 1_000 => $"{bytes / 1_000d:F1} KB",
            _ => $"{bytes} B"
        };

}
