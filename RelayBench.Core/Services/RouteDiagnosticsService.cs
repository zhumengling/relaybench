using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using RelayBench.Core.Models;
using RelayBench.Core.Support;

namespace RelayBench.Core.Services;

public sealed partial class RouteDiagnosticsService
{
    private static readonly Regex HopLineRegex = new(
        @"^\s*(\d+)\s+(<\d+\s*ms|\d+\s*ms|\*)\s+(<\d+\s*ms|\d+\s*ms|\*)\s+(<\d+\s*ms|\d+\s*ms|\*)\s+(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<RouteDiagnosticsResult> RunAsync(
        string target,
        int maxHops,
        int timeoutMilliseconds,
        int samplesPerHop,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => RunAsync(
            target,
            maxHops,
            timeoutMilliseconds,
            samplesPerHop,
            null,
            null,
            null,
            progress,
            cancellationToken);

    public async Task<RouteDiagnosticsResult> RunAsync(
        string target,
        int maxHops,
        int timeoutMilliseconds,
        int samplesPerHop,
        string? protocolKey,
        string? resolverMode,
        int? destinationPort,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedTarget = NormalizeTargetInput(target);
        var normalizedMaxHops = Math.Clamp(maxHops, 1, 30);
        var normalizedTimeout = Math.Clamp(timeoutMilliseconds, 250, 5_000);
        var normalizedSamples = Math.Clamp(samplesPerHop, 1, 10);
        var normalizedProtocol = NormalizeProtocolKey(protocolKey);
        var normalizedResolverMode = NormalizeResolverMode(resolverMode);

        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return new RouteDiagnosticsResult(
                DateTimeOffset.Now,
                normalizedTarget,
                Array.Empty<string>(),
                normalizedMaxHops,
                normalizedTimeout,
                normalizedSamples,
                false,
                0,
                "未提供检测目标。",
                "请输入要检测的域名或 IP 地址。",
                string.Empty,
                Array.Empty<RouteHopResult>(),
                null,
                Array.Empty<string>(),
                null,
                null,
                null);
        }

        progress?.Report($"正在解析 {normalizedTarget} 的路由探测目标...");
        var tracePlan = await ResolveTracePlanAsync(normalizedTarget, normalizedResolverMode, cancellationToken);
        var resolvedAddresses = tracePlan.ResolvedAddresses;

        if (!string.Equals(normalizedProtocol, "icmp", StringComparison.OrdinalIgnoreCase))
        {
            var protocolText = DescribeProtocol(normalizedProtocol);
            var builtinOnlyMessage = "软件当前仅使用内置 ICMP MTR 与 Windows tracert，已禁用 TCP/UDP 探测。";
            progress?.Report($"当前内置模式仅支持 ICMP，未执行 {protocolText} 路由探测。");

            return new RouteDiagnosticsResult(
                DateTimeOffset.Now,
                normalizedTarget,
                resolvedAddresses,
                normalizedMaxHops,
                normalizedTimeout,
                normalizedSamples,
                false,
                0,
                $"软件当前仅支持 ICMP 路由探测，未执行 {protocolText} 探测。",
                builtinOnlyMessage,
                $"请求协议：{protocolText}{Environment.NewLine}{builtinOnlyMessage}",
                Array.Empty<RouteHopResult>(),
                tracePlan.TraceTarget,
                tracePlan.SystemResolvedAddresses,
                tracePlan.ResolutionSummary,
                "内置模式（仅支持 ICMP）",
                builtinOnlyMessage);
        }

        var (builtinResult, builtinFallbackReason) = await TryRunWithBuiltInMtrAsync(
            normalizedTarget,
            tracePlan,
            normalizedMaxHops,
            normalizedTimeout,
            normalizedSamples,
            normalizedProtocol,
            progress,
            cancellationToken);

        if (builtinResult is not null)
        {
            return builtinResult;
        }

        progress?.Report(
            string.Equals(tracePlan.TraceTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase)
                ? $"正在对 {normalizedTarget} 执行 Windows tracert..."
                : $"正在对 {normalizedTarget} 执行 Windows tracert，实际探测目标 {tracePlan.TraceTarget}...");

        var traceTimeout = TimeSpan.FromMilliseconds(Math.Max(normalizedMaxHops * normalizedTimeout * 4L, 15_000));
        var traceCommand = await CommandLineRunner.RunStreamingAsync(
            "tracert.exe",
            ["-d", "-w", normalizedTimeout.ToString(), "-h", normalizedMaxHops.ToString(), tracePlan.TraceTarget],
            traceTimeout,
            standardOutputLineReceived: line => ReportRouteRawLine(progress, line),
            standardErrorLineReceived: line => ReportRouteRawLine(progress, $"[stderr] {line}"),
            cancellationToken);

        var rawTraceOutput = BuildRawCommandOutput(traceCommand);
        var fallbackText = string.IsNullOrWhiteSpace(builtinFallbackReason)
            ? "内置 ICMP MTR 不可用"
            : builtinFallbackReason.Trim().TrimEnd('。');
        var tracertEngineSummary = $"{fallbackText}，已回退到 Windows tracert + Ping。";

        if (!traceCommand.Started)
        {
            return new RouteDiagnosticsResult(
                DateTimeOffset.Now,
                normalizedTarget,
                resolvedAddresses,
                normalizedMaxHops,
                normalizedTimeout,
                normalizedSamples,
                false,
                0,
                "无法启动 tracert。",
                traceCommand.Error,
                rawTraceOutput,
                Array.Empty<RouteHopResult>(),
                tracePlan.TraceTarget,
                tracePlan.SystemResolvedAddresses,
                tracePlan.ResolutionSummary,
                "Windows tracert + Ping",
                tracertEngineSummary);
        }

        var parsedTraceHops = ParseTraceHops(traceCommand.StandardOutput);
        if (parsedTraceHops.Count == 0)
        {
            return new RouteDiagnosticsResult(
                DateTimeOffset.Now,
                normalizedTarget,
                resolvedAddresses,
                normalizedMaxHops,
                normalizedTimeout,
                normalizedSamples,
                false,
                0,
                "tracert 已执行，但没有解析出 hop 数据。",
                traceCommand.Error,
                rawTraceOutput,
                Array.Empty<RouteHopResult>(),
                tracePlan.TraceTarget,
                tracePlan.SystemResolvedAddresses,
                tracePlan.ResolutionSummary,
                "Windows tracert + Ping",
                tracertEngineSummary);
        }

        List<RouteHopResult> sampledHops = new(parsedTraceHops.Count);
        for (var index = 0; index < parsedTraceHops.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hop = parsedTraceHops[index];
            if (string.IsNullOrWhiteSpace(hop.Address))
            {
                sampledHops.Add(hop with
                {
                    SentProbes = 0,
                    ReceivedResponses = 0,
                    LossPercent = null,
                    BestRoundTripTime = null,
                    AverageRoundTripTime = null,
                    WorstRoundTripTime = null
                });
                continue;
            }

            progress?.Report($"正在采样第 {index + 1}/{parsedTraceHops.Count} 跳：{hop.Address}");
            var sampledHop = await SampleHopAsync(hop, normalizedSamples, normalizedTimeout, cancellationToken);
            sampledHops.Add(sampledHop);
            ReportRouteHopPreview(progress, sampledHop);
        }

        var responsiveHopCount = sampledHops.Count(hop => !string.IsNullOrWhiteSpace(hop.Address));
        var traceCompleted = DidTraceReachTarget(sampledHops, tracePlan.TraceTarget, resolvedAddresses);
        var summary = BuildSummary(sampledHops, traceCompleted, normalizedTarget);
        var error = traceCommand.TimedOut
            ? traceCommand.Error
            : traceCommand.ExitCode is > 0
                ? $"tracert 以退出码 {traceCommand.ExitCode} 结束。"
                : traceCommand.Error;

        return new RouteDiagnosticsResult(
            DateTimeOffset.Now,
            normalizedTarget,
            resolvedAddresses,
            normalizedMaxHops,
            normalizedTimeout,
            normalizedSamples,
            traceCompleted,
            responsiveHopCount,
            summary,
            error,
            rawTraceOutput,
            sampledHops,
            tracePlan.TraceTarget,
            tracePlan.SystemResolvedAddresses,
            tracePlan.ResolutionSummary,
            "Windows tracert + Ping",
            tracertEngineSummary);
    }
}
