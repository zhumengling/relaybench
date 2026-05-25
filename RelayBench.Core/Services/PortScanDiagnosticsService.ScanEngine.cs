using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class PortScanDiagnosticsService
{
    private static int[] GetUdpProbePorts(IReadOnlyList<int> ports)
        => ports.Where(ShouldProbeUdp).Distinct().OrderBy(static port => port).ToArray();

    private static IReadOnlyList<EndpointAttempt> BuildAttempts(
        IReadOnlyList<IPAddress> resolvedAddresses,
        IReadOnlyList<int> tcpPorts,
        IReadOnlyList<int> udpPorts)
    {
        List<EndpointAttempt> attempts = new(resolvedAddresses.Count * (tcpPorts.Count + udpPorts.Count));
        foreach (var address in resolvedAddresses)
        {
            foreach (var port in tcpPorts)
            {
                attempts.Add(new EndpointAttempt(address, port, "tcp"));
            }

            foreach (var port in udpPorts)
            {
                attempts.Add(new EndpointAttempt(address, port, "udp"));
            }
        }

        return attempts;
    }

    private static async Task<IReadOnlyList<PortScanFinding>> ScanEndpointsAsync(
        string target,
        IReadOnlyList<EndpointAttempt> attempts,
        PortScanProfile profile,
        IProgress<PortScanProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        using SemaphoreSlim throttle = new(profile.MaxConcurrency);
        List<Task<PortScanFinding?>> tasks = [];
        var totalEndpoints = attempts.Count;
        var progressInterval = Math.Max(1, totalEndpoints / 8);
        var completedEndpointCount = 0;
        var openEndpointCount = 0;

        foreach (var attempt in attempts)
        {
            tasks.Add(ScanAttemptWithThrottleAsync(
                target,
                attempt,
                profile,
                throttle,
                totalEndpoints,
                progressInterval,
                () => Interlocked.Increment(ref completedEndpointCount),
                () => Interlocked.Increment(ref openEndpointCount),
                () => Volatile.Read(ref openEndpointCount),
                progress,
                cancellationToken));
        }

        var rawResults = await Task.WhenAll(tasks);
        return rawResults
            .Where(static finding => finding is not null)
            .Cast<PortScanFinding>()
            .OrderBy(static finding => finding.Address, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static finding => finding.Port)
            .ThenBy(static finding => finding.Protocol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<PortScanFinding?> ScanAttemptWithThrottleAsync(
        string target,
        EndpointAttempt attempt,
        PortScanProfile profile,
        SemaphoreSlim throttle,
        int totalEndpoints,
        int progressInterval,
        Func<int> incrementCompleted,
        Func<int> incrementOpen,
        Func<int> readOpen,
        IProgress<PortScanProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);
        try
        {
            var finding = await ScanEndpointAsync(target, attempt, profile, cancellationToken);
            var completedEndpointCount = incrementCompleted();
            var currentEndpoint = FormatEndpoint(attempt.Address.ToString(), attempt.Port);

            int openEndpointCount;
            string? message = null;
            if (finding is not null)
            {
                openEndpointCount = incrementOpen();
                message = $"发现开放端点 {finding.Endpoint}/{finding.Protocol}，服务提示 {finding.ServiceHint}，延迟 {finding.ConnectLatencyMilliseconds} ms。";
            }
            else
            {
                openEndpointCount = readOpen();
                if (completedEndpointCount == 1 ||
                    completedEndpointCount == totalEndpoints ||
                    completedEndpointCount % progressInterval == 0)
                {
                    message = $"扫描进度 {completedEndpointCount}/{totalEndpoints}，已发现 {openEndpointCount} 个开放端点。";
                }
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                progress?.Report(new PortScanProgressUpdate(
                    DateTimeOffset.Now,
                    completedEndpointCount,
                    totalEndpoints,
                    openEndpointCount,
                    $"{currentEndpoint}/{attempt.Protocol}",
                    message,
                    finding));
            }
            else if (finding is not null)
            {
                progress?.Report(new PortScanProgressUpdate(
                    DateTimeOffset.Now,
                    completedEndpointCount,
                    totalEndpoints,
                    openEndpointCount,
                    $"{currentEndpoint}/{attempt.Protocol}",
                    string.Empty,
                    finding));
            }

            return finding;
        }
        finally
        {
            throttle.Release();
        }
    }

    private static Task<PortScanFinding?> ScanEndpointAsync(
        string target,
        EndpointAttempt attempt,
        PortScanProfile profile,
        CancellationToken cancellationToken)
        => string.Equals(attempt.Protocol, "udp", StringComparison.OrdinalIgnoreCase)
            ? ScanUdpEndpointAsync(target, attempt.Address, attempt.Port, profile, cancellationToken)
            : ScanTcpEndpointAsync(target, attempt.Address, attempt.Port, profile, cancellationToken);

    private static async Task<PortScanFinding?> ScanTcpEndpointAsync(
        string target,
        IPAddress address,
        int port,
        PortScanProfile profile,
        CancellationToken cancellationToken)
    {
        var connectLatency = await TryMeasureConnectLatencyAsync(address, port, profile.ConnectTimeoutMilliseconds, cancellationToken);
        if (connectLatency is null)
        {
            return null;
        }

        List<string> notes = [];
        ProbeOutcome bannerOutcome = default;
        ProbeOutcome tlsOutcome = default;
        ProbeOutcome httpOutcome = default;
        ProbeOutcome redisOutcome = default;
        ProbeOutcome applicationOutcome = default;

        if (profile.EnableBannerProbe && ShouldProbePassiveBanner(port))
        {
            bannerOutcome = await TryReadPassiveBannerAsync(address, port, profile.ConnectTimeoutMilliseconds, cancellationToken);
            if (!string.IsNullOrWhiteSpace(bannerOutcome.Warning))
            {
                notes.Add($"Banner={bannerOutcome.Warning}");
            }
        }

        if (profile.EnableTlsProbe && ShouldProbeTls(port))
        {
            tlsOutcome = await TryProbeTlsAsync(target, address, port, profile.ConnectTimeoutMilliseconds, cancellationToken);
            if (!string.IsNullOrWhiteSpace(tlsOutcome.Warning))
            {
                notes.Add($"TLS={tlsOutcome.Warning}");
            }
        }

        if (profile.EnableHttpProbe && ShouldProbeHttp(port))
        {
            httpOutcome = await TryProbeHttpAsync(
                target,
                address,
                port,
                useTls: tlsOutcome.Succeeded || TlsPorts.Contains(port),
                profile.ConnectTimeoutMilliseconds,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(httpOutcome.Warning))
            {
                notes.Add($"HTTP={httpOutcome.Warning}");
            }
        }

        if (port == 6379)
        {
            redisOutcome = await TryProbeRedisAsync(address, port, profile.ConnectTimeoutMilliseconds, cancellationToken);
            if (!string.IsNullOrWhiteSpace(redisOutcome.Warning))
            {
                notes.Add($"Redis={redisOutcome.Warning}");
            }
        }

        applicationOutcome = await TryProbeApplicationProtocolAsync(
            target,
            address,
            port,
            tlsOutcome.Succeeded || TlsPorts.Contains(port),
            profile.ConnectTimeoutMilliseconds,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(applicationOutcome.Warning))
        {
            notes.Add($"APP={applicationOutcome.Warning}");
        }

        var applicationSummary = httpOutcome.Summary ?? redisOutcome.Summary ?? applicationOutcome.Summary;
        var serviceHint = DeriveServiceHint(
            port,
            bannerOutcome.Summary,
            tlsOutcome.Summary,
            httpOutcome.Summary,
            redisOutcome.Summary,
            applicationOutcome.Summary);

        return new PortScanFinding(
            address.ToString(),
            port,
            "tcp",
            connectLatency.Value,
            serviceHint,
            bannerOutcome.Summary,
            tlsOutcome.Summary,
            applicationSummary,
            notes.Count == 0 ? null : string.Join(" | ", notes));
    }

    private static async Task<PortScanFinding?> ScanUdpEndpointAsync(
        string target,
        IPAddress address,
        int port,
        PortScanProfile profile,
        CancellationToken cancellationToken)
    {
        var udpResult = await TryProbeUdpAsync(target, address, port, profile.ConnectTimeoutMilliseconds, cancellationToken);
        if (!udpResult.Succeeded || string.IsNullOrWhiteSpace(udpResult.Summary))
        {
            return null;
        }

        return new PortScanFinding(
            address.ToString(),
            port,
            "udp",
            udpResult.RoundTripMilliseconds,
            udpResult.ServiceHint,
            null,
            null,
            udpResult.Summary,
            udpResult.Warning);
    }
}
