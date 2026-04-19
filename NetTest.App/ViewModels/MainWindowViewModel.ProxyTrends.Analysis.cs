using System.Text;
using System.Windows.Media.Imaging;
using NetTest.App.Infrastructure;
using NetTest.App.Services;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string ResolveProxyTrendTarget(string? requestedBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(requestedBaseUrl))
        {
            return requestedBaseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ProxyBaseUrl))
        {
            return ProxyBaseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_lastProxyStabilityResult?.BaseUrl))
        {
            return _lastProxyStabilityResult.BaseUrl;
        }

        return _lastProxySingleResult?.BaseUrl ?? string.Empty;
    }

    private string ResolvePreferredTrendTarget(IReadOnlyList<ProxyBatchProbeRow> rows)
    {
        if (!string.IsNullOrWhiteSpace(ProxyBaseUrl) &&
            rows.Any(row => string.Equals(
                ProxyTrendStore.NormalizeBaseUrl(row.Result.BaseUrl),
                ProxyTrendStore.NormalizeBaseUrl(ProxyBaseUrl),
                StringComparison.OrdinalIgnoreCase)))
        {
            return ProxyBaseUrl;
        }

        return rows
            .OrderByDescending(ResolveBatchPassedCapabilityCount)
            .ThenBy(row => row.Result.ChatLatency ?? TimeSpan.MaxValue)
            .ThenBy(row => row.Result.StreamFirstTokenLatency ?? TimeSpan.MaxValue)
            .Select(row => row.Result.BaseUrl)
            .FirstOrDefault() ?? string.Empty;
    }

    private ProxyTrendComparisonResult AnalyzeProxyTrend(IReadOnlyList<ProxyTrendEntry> records)
    {
        if (records.Count <= 1)
        {
            return new ProxyTrendComparisonResult(
                "样本不足，暂时无法判断近 24 小时变化。",
                null,
                null,
                null,
                null,
                null,
                null);
        }

        var ordered = records.OrderBy(record => record.Timestamp).ToArray();
        var splitIndex = Math.Max(1, ordered.Length / 2);
        var earlier = ordered.Take(splitIndex).ToArray();
        var later = ordered.Skip(splitIndex).ToArray();
        if (later.Length == 0)
        {
            later = [ordered[^1]];
        }

        var earlierSuccess = Average(earlier.Select(record => (double?)ResolveSuccessRate(record)));
        var laterSuccess = Average(later.Select(record => (double?)ResolveSuccessRate(record)));
        var earlierChatLatency = Average(earlier.Select(record => record.ChatLatencyMs));
        var laterChatLatency = Average(later.Select(record => record.ChatLatencyMs));
        var earlierTtft = Average(earlier.Select(record => record.StreamFirstTokenLatencyMs));
        var laterTtft = Average(later.Select(record => record.StreamFirstTokenLatencyMs));

        var summary =
            $"稳定性{DescribeDelta(laterSuccess, earlierSuccess, "pct", betterWhenHigher: true)}；" +
            $"普通延迟 {DescribeDelta(laterChatLatency, earlierChatLatency, "ms", betterWhenHigher: false)}；" +
            $"TTFT {DescribeDelta(laterTtft, earlierTtft, "ms", betterWhenHigher: false)}；" +
            $"结论以更低延迟、更低 TTFT 为优。";

        return new ProxyTrendComparisonResult(
            summary,
            earlierSuccess,
            laterSuccess,
            earlierChatLatency,
            laterChatLatency,
            earlierTtft,
            laterTtft);
    }

    private static string DescribeDelta(double? current, double? baseline, string unit, bool betterWhenHigher)
    {
        if (!current.HasValue || !baseline.HasValue)
        {
            return "样本不足";
        }

        var delta = current.Value - baseline.Value;
        var absolute = Math.Abs(delta);
        var threshold = unit switch
        {
            "pct" => 5d,
            "ms" => 120d,
            _ => 4d
        };

        if (absolute < threshold)
        {
            return "基本持平";
        }

        var improved = betterWhenHigher ? delta > 0 : delta < 0;
        var direction = improved ? "改善" : "走弱";
        var valueText = unit switch
        {
            "pct" => $"{absolute:F1} 个百分点",
            "ms" => $"{absolute:F0} ms",
            _ => $"{absolute:F0}"
        };

        return $"{direction} {valueText}";
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var filtered = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return filtered.Length == 0 ? null : filtered.Average();
    }

    private static string FormatMillisecondsValue(double? value)
        => value?.ToString("F0") + " ms" ?? "--";

    private static string FormatPercentValue(double? value)
        => value?.ToString("F1") + "%" ?? "--";

    private static string BuildVolatilityLabel(IReadOnlyList<ProxyTrendEntry> records)
    {
        if (records.Count <= 1)
        {
            return "样本不足";
        }

        var ordered = records.OrderBy(record => record.Timestamp).ToArray();
        var flips = 0;
        for (var index = 1; index < ordered.Length; index++)
        {
            var previousStable = ResolveSuccessRate(ordered[index - 1]) >= 95;
            var currentStable = ResolveSuccessRate(ordered[index]) >= 95;
            if (previousStable != currentStable)
            {
                flips++;
            }
        }

        var successRate = Average(ordered.Select(record => (double?)ResolveSuccessRate(record))) ?? 0;
        return successRate switch
        {
            >= 90 when flips <= 1 => "较稳",
            >= 70 => "有波动",
            _ => "明显波动"
        };
    }

    private static string TryBuildHostLabel(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return baseUrl;
    }

    private static double ResolveSuccessRate(ProxyTrendEntry entry)
        => entry.FullSuccessRate ?? ComputeComponentSuccessRate(entry);

    private static double ComputeComponentSuccessRate(ProxyDiagnosticsResult result)
    {
        var score = 0d;
        score += result.ModelsRequestSucceeded ? 100d / 3d : 0;
        score += result.ChatRequestSucceeded ? 100d / 3d : 0;
        score += result.StreamRequestSucceeded ? 100d / 3d : 0;
        return Math.Round(score, 1);
    }

    private static double ComputeComponentSuccessRate(ProxyTrendEntry entry)
    {
        var score = 0d;
        score += entry.ModelsSuccess ? 100d / 3d : 0;
        score += entry.ChatSuccess ? 100d / 3d : 0;
        score += entry.StreamSuccess ? 100d / 3d : 0;
        return Math.Round(score, 1);
    }

    private sealed record ProxyTrendComparisonResult(
        string Summary,
        double? EarlierStability,
        double? LaterStability,
        double? EarlierChatLatency,
        double? LaterChatLatency,
        double? EarlierTtft,
        double? LaterTtft);

}
