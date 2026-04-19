using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class RouteDiagnosticsService
{
    private static readonly byte[] BuiltInTracePayload = Encoding.ASCII.GetBytes("NetTestBuiltInTraceProbePayload");

    private static async Task<(RouteDiagnosticsResult? Result, string? FallbackReason)> TryRunWithBuiltInMtrAsync(
        string normalizedTarget,
        RouteTracePlan tracePlan,
        int maxHops,
        int timeoutMilliseconds,
        int samplesPerHop,
        string protocolKey,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(protocolKey, "icmp", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "内置 MTR 引擎当前仅支持 ICMP 协议。");
        }

        if (!IPAddress.TryParse(tracePlan.TraceTarget, out var traceAddress))
        {
            return (null, "内置 ICMP MTR 需要一个已解析的 IP 目标。");
        }

        try
        {
            progress?.Report(
                string.Equals(tracePlan.TraceTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase)
                    ? $"正在使用内置 ICMP MTR 引擎探测 {normalizedTarget}..."
                    : $"正在使用内置 ICMP MTR 引擎探测 {normalizedTarget}，实际探测目标 {tracePlan.TraceTarget}...");

            List<RouteHopResult> hops = [];
            List<string> rawLines = [];
            var reachedDestination = false;

            for (var ttl = 1; ttl <= maxHops; ttl++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"内置 ICMP MTR：第 {ttl}/{maxHops} 跳，采样 {samplesPerHop} 次...");

                var hopSamples = await ProbeHopAsync(traceAddress, ttl, timeoutMilliseconds, samplesPerHop, cancellationToken);
                var hop = BuildBuiltInHopResult(ttl, hopSamples, tracePlan.TraceTarget, tracePlan.ResolvedAddresses);
                hops.Add(hop);
                rawLines.AddRange(hopSamples.Select(static sample => sample.RawLine));
                foreach (var sample in hopSamples)
                {
                    ReportRouteRawLine(progress, sample.RawLine);
                }

                ReportRouteHopPreview(progress, hop);

                if (hopSamples.Any(sample => sample.ReachedDestination))
                {
                    reachedDestination = true;
                    break;
                }
            }

            if (hops.Count == 0 || hops.All(static hop => hop.ReceivedResponses == 0))
            {
                return (null, "内置 ICMP MTR 没有采到可响应 hop。");
            }

            var responsiveHopCount = hops.Count(static hop => hop.ReceivedResponses > 0);
            var traceCompleted = reachedDestination || DidTraceReachTarget(hops, tracePlan.TraceTarget, tracePlan.ResolvedAddresses);
            var summary = BuildSummary(hops, traceCompleted, normalizedTarget);
            var engineSummary =
                $"已使用内置 ICMP MTR 引擎直接进行 TTL 探测；每跳采样 {samplesPerHop} 次，不再依赖 tracert 后再二次 Ping。";

            return (new RouteDiagnosticsResult(
                DateTimeOffset.Now,
                normalizedTarget,
                tracePlan.ResolvedAddresses,
                maxHops,
                timeoutMilliseconds,
                samplesPerHop,
                traceCompleted,
                responsiveHopCount,
                summary,
                null,
                string.Join(Environment.NewLine, rawLines),
                hops,
                tracePlan.TraceTarget,
                tracePlan.SystemResolvedAddresses,
                tracePlan.ResolutionSummary,
                "内置 ICMP MTR",
                engineSummary), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"内置 ICMP MTR 执行失败：{ex.Message}");
        }
    }

    private static async Task<List<BuiltInTraceSample>> ProbeHopAsync(
        IPAddress targetAddress,
        int ttl,
        int timeoutMilliseconds,
        int samplesPerHop,
        CancellationToken cancellationToken)
    {
        List<BuiltInTraceSample> samples = new(samplesPerHop);
        using Ping ping = new();

        for (var sampleIndex = 1; sampleIndex <= samplesPerHop; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PingReply? reply = null;
            Exception? error = null;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                reply = await ping.SendPingAsync(
                    targetAddress,
                    timeoutMilliseconds,
                    BuiltInTracePayload,
                    new PingOptions(ttl, true));
            }
            catch (Exception ex)
            {
                error = ex;
            }

            stopwatch.Stop();
            samples.Add(BuildBuiltInTraceSample(ttl, sampleIndex, timeoutMilliseconds, targetAddress, reply, error, stopwatch.Elapsed));
        }

        return samples;
    }

    private static BuiltInTraceSample BuildBuiltInTraceSample(
        int ttl,
        int sampleIndex,
        int timeoutMilliseconds,
        IPAddress targetAddress,
        PingReply? reply,
        Exception? error,
        TimeSpan measuredElapsed)
    {
        if (reply is null)
        {
            var rawLine = $"{ttl}|{sampleIndex}|error||timeout={timeoutMilliseconds}|{error?.Message ?? "unknown"}";
            return new BuiltInTraceSample(null, null, false, false, rawLine);
        }

        var addressText = reply.Address?.ToString();
        var responsive = IsResponsiveTraceStatus(reply.Status, reply.Address);
        long? latency = responsive ? ResolveBuiltInLatencyMilliseconds(reply, measuredElapsed) : null;
        var reachedDestination = reply.Status == IPStatus.Success ||
                                 string.Equals(addressText, targetAddress.ToString(), StringComparison.OrdinalIgnoreCase);
        var raw = $"{ttl}|{sampleIndex}|{reply.Status}|{addressText ?? "*"}|rtt={(latency?.ToString() ?? "*")}ms";

        return new BuiltInTraceSample(
            responsive ? addressText : null,
            latency,
            responsive,
            reachedDestination,
            raw);
    }

    private static long ResolveBuiltInLatencyMilliseconds(PingReply reply, TimeSpan measuredElapsed)
    {
        if (reply.RoundtripTime > 0)
        {
            return reply.RoundtripTime;
        }

        var elapsedMilliseconds = measuredElapsed.TotalMilliseconds;
        if (elapsedMilliseconds > 0d)
        {
            return Math.Max(1L, (long)Math.Ceiling(elapsedMilliseconds));
        }

        return 1L;
    }

    private static bool IsResponsiveTraceStatus(IPStatus status, IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        return status switch
        {
            IPStatus.Success => true,
            IPStatus.TtlExpired => true,
            IPStatus.TimeExceeded => true,
            IPStatus.TtlReassemblyTimeExceeded => true,
            IPStatus.DestinationHostUnreachable => true,
            IPStatus.DestinationNetworkUnreachable => true,
            IPStatus.DestinationProtocolUnreachable => true,
            IPStatus.DestinationPortUnreachable => true,
            _ => false
        };
    }

    private static RouteHopResult BuildBuiltInHopResult(
        int ttl,
        IReadOnlyList<BuiltInTraceSample> samples,
        string traceTarget,
        IReadOnlyList<string> resolvedAddresses)
    {
        var successfulSamples = samples.Where(static sample => sample.Responsive).ToList();
        var selectedAddress = SelectBuiltInHopAddress(successfulSamples, traceTarget, resolvedAddresses);
        var latencies = samples.Select(static sample => sample.RoundTripTimeMilliseconds).ToArray();
        var sent = samples.Count;
        var received = successfulSamples.Count;
        double? lossPercent = sent == 0 ? null : (sent - received) * 100d / sent;
        var successfulLatencies = successfulSamples
            .Where(static sample => sample.RoundTripTimeMilliseconds.HasValue)
            .Select(sample => sample.RoundTripTimeMilliseconds!.Value)
            .ToArray();

        return new RouteHopResult(
            ttl,
            selectedAddress,
            latencies,
            received == 0,
            sent,
            received,
            lossPercent,
            successfulLatencies.Length == 0 ? null : successfulLatencies.Min(),
            successfulLatencies.Length == 0 ? (double?)null : successfulLatencies.Average(),
            successfulLatencies.Length == 0 ? null : successfulLatencies.Max(),
            string.Join(" || ", samples.Select(static sample => sample.RawLine)));
    }

    private static string? SelectBuiltInHopAddress(
        IReadOnlyList<BuiltInTraceSample> samples,
        string traceTarget,
        IReadOnlyList<string> resolvedAddresses)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var destinationAddress = samples
            .Select(static sample => sample.Address)
            .FirstOrDefault(address =>
                !string.IsNullOrWhiteSpace(address) &&
                (string.Equals(address, traceTarget, StringComparison.OrdinalIgnoreCase) ||
                 resolvedAddresses.Contains(address, StringComparer.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(destinationAddress))
        {
            return destinationAddress;
        }

        Dictionary<string, (int Count, int FirstIndex)> stats = new(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < samples.Count; index++)
        {
            var address = samples[index].Address;
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            if (stats.TryGetValue(address, out var stat))
            {
                stats[address] = (stat.Count + 1, stat.FirstIndex);
            }
            else
            {
                stats[address] = (1, index);
            }
        }

        return stats
            .OrderByDescending(static pair => pair.Value.Count)
            .ThenBy(static pair => pair.Value.FirstIndex)
            .Select(static pair => pair.Key)
            .FirstOrDefault();
    }

    private sealed record BuiltInTraceSample(
        string? Address,
        long? RoundTripTimeMilliseconds,
        bool Responsive,
        bool ReachedDestination,
        string RawLine);
}
