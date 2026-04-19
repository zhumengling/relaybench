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
    private static ProxyStabilityResult BuildStabilityResult(
        ProxyEndpointSettings settings,
        int requestedRounds,
        int delayMilliseconds,
        IReadOnlyList<ProxyDiagnosticsResult> rounds)
    {
        var completedRounds = rounds.Count;
        var fullSuccessCount = rounds.Count(IsFullSuccess);
        var modelsSuccessCount = rounds.Count(round => round.ModelsRequestSucceeded);
        var chatSuccessCount = rounds.Count(round => round.ChatRequestSucceeded);
        var streamSuccessCount = rounds.Count(round => round.StreamRequestSucceeded);
        var responsesSuccessCount = rounds.Count(round => GetScenario(round.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>(), ProxyProbeScenarioKind.Responses)?.Success == true);
        var structuredOutputSuccessCount = rounds.Count(round => GetScenario(round.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>(), ProxyProbeScenarioKind.StructuredOutput)?.Success == true);
        var maxConsecutiveFailures = CalculateMaxConsecutiveFailures(rounds);
        var fullSuccessRate = Rate(fullSuccessCount, completedRounds);
        var modelsSuccessRate = Rate(modelsSuccessCount, completedRounds);
        var chatSuccessRate = Rate(chatSuccessCount, completedRounds);
        var streamSuccessRate = Rate(streamSuccessCount, completedRounds);
        var averageChatLatency = AverageTimeSpan(rounds.Where(round => round.ChatLatency.HasValue).Select(round => round.ChatLatency!.Value));
        var averageTtft = AverageTimeSpan(rounds.Where(round => round.StreamFirstTokenLatency.HasValue).Select(round => round.StreamFirstTokenLatency!.Value));
        var averageResponsesLatency = AverageTimeSpan(
            rounds
                .Select(round => GetScenario(round.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>(), ProxyProbeScenarioKind.Responses)?.Latency)
                .Where(latency => latency.HasValue)
                .Select(latency => latency!.Value));
        var averageStructuredOutputLatency = AverageTimeSpan(
            rounds
                .Select(round => GetScenario(round.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>(), ProxyProbeScenarioKind.StructuredOutput)?.Latency)
                .Where(latency => latency.HasValue)
                .Select(latency => latency!.Value));
        var failureDistributions = BuildFailureDistribution(rounds, completedRounds);
        var failureDistributionSummary = failureDistributions.Count == 0
            ? "未观察到明确的主故障类型。"
            : string.Join("；", failureDistributions.Take(5).Select(item => item.Summary));
        var distinctResolvedAddressCount = rounds
            .SelectMany(round => round.ResolvedAddresses ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var distinctEdgeSignatureCount = rounds
            .Select(round => round.EdgeSignature)
            .Where(signature => !string.IsNullOrWhiteSpace(signature))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var edgeSwitchCount = CalculateEdgeSwitchCount(rounds);
        var cdnStabilitySummary = BuildCdnStabilitySummary(rounds, distinctResolvedAddressCount, distinctEdgeSignatureCount, edgeSwitchCount);
        var healthScore = CalculateHealthScore(fullSuccessRate, streamSuccessRate, averageChatLatency, averageTtft, maxConsecutiveFailures);
        var healthLabel = LabelHealth(healthScore);

        var summary =
            $"健康度 {healthScore}/100（{healthLabel}）。" +
            $"完整成功 {fullSuccessCount}/{completedRounds}，" +
            $"流式成功 {streamSuccessCount}/{completedRounds}，" +
            $"Responses 成功 {responsesSuccessCount}/{completedRounds}，" +
            $"结构化输出成功 {structuredOutputSuccessCount}/{completedRounds}，" +
            $"平均普通对话延迟 {(averageChatLatency?.TotalMilliseconds.ToString("F0") ?? "--")} ms，" +
            $"平均 TTFT {(averageTtft?.TotalMilliseconds.ToString("F0") ?? "--")} ms，" +
            $"最大连续失败 {maxConsecutiveFailures}。" +
            $"失败分布：{failureDistributionSummary}" +
            $" CDN 观察：{cdnStabilitySummary}";

        return new ProxyStabilityResult(
            DateTimeOffset.Now,
            settings.BaseUrl,
            requestedRounds,
            completedRounds,
            delayMilliseconds,
            fullSuccessCount,
            modelsSuccessCount,
            chatSuccessCount,
            streamSuccessCount,
            maxConsecutiveFailures,
            fullSuccessRate,
            modelsSuccessRate,
            chatSuccessRate,
            streamSuccessRate,
            averageChatLatency,
            averageTtft,
            healthScore,
            healthLabel,
            summary,
            rounds,
            responsesSuccessCount,
            structuredOutputSuccessCount,
            averageResponsesLatency,
            averageStructuredOutputLatency,
            failureDistributions,
            failureDistributionSummary,
            distinctResolvedAddressCount,
            distinctEdgeSignatureCount,
            edgeSwitchCount,
            cdnStabilitySummary);
    }

    private static bool IsFullSuccess(ProxyDiagnosticsResult result)
        => result.ModelsRequestSucceeded && result.ChatRequestSucceeded && result.StreamRequestSucceeded;

    private static int CalculateMaxConsecutiveFailures(IReadOnlyList<ProxyDiagnosticsResult> rounds)
    {
        var max = 0;
        var current = 0;

        foreach (var round in rounds)
        {
            if (IsFullSuccess(round))
            {
                current = 0;
                continue;
            }

            current++;
            max = Math.Max(max, current);
        }

        return max;
    }

    private static int CalculateEdgeSwitchCount(IReadOnlyList<ProxyDiagnosticsResult> rounds)
    {
        var previousSignature = string.Empty;
        var switches = 0;

        foreach (var round in rounds)
        {
            var currentSignature = round.EdgeSignature?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentSignature))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(previousSignature) &&
                !string.Equals(previousSignature, currentSignature, StringComparison.OrdinalIgnoreCase))
            {
                switches++;
            }

            previousSignature = currentSignature;
        }

        return switches;
    }

    private static IReadOnlyList<ProxyFailureDistributionItem> BuildFailureDistribution(
        IReadOnlyList<ProxyDiagnosticsResult> rounds,
        int completedRounds)
    {
        if (completedRounds == 0)
        {
            return Array.Empty<ProxyFailureDistributionItem>();
        }

        return rounds
            .Select(round => round.PrimaryFailureKind)
            .Where(kind => kind.HasValue)
            .Select(kind => kind!.Value)
            .GroupBy(kind => kind)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => GetFailurePriority(group.Key))
            .Select(group =>
            {
                var count = group.Count();
                var rate = Rate(count, completedRounds);
                return new ProxyFailureDistributionItem(
                    group.Key,
                    count,
                    rate,
                    $"{DescribeFailureKind(group.Key)} {count} 次（{rate:F1}%）");
            })
            .ToArray();
    }

}
