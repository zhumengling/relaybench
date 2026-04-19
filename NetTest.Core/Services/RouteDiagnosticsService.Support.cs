using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using NetTest.Core.Models;
using NetTest.Core.Support;

namespace NetTest.Core.Services;

public sealed partial class RouteDiagnosticsService
{
    private static string NormalizeProtocolKey(string? protocolKey)
        => protocolKey?.Trim().ToLowerInvariant() switch
        {
            "tcp" => "tcp",
            "udp" => "udp",
            _ => "icmp"
        };

    private static string NormalizeResolverMode(string? resolverMode)
        => resolverMode?.Trim().ToLowerInvariant() switch
        {
            "system" => "system",
            "google-doh" => "google-doh",
            "cloudflare-doh" => "cloudflare-doh",
            _ => "auto"
        };

    private static string DescribeProtocol(string protocolKey)
        => protocolKey switch
        {
            "tcp" => "TCP",
            "udp" => "UDP",
            _ => "ICMP"
        };

    private static string NormalizeTargetInput(string? target)
    {
        var normalized = target?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absoluteUri) && !string.IsNullOrWhiteSpace(absoluteUri.Host))
        {
            normalized = absoluteUri.Host;
        }

        if (normalized.Contains(':', StringComparison.Ordinal) &&
            normalized.Contains('.', StringComparison.Ordinal) &&
            !normalized.Contains('[', StringComparison.Ordinal) &&
            !IPAddress.TryParse(normalized, out _))
        {
            var hostCandidate = normalized.Split(':', 2, StringSplitOptions.TrimEntries)[0];
            if (!string.IsNullOrWhiteSpace(hostCandidate))
            {
                normalized = hostCandidate;
            }
        }

        return normalized;
    }

    private static bool DidTraceReachTarget(
        IReadOnlyList<RouteHopResult> hops,
        string target,
        IReadOnlyList<string> resolvedAddresses)
    {
        var lastResponsiveHop = hops.LastOrDefault(hop => !string.IsNullOrWhiteSpace(hop.Address));
        if (lastResponsiveHop?.Address is null)
        {
            return false;
        }

        if (resolvedAddresses.Contains(lastResponsiveHop.Address, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(lastResponsiveHop.Address, target, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSummary(IReadOnlyList<RouteHopResult> hops, bool traceCompleted, string target)
    {
        var sampledHops = hops.Where(hop => hop.SentProbes > 0).ToList();
        var responsiveHops = sampledHops.Where(hop => hop.ReceivedResponses > 0).ToList();
        var highestLossHop = sampledHops
            .Where(hop => hop.LossPercent.HasValue)
            .OrderByDescending(hop => hop.LossPercent)
            .ThenByDescending(hop => hop.HopNumber)
            .FirstOrDefault();
        var slowestHop = sampledHops
            .Where(hop => hop.AverageRoundTripTime.HasValue)
            .OrderByDescending(hop => hop.AverageRoundTripTime)
            .ThenByDescending(hop => hop.HopNumber)
            .FirstOrDefault();

        var routeStatus = traceCompleted
            ? $"已在 {hops.Count} 跳内到达 {target}。"
            : $"未能明确确认到达 {target}；当前共解析出 {hops.Count} 跳。";

        var lossStatus = highestLossHop?.LossPercent is null
            ? "没有采集到逐跳丢包数据。"
            : $"最高丢包：第 {highestLossHop.HopNumber} 跳 {highestLossHop.Address}，丢包率 {highestLossHop.LossPercent:F1}%。";

        var latencyStatus = slowestHop?.AverageRoundTripTime is null
            ? "没有采集到逐跳平均延迟数据。"
            : $"最高平均延迟：第 {slowestHop.HopNumber} 跳 {slowestHop.Address}，平均延迟 {slowestHop.AverageRoundTripTime:F1} ms。";

        return $"{routeStatus} 共采样 {responsiveHops.Count} 个可响应 hop。 {lossStatus} {latencyStatus}";
    }

    private static string BuildRawCommandOutput(CommandExecutionResult command)
    {
        if (!string.IsNullOrWhiteSpace(command.StandardError))
        {
            return $"{command.StandardOutput}\n\n标准错误：\n{command.StandardError}".Trim();
        }

        return command.StandardOutput.Trim();
    }

    private static void ReportRouteRawLine(IProgress<string>? progress, string? rawLine)
    {
        var normalized = rawLine?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        progress?.Report(RouteDiagnosticsProgressTokens.RawLinePrefix + normalized);
    }

    private static void ReportRouteHopPreview(IProgress<string>? progress, RouteHopResult hop)
        => progress?.Report(RouteDiagnosticsProgressTokens.HopPreviewPrefix + BuildHopPreviewLine(hop));

    private static string BuildHopPreviewLine(RouteHopResult hop)
    {
        var address = string.IsNullOrWhiteSpace(hop.Address) ? "*" : hop.Address;
        var traceSamples = string.Join("/", hop.TraceRoundTripTimes.Select(FormatTraceSampleForPreview));
        var loss = hop.LossPercent?.ToString("F1") + "%" ?? "--";
        var average = hop.AverageRoundTripTime?.ToString("F1") + " ms" ?? "--";
        return $"#{hop.HopNumber,2} {address}  trace={traceSamples}  发送={hop.SentProbes} 接收={hop.ReceivedResponses}  丢包={loss}  平均={average}";
    }

    private static string FormatTraceSampleForPreview(long? value)
        => value?.ToString() + " ms" ?? "*";
}
