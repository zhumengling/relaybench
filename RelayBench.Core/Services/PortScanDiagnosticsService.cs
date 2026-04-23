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

public sealed class PortScanDiagnosticsService
{
    private const string EngineName = "RelayBench Local Port Scanner";
    private const int MaxCustomPortCount = 2048;
    private static readonly string EngineVersion = typeof(PortScanDiagnosticsService).Assembly.GetName().Version?.ToString(3) ?? "dev";
    private static readonly HttpClient PublicDnsHttpClient = CreatePublicDnsHttpClient();

    private static readonly HashSet<int> PassiveBannerPorts =
    [
        21, 22, 25, 110, 143, 3306
    ];

    private static readonly HashSet<int> HttpPorts =
    [
        80, 81, 443, 591, 593, 8000, 8008, 8080, 8081, 8088, 8443, 8448, 8888,
        2052, 2053, 2082, 2083, 2086, 2087, 2095, 2096
    ];

    private static readonly HashSet<int> TlsPorts =
    [
        443, 465, 993, 995, 2053, 2083, 2087, 2096, 8443, 8448, 9443
    ];

    private static readonly HashSet<int> UdpProbePorts =
    [
        53, 123, 1900, 3478
    ];

    private static readonly IReadOnlyDictionary<int, string> KnownServiceHints = new Dictionary<int, string>
    {
        [21] = "ftp",
        [22] = "ssh",
        [23] = "telnet",
        [25] = "smtp",
        [53] = "dns",
        [80] = "http",
        [81] = "http-alt",
        [110] = "pop3",
        [123] = "ntp",
        [135] = "msrpc",
        [139] = "netbios-ssn",
        [143] = "imap",
        [389] = "ldap",
        [443] = "https",
        [445] = "smb",
        [465] = "smtps",
        [587] = "smtp-submission",
        [993] = "imaps",
        [995] = "pop3s",
        [1433] = "mssql",
        [1521] = "oracle",
        [1900] = "ssdp",
        [3306] = "mysql",
        [3389] = "rdp",
        [3478] = "stun",
        [5432] = "postgresql",
        [5900] = "vnc",
        [6379] = "redis",
        [8000] = "http-alt",
        [8008] = "http-alt",
        [8080] = "http-alt",
        [8081] = "http-alt",
        [8088] = "http-alt",
        [8443] = "https-alt"
    };

    private static readonly IReadOnlyList<PortScanProfile> Profiles =
    [
        new PortScanProfile(
            "relay-baseline",
            "接口基线扫描",
            "面向代理与 HTTPS 节点的保守异步 TCP Connect 扫描，覆盖常见兼容接口端口，并补充 TLS / HTTP 识别。",
            [80, 443, 8080, 8443, 2053, 2083, 2087, 2096],
            1200,
            48,
            true,
            true,
            true,
            false),
        new PortScanProfile(
            "top-ports",
            "常见端口快速扫描",
            "借鉴 naabu / RustScan 的高并发思路，快速检查常见服务端口，并输出结构化结果；对支持的 UDP 端口额外做响应探测。",
            [21, 22, 23, 25, 53, 80, 110, 123, 135, 139, 143, 389, 443, 445, 465, 587, 993, 995, 1433, 1521, 3306, 3389, 5432, 5900, 6379, 8000, 8080, 8443],
            1000,
            96,
            true,
            true,
            true,
            true),
        new PortScanProfile(
            "service-detect",
            "服务识别优先",
            "优先执行轻量服务识别：被动 Banner、TLS 握手、HTTP 头探测，以及 SMTP / POP3 / IMAP / DNS 等协议识别。",
            [21, 22, 25, 53, 80, 110, 143, 443, 465, 587, 993, 995, 1433, 3306, 3389, 5432, 6379, 8000, 8080, 8443, 9443],
            1800,
            40,
            true,
            true,
            true,
            true),
        new PortScanProfile(
            "udp-discovery",
            "UDP 服务发现",
            "聚焦 DNS、NTP、SSDP、STUN 等可响应 UDP 服务，适合补充发现常见基础设施端口。",
            [53, 123, 1900, 3478],
            1200,
            48,
            false,
            false,
            false,
            true)
    ];

    private readonly record struct ProbeOutcome(bool Succeeded, string? Summary, string? Warning);
    private readonly record struct EndpointAttempt(IPAddress Address, int Port, string Protocol);
    private readonly record struct UdpProbeResult(bool Succeeded, long RoundTripMilliseconds, string ServiceHint, string? Summary, string? Warning);
    private readonly record struct UdpExchangeResult(byte[] ResponseBytes, long RoundTripMilliseconds);
    private readonly record struct AddressResolutionInfo(
        IReadOnlyList<IPAddress> Addresses,
        IReadOnlyList<string> SystemAddresses,
        string Source,
        string Summary);

    public IReadOnlyList<PortScanProfile> GetProfiles() => Profiles;

    public PortScanProfile GetDefaultProfile() => Profiles[0];

    public Task<PortScanResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var detailOutput =
            $"Engine: {EngineName}{Environment.NewLine}" +
            $"Version: {EngineVersion}{Environment.NewLine}" +
            "Capabilities: async TCP connect, supported UDP probes, custom port parser, banner probe, TLS probe, HTTP probe";

        return Task.FromResult(
            new PortScanResult(
                DateTimeOffset.Now,
                true,
                EngineName,
                EngineVersion,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "builtin://port-scan --detect",
                false,
                true,
                0,
                0,
                0,
                0,
                [],
                [],
                $"内置端口扫描引擎已就绪（{EngineVersion}），支持 TCP Connect、UDP 响应探测与轻量协议识别。",
                null,
                detailOutput,
                string.Empty,
                []));
    }

    public async Task<PortScanResult> RunAsync(
        string target,
        string profileKey,
        string? customPortsText = null,
        IProgress<PortScanProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTarget = target.Trim();
        var probeTarget = NormalizeDnsHostForNetworkApis(normalizedTarget);
        var normalizedCustomPortsText = NormalizeCustomPortsText(customPortsText);
        var profile = Profiles.FirstOrDefault(candidate => string.Equals(candidate.Key, profileKey, StringComparison.OrdinalIgnoreCase))
            ?? GetDefaultProfile();

        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return new PortScanResult(
                DateTimeOffset.Now,
                true,
                EngineName,
                EngineVersion,
                normalizedTarget,
                profile.Key,
                profile.DisplayName,
                normalizedCustomPortsText,
                string.Empty,
                BuildPseudoCommandLine("<missing>", profile, string.Empty),
                false,
                false,
                2,
                0,
                0,
                0,
                [],
                [],
                "参数校验失败",
                "必须填写扫描目标。",
                string.Empty,
                string.Empty,
                []);
        }

        var ports = ResolvePorts(profile, normalizedCustomPortsText, out var effectivePortsText, out var portError);
        if (portError is not null)
        {
            return new PortScanResult(
                DateTimeOffset.Now,
                true,
                EngineName,
                EngineVersion,
                normalizedTarget,
                profile.Key,
                profile.DisplayName,
                normalizedCustomPortsText,
                effectivePortsText,
                BuildPseudoCommandLine(normalizedTarget, profile, effectivePortsText),
                false,
                false,
                2,
                0,
                0,
                0,
                [],
                [],
                "端口输入无效",
                portError,
                string.Empty,
                string.Empty,
                []);
        }

        progress?.Report(new PortScanProgressUpdate(
            DateTimeOffset.Now,
            0,
            0,
            0,
            normalizedTarget,
            $"正在解析目标 {normalizedTarget} ...",
            null));

        AddressResolutionInfo resolutionInfo;
        try
        {
            resolutionInfo = await ResolveTargetAddressesAsync(normalizedTarget, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PortScanResult(
                DateTimeOffset.Now,
                true,
                EngineName,
                EngineVersion,
                normalizedTarget,
                profile.Key,
                profile.DisplayName,
                normalizedCustomPortsText,
                effectivePortsText,
                BuildPseudoCommandLine(normalizedTarget, profile, effectivePortsText),
                true,
                false,
                3,
                0,
                0,
                0,
                [],
                [],
                $"未能解析目标：{normalizedTarget}",
                ex.Message,
                string.Empty,
                string.Empty,
                []);
        }

        var resolvedAddresses = resolutionInfo.Addresses;
        if (resolvedAddresses.Count == 0)
        {
            return new PortScanResult(
                DateTimeOffset.Now,
                true,
                EngineName,
                EngineVersion,
                normalizedTarget,
                profile.Key,
                profile.DisplayName,
                normalizedCustomPortsText,
                effectivePortsText,
                BuildPseudoCommandLine(normalizedTarget, profile, effectivePortsText),
                true,
                false,
                3,
                0,
                0,
                0,
                [],
                [],
                $"未能解析目标：{normalizedTarget}",
                resolutionInfo.Summary,
                string.Empty,
                string.Empty,
                resolutionInfo.SystemAddresses);
        }

        var resolvedAddressTexts = resolvedAddresses.Select(static address => address.ToString()).ToArray();
        var resolutionNote = resolutionInfo.Source is "system-dns-filtered" or "public-doh" or "system-dns-fallback"
            ? $" 解析说明：{resolutionInfo.Summary}"
            : string.Empty;
        var udpPorts = profile.EnableUdpProbe ? GetUdpProbePorts(ports) : Array.Empty<int>();
        var attempts = BuildAttempts(resolvedAddresses, ports, udpPorts);
        var totalEndpoints = attempts.Count;

        progress?.Report(new PortScanProgressUpdate(
            DateTimeOffset.Now,
            0,
            totalEndpoints,
            0,
            normalizedTarget,
            $"已解析 {resolvedAddressTexts.Length} 个地址：{string.Join(", ", resolvedAddressTexts)}；准备执行 {ports.Count} 个 TCP 端口与 {udpPorts.Length} 个 UDP 端口探测。{resolutionNote}",
            null));

        var findings = await ScanEndpointsAsync(probeTarget, attempts, profile, progress, cancellationToken);
        var openPortCount = findings.Select(static finding => finding.Port).Distinct().Count();
        var openEndpointCount = findings.Count;
        var summary = openEndpointCount == 0
            ? $"{profile.DisplayName}：{normalizedTarget} 未发现可确认开放的端点。{resolutionNote}"
            : $"{profile.DisplayName}：{normalizedTarget} 发现 {openEndpointCount} 个开放端点，覆盖 {openPortCount} 个端口。{resolutionNote}";

        progress?.Report(new PortScanProgressUpdate(
            DateTimeOffset.Now,
            totalEndpoints,
            totalEndpoints,
            openEndpointCount,
            normalizedTarget,
            summary,
            null));

        var standardOutput = BuildRawOutput(
            normalizedTarget,
            profile,
            normalizedCustomPortsText,
            effectivePortsText,
            resolvedAddressTexts,
            resolutionInfo.SystemAddresses,
            resolutionInfo.Source,
            resolutionInfo.Summary,
            findings,
            totalEndpoints,
            summary,
            udpPorts);

        return new PortScanResult(
            DateTimeOffset.Now,
            true,
            EngineName,
            EngineVersion,
            normalizedTarget,
            profile.Key,
            profile.DisplayName,
            normalizedCustomPortsText,
            effectivePortsText,
            BuildPseudoCommandLine(normalizedTarget, profile, effectivePortsText),
            true,
            true,
            0,
            openPortCount,
            openEndpointCount,
            totalEndpoints,
            resolvedAddressTexts,
            findings,
            summary,
            null,
            standardOutput,
            string.Empty,
            resolutionInfo.SystemAddresses);
    }

    private static IReadOnlyList<int> ResolvePorts(
        PortScanProfile profile,
        string normalizedCustomPortsText,
        out string effectivePortsText,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(normalizedCustomPortsText))
        {
            effectivePortsText = profile.PortListText;
            error = null;
            return profile.DefaultPorts.Distinct().OrderBy(static port => port).ToArray();
        }

        HashSet<int> ports = [];
        var tokens = normalizedCustomPortsText
            .Replace('，', ',')
            .Replace('；', ';')
            .Replace('、', ',')
            .Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (token.Contains('-', StringComparison.Ordinal))
            {
                var parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], out var startPort) ||
                    !int.TryParse(parts[1], out var endPort))
                {
                    effectivePortsText = string.Empty;
                    error = $"无法解析端口范围：{token}";
                    return [];
                }

                if (!IsValidPort(startPort) || !IsValidPort(endPort) || startPort > endPort)
                {
                    effectivePortsText = string.Empty;
                    error = $"端口范围无效：{token}";
                    return [];
                }

                for (var port = startPort; port <= endPort; port++)
                {
                    ports.Add(port);
                    if (ports.Count > MaxCustomPortCount)
                    {
                        effectivePortsText = string.Empty;
                        error = $"自定义端口数量超过上限 {MaxCustomPortCount}。";
                        return [];
                    }
                }

                continue;
            }

            if (!int.TryParse(token, out var singlePort) || !IsValidPort(singlePort))
            {
                effectivePortsText = string.Empty;
                error = $"端口无效：{token}";
                return [];
            }

            ports.Add(singlePort);
            if (ports.Count > MaxCustomPortCount)
            {
                effectivePortsText = string.Empty;
                error = $"自定义端口数量超过上限 {MaxCustomPortCount}。";
                return [];
            }
        }

        if (ports.Count == 0)
        {
            effectivePortsText = string.Empty;
            error = "未解析出任何端口。";
            return [];
        }

        var orderedPorts = ports.OrderBy(static port => port).ToArray();
        effectivePortsText = string.Join(", ", orderedPorts);
        error = null;
        return orderedPorts;
    }

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

    private static async Task<ProbeOutcome> TryProbeApplicationProtocolAsync(
        string target,
        IPAddress address,
        int port,
        bool useTls,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        return port switch
        {
            21 => await TryProbeTextCommandAsync(target, address, port, false, "SYST\r\n", timeoutMilliseconds, "FTP", cancellationToken),
            25 or 587 => await TryProbeTextCommandAsync(target, address, port, false, "EHLO relaybench.local\r\n", timeoutMilliseconds, "SMTP", cancellationToken),
            110 => await TryProbeTextCommandAsync(target, address, port, false, "CAPA\r\n", timeoutMilliseconds, "POP3", cancellationToken),
            143 => await TryProbeTextCommandAsync(target, address, port, false, "a001 CAPABILITY\r\n", timeoutMilliseconds, "IMAP", cancellationToken),
            465 when useTls => await TryProbeTextCommandAsync(target, address, port, true, "EHLO relaybench.local\r\n", timeoutMilliseconds, "SMTPS", cancellationToken),
            993 when useTls => await TryProbeTextCommandAsync(target, address, port, true, "a001 CAPABILITY\r\n", timeoutMilliseconds, "IMAPS", cancellationToken),
            995 when useTls => await TryProbeTextCommandAsync(target, address, port, true, "CAPA\r\n", timeoutMilliseconds, "POP3S", cancellationToken),
            53 => await TryProbeDnsTcpAsync(address, port, timeoutMilliseconds, cancellationToken),
            _ => default
        };
    }

    private static async Task<UdpProbeResult> TryProbeUdpAsync(
        string target,
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        return port switch
        {
            53 => await TryProbeDnsUdpAsync(target, address, port, timeoutMilliseconds, cancellationToken),
            123 => await TryProbeNtpAsync(address, port, timeoutMilliseconds, cancellationToken),
            1900 => await TryProbeSsdpAsync(address, port, timeoutMilliseconds, cancellationToken),
            3478 => await TryProbeStunUdpAsync(address, port, timeoutMilliseconds, cancellationToken),
            _ => default
        };
    }

    private static async Task<long?> TryMeasureConnectLatencyAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            stopwatch.Stop();
            return stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task ConnectWithTimeoutAsync(
        TcpClient client,
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMilliseconds);
        await client.ConnectAsync(address, port, timeoutCts.Token);
    }

    private static async Task<ProbeOutcome> TryReadPassiveBannerAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var stream = client.GetStream();
            var banner = await ReadTextAsync(stream, 512, Math.Min(timeoutMilliseconds, 450), cancellationToken);
            if (string.IsNullOrWhiteSpace(banner))
            {
                return default;
            }

            return new ProbeOutcome(true, ShortenText(banner, 180), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> TryProbeTlsAsync(
        string target,
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var networkStream = client.GetStream();
            using SslStream sslStream = new(
                networkStream,
                leaveInnerStreamOpen: false,
                static (_, _, _, _) => true);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);

            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = target,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                timeoutCts.Token);

            X509Certificate2? certificate = sslStream.RemoteCertificate is null
                ? null
                : new X509Certificate2(sslStream.RemoteCertificate);

            List<string> summaryParts = [sslStream.SslProtocol.ToString()];

            if (certificate is not null)
            {
                var certificateName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
                if (string.IsNullOrWhiteSpace(certificateName))
                {
                    certificateName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                }

                if (!string.IsNullOrWhiteSpace(certificateName))
                {
                    summaryParts.Add($"证书={certificateName}");
                }

                summaryParts.Add($"到期={certificate.NotAfter:yyyy-MM-dd}");
            }

            return new ProbeOutcome(true, string.Join("; ", summaryParts), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeOutcome(false, null, "握手超时");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> TryProbeHttpAsync(
        string target,
        IPAddress address,
        int port,
        bool useTls,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var networkStream = client.GetStream();

            if (useTls)
            {
                using SslStream sslStream = new(networkStream, leaveInnerStreamOpen: false, static (_, _, _, _) => true);
                using var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                authCts.CancelAfter(timeoutMilliseconds);
                await sslStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = target,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    },
                    authCts.Token);

                return await SendHttpHeadAsync(sslStream, target, timeoutMilliseconds, cancellationToken);
            }

            return await SendHttpHeadAsync(networkStream, target, timeoutMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> SendHttpHeadAsync(
        Stream stream,
        string target,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var hostHeader = target.Contains(':') && !target.Contains(']')
            ? $"[{target}]"
            : target;

        var requestText =
            $"HEAD / HTTP/1.1\r\nHost: {hostHeader}\r\nUser-Agent: RelayBench/{EngineVersion}\r\nAccept: */*\r\nConnection: close\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(requestText);

        await stream.WriteAsync(requestBytes.AsMemory(0, requestBytes.Length), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var responseText = await ReadTextAsync(stream, 2048, timeoutMilliseconds, cancellationToken);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return default;
        }

        var summary = BuildHttpSummary(responseText);
        return string.IsNullOrWhiteSpace(summary)
            ? default
            : new ProbeOutcome(true, summary, null);
    }

    private static async Task<ProbeOutcome> TryProbeRedisAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var stream = client.GetStream();

            var payload = Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n");
            await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var responseText = await ReadTextAsync(stream, 256, timeoutMilliseconds, cancellationToken);
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return default;
            }

            var summary = ShortenText(responseText, 80);
            if (summary.Contains("PONG", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeOutcome(true, $"Redis {summary}", null);
            }

            return default;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> TryProbeTextCommandAsync(
        string target,
        IPAddress address,
        int port,
        bool useTls,
        string requestText,
        int timeoutMilliseconds,
        string prefix,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var networkStream = client.GetStream();
            Stream stream = networkStream;
            SslStream? sslStream = null;

            if (useTls)
            {
                sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false, static (_, _, _, _) => true);
                using var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                authCts.CancelAfter(timeoutMilliseconds);
                await sslStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = target,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    },
                    authCts.Token);
                stream = sslStream;
            }

            try
            {
                _ = await ReadTextAsync(stream, 512, Math.Min(timeoutMilliseconds, 250), cancellationToken);
                var payload = Encoding.ASCII.GetBytes(requestText);
                await stream.WriteAsync(payload.AsMemory(0, payload.Length), cancellationToken);
                await stream.FlushAsync(cancellationToken);

                var responseText = await ReadTextAsync(stream, 2048, timeoutMilliseconds, cancellationToken);
                if (string.IsNullOrWhiteSpace(responseText))
                {
                    return default;
                }

                return new ProbeOutcome(true, NormalizeProtocolSummary(prefix, responseText), null);
            }
            finally
            {
                sslStream?.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<ProbeOutcome> TryProbeDnsTcpAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var transactionId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
            var queryBytes = BuildDnsQueryPacket("example.com", transactionId);

            using TcpClient client = new(address.AddressFamily);
            await ConnectWithTimeoutAsync(client, address, port, timeoutMilliseconds, cancellationToken);
            using var stream = client.GetStream();

            var framed = new byte[queryBytes.Length + 2];
            framed[0] = (byte)((queryBytes.Length >> 8) & 0xFF);
            framed[1] = (byte)(queryBytes.Length & 0xFF);
            Buffer.BlockCopy(queryBytes, 0, framed, 2, queryBytes.Length);

            await stream.WriteAsync(framed.AsMemory(0, framed.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var header = await ReadExactAsync(stream, 2, timeoutMilliseconds, cancellationToken);
            if (header is null || header.Length < 2)
            {
                return default;
            }

            var responseLength = (header[0] << 8) | header[1];
            if (responseLength <= 0 || responseLength > 4096)
            {
                return default;
            }

            var responseBytes = await ReadExactAsync(stream, responseLength, timeoutMilliseconds, cancellationToken);
            if (responseBytes is null)
            {
                return default;
            }

            var summary = BuildDnsSummary(responseBytes, useUdp: false);
            return string.IsNullOrWhiteSpace(summary)
                ? default
                : new ProbeOutcome(true, summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeOutcome(false, null, ex.Message);
        }
    }

    private static async Task<UdpProbeResult> TryProbeDnsUdpAsync(
        string target,
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var domain = IsLikelyDnsName(target) ? target : "example.com";
            var transactionId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
            var queryBytes = BuildDnsQueryPacket(domain, transactionId);
            var exchange = await SendUdpPayloadAsync(address, port, queryBytes, timeoutMilliseconds, cancellationToken);
            if (exchange is null)
            {
                return default;
            }

            var summary = BuildDnsSummary(exchange.Value.ResponseBytes, useUdp: true);
            return string.IsNullOrWhiteSpace(summary)
                ? default
                : new UdpProbeResult(true, exchange.Value.RoundTripMilliseconds, "dns", summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UdpProbeResult(false, 0, "dns", null, ex.Message);
        }
    }

    private static async Task<UdpProbeResult> TryProbeNtpAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] payload = new byte[48];
            payload[0] = 0x1B;

            var exchange = await SendUdpPayloadAsync(address, port, payload, timeoutMilliseconds, cancellationToken);
            if (exchange is null || exchange.Value.ResponseBytes.Length < 48)
            {
                return default;
            }

            var response = exchange.Value.ResponseBytes;
            var version = (response[0] >> 3) & 0x07;
            var mode = response[0] & 0x07;
            var stratum = response[1];
            var summary = $"NTP v{version}; mode={mode}; stratum={stratum}";
            return new UdpProbeResult(true, exchange.Value.RoundTripMilliseconds, "ntp", summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UdpProbeResult(false, 0, "ntp", null, ex.Message);
        }
    }

    private static async Task<UdpProbeResult> TryProbeSsdpAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestText =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST:239.255.255.250:1900\r\n" +
                "MAN:\"ssdp:discover\"\r\n" +
                "MX:1\r\n" +
                "ST:ssdp:all\r\n\r\n";
            var payload = Encoding.ASCII.GetBytes(requestText);
            var exchange = await SendUdpPayloadAsync(address, port, payload, timeoutMilliseconds, cancellationToken);
            if (exchange is null)
            {
                return default;
            }

            var responseText = SanitizeText(Encoding.ASCII.GetString(exchange.Value.ResponseBytes));
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return default;
            }

            var lines = responseText.Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<string> parts = [];
            if (lines.Length > 0)
            {
                parts.Add(lines[0]);
            }

            foreach (var headerName in new[] { "ST:", "SERVER:", "LOCATION:" })
            {
                var match = lines.FirstOrDefault(line => line.StartsWith(headerName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                {
                    parts.Add(match);
                }
            }

            var summary = ShortenText(string.Join("; ", parts), 220);
            return new UdpProbeResult(true, exchange.Value.RoundTripMilliseconds, "ssdp", summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UdpProbeResult(false, 0, "ssdp", null, ex.Message);
        }
    }

    private static async Task<UdpProbeResult> TryProbeStunUdpAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] transactionId = RandomNumberGenerator.GetBytes(12);
            byte[] payload = new byte[20];
            payload[0] = 0x00;
            payload[1] = 0x01;
            payload[2] = 0x00;
            payload[3] = 0x00;
            payload[4] = 0x21;
            payload[5] = 0x12;
            payload[6] = 0xA4;
            payload[7] = 0x42;
            Buffer.BlockCopy(transactionId, 0, payload, 8, transactionId.Length);

            var exchange = await SendUdpPayloadAsync(address, port, payload, timeoutMilliseconds, cancellationToken);
            if (exchange is null)
            {
                return default;
            }

            var summary = BuildStunSummary(exchange.Value.ResponseBytes, transactionId);
            return string.IsNullOrWhiteSpace(summary)
                ? default
                : new UdpProbeResult(true, exchange.Value.RoundTripMilliseconds, "stun", summary, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UdpProbeResult(false, 0, "stun", null, ex.Message);
        }
    }

    private static async Task<UdpExchangeResult?> SendUdpPayloadAsync(
        IPAddress address,
        int port,
        byte[] payload,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using Socket socket = new(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);

            await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token);
            await socket.SendAsync(payload, SocketFlags.None, timeoutCts.Token);

            byte[] responseBuffer = new byte[4096];
            var receivedCount = await socket.ReceiveAsync(responseBuffer, SocketFlags.None, timeoutCts.Token);
            if (receivedCount <= 0)
            {
                return null;
            }

            stopwatch.Stop();
            var responseBytes = new byte[receivedCount];
            Buffer.BlockCopy(responseBuffer, 0, responseBytes, 0, receivedCount);
            return new UdpExchangeResult(responseBytes, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadTextAsync(
        Stream stream,
        int maxBytes,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Math.Min(512, maxBytes)];
        using MemoryStream collector = new();

        while (collector.Length < maxBytes)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);

            int readCount;
            try
            {
                var remaining = Math.Min(buffer.Length, maxBytes - (int)collector.Length);
                readCount = await stream.ReadAsync(buffer.AsMemory(0, remaining), timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (readCount <= 0)
            {
                break;
            }

            collector.Write(buffer, 0, readCount);

            var snapshot = Encoding.ASCII.GetString(collector.ToArray());
            if (snapshot.Contains("\r\n\r\n", StringComparison.Ordinal) ||
                snapshot.Contains("\n\n", StringComparison.Ordinal))
            {
                break;
            }

            if (readCount < buffer.Length)
            {
                break;
            }
        }

        if (collector.Length == 0)
        {
            return null;
        }

        return SanitizeText(Encoding.ASCII.GetString(collector.ToArray()));
    }

    private static async Task<byte[]?> ReadExactAsync(
        Stream stream,
        int byteCount,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[byteCount];
        var offset = 0;

        while (offset < byteCount)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);

            int readCount;
            try
            {
                readCount = await stream.ReadAsync(buffer.AsMemory(offset, byteCount - offset), timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (readCount <= 0)
            {
                return null;
            }

            offset += readCount;
        }

        return buffer;
    }

    private static string BuildHttpSummary(string responseText)
    {
        var lines = responseText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        List<string> parts = [lines[0]];

        var serverHeader = lines.FirstOrDefault(static line => line.StartsWith("Server:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(serverHeader))
        {
            parts.Add(serverHeader);
        }

        var locationHeader = lines.FirstOrDefault(static line => line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(locationHeader))
        {
            parts.Add(locationHeader);
        }

        return ShortenText(string.Join("; ", parts), 220);
    }

    private static string NormalizeProtocolSummary(string prefix, string responseText)
    {
        var lines = responseText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(4)
            .ToArray();

        if (lines.Length == 0)
        {
            return string.Empty;
        }

        return ShortenText($"{prefix} {string.Join("; ", lines)}", 220);
    }

    private static byte[] BuildDnsQueryPacket(string domain, ushort transactionId)
    {
        var normalizedDomain = string.IsNullOrWhiteSpace(domain) ? "example.com" : domain.Trim().Trim('.');
        List<byte> packet = new();
        packet.Add((byte)((transactionId >> 8) & 0xFF));
        packet.Add((byte)(transactionId & 0xFF));
        packet.Add(0x01);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x01);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x00);

        foreach (var label in normalizedDomain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            packet.Add((byte)Math.Min(bytes.Length, 63));
            packet.AddRange(bytes.Take(63));
        }

        packet.Add(0x00);
        packet.Add(0x00);
        packet.Add(0x01);
        packet.Add(0x00);
        packet.Add(0x01);
        return packet.ToArray();
    }

    private static string BuildDnsSummary(byte[] responseBytes, bool useUdp)
    {
        if (responseBytes.Length < 12)
        {
            return string.Empty;
        }

        var answerCount = (responseBytes[6] << 8) | responseBytes[7];
        var authorityCount = (responseBytes[8] << 8) | responseBytes[9];
        var additionalCount = (responseBytes[10] << 8) | responseBytes[11];
        var flags = (responseBytes[2] << 8) | responseBytes[3];
        var rcode = flags & 0x000F;
        var transport = useUdp ? "UDP" : "TCP";
        return $"DNS/{transport}; answers={answerCount}; authority={authorityCount}; additional={additionalCount}; rcode={rcode}";
    }

    private static string BuildStunSummary(byte[] responseBytes, byte[] expectedTransactionId)
    {
        if (responseBytes.Length < 20 ||
            responseBytes[0] != 0x01 ||
            responseBytes[1] != 0x01)
        {
            return string.Empty;
        }

        for (var index = 0; index < expectedTransactionId.Length; index++)
        {
            if (responseBytes[8 + index] != expectedTransactionId[index])
            {
                return string.Empty;
            }
        }

        var attributeOffset = 20;
        string? mappedAddress = null;
        while (attributeOffset + 4 <= responseBytes.Length)
        {
            var attributeType = (responseBytes[attributeOffset] << 8) | responseBytes[attributeOffset + 1];
            var attributeLength = (responseBytes[attributeOffset + 2] << 8) | responseBytes[attributeOffset + 3];
            var attributeValueOffset = attributeOffset + 4;
            if (attributeValueOffset + attributeLength > responseBytes.Length)
            {
                break;
            }

            if (attributeType is 0x0001 or 0x0020)
            {
                mappedAddress = TryParseStunMappedAddress(responseBytes, attributeValueOffset, attributeLength, attributeType == 0x0020);
                if (!string.IsNullOrWhiteSpace(mappedAddress))
                {
                    break;
                }
            }

            attributeOffset = attributeValueOffset + attributeLength;
            while (attributeOffset % 4 != 0)
            {
                attributeOffset++;
            }
        }

        return string.IsNullOrWhiteSpace(mappedAddress)
            ? "STUN Binding Success"
            : $"STUN Binding Success; mapped={mappedAddress}";
    }

    private static string? TryParseStunMappedAddress(byte[] buffer, int offset, int length, bool xorMapped)
    {
        if (length < 4)
        {
            return null;
        }

        var family = buffer[offset + 1];
        var port = (buffer[offset + 2] << 8) | buffer[offset + 3];
        if (xorMapped)
        {
            port ^= 0x2112;
        }

        if (family == 0x01 && length >= 8)
        {
            byte[] addressBytes = new byte[4];
            Buffer.BlockCopy(buffer, offset + 4, addressBytes, 0, 4);
            if (xorMapped)
            {
                addressBytes[0] ^= 0x21;
                addressBytes[1] ^= 0x12;
                addressBytes[2] ^= 0xA4;
                addressBytes[3] ^= 0x42;
            }

            return $"{new IPAddress(addressBytes)}:{port}";
        }

        if (family == 0x02 && length >= 20)
        {
            byte[] addressBytes = new byte[16];
            Buffer.BlockCopy(buffer, offset + 4, addressBytes, 0, 16);
            if (xorMapped)
            {
                byte[] cookieAndTransaction =
                [
                    0x21, 0x12, 0xA4, 0x42,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0
                ];
                for (var index = 0; index < addressBytes.Length; index++)
                {
                    addressBytes[index] ^= cookieAndTransaction[index];
                }
            }

            return $"[{new IPAddress(addressBytes)}]:{port}";
        }

        return null;
    }

    private static string DeriveServiceHint(
        int port,
        string? bannerSummary,
        string? tlsSummary,
        string? httpSummary,
        string? redisSummary,
        string? appSummary)
    {
        if (!string.IsNullOrWhiteSpace(redisSummary))
        {
            return "redis";
        }

        if (!string.IsNullOrWhiteSpace(appSummary))
        {
            if (appSummary.StartsWith("DNS", StringComparison.OrdinalIgnoreCase))
            {
                return "dns";
            }

            if (appSummary.StartsWith("SMTP", StringComparison.OrdinalIgnoreCase) ||
                appSummary.StartsWith("SMTPS", StringComparison.OrdinalIgnoreCase))
            {
                return port is 465 ? "smtps" : "smtp";
            }

            if (appSummary.StartsWith("POP3", StringComparison.OrdinalIgnoreCase) ||
                appSummary.StartsWith("POP3S", StringComparison.OrdinalIgnoreCase))
            {
                return port is 995 ? "pop3s" : "pop3";
            }

            if (appSummary.StartsWith("IMAP", StringComparison.OrdinalIgnoreCase) ||
                appSummary.StartsWith("IMAPS", StringComparison.OrdinalIgnoreCase))
            {
                return port is 993 ? "imaps" : "imap";
            }

            if (appSummary.StartsWith("FTP", StringComparison.OrdinalIgnoreCase))
            {
                return "ftp";
            }
        }

        if (!string.IsNullOrWhiteSpace(httpSummary))
        {
            return TlsPorts.Contains(port) ? "https" : "http";
        }

        if (!string.IsNullOrWhiteSpace(bannerSummary))
        {
            if (bannerSummary.StartsWith("SSH-", StringComparison.OrdinalIgnoreCase))
            {
                return "ssh";
            }

            if (bannerSummary.StartsWith("220", StringComparison.OrdinalIgnoreCase) && port is 21)
            {
                return "ftp";
            }

            if (bannerSummary.StartsWith("220", StringComparison.OrdinalIgnoreCase) && (port is 25 or 465 or 587))
            {
                return "smtp";
            }

            if (bannerSummary.StartsWith("+OK", StringComparison.OrdinalIgnoreCase))
            {
                return "pop3";
            }

            if (bannerSummary.StartsWith("* OK", StringComparison.OrdinalIgnoreCase))
            {
                return "imap";
            }

            if (bannerSummary.Contains("mysql", StringComparison.OrdinalIgnoreCase))
            {
                return "mysql";
            }
        }

        if (!string.IsNullOrWhiteSpace(tlsSummary))
        {
            return port is 465 ? "smtps" : "tls";
        }

        return KnownServiceHints.TryGetValue(port, out var knownService)
            ? knownService
            : "tcp";
    }

    private static bool ShouldProbePassiveBanner(int port) => PassiveBannerPorts.Contains(port);

    private static bool ShouldProbeHttp(int port) => HttpPorts.Contains(port);

    private static bool ShouldProbeTls(int port) => TlsPorts.Contains(port);

    private static bool ShouldProbeUdp(int port) => UdpProbePorts.Contains(port);

    private static async Task<AddressResolutionInfo> ResolveTargetAddressesAsync(string target, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out var parsedAddress))
        {
            var addressText = parsedAddress.ToString();
            return new AddressResolutionInfo(
                [parsedAddress],
                [addressText],
                "literal-ip",
                $"目标本身就是 IP 地址 {addressText}。");
        }

        IReadOnlyList<IPAddress> systemAddresses = Array.Empty<IPAddress>();
        Exception? systemException = null;
        try
        {
            systemAddresses = await ResolveSystemTargetAddressesAsync(target);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            systemException = ex;
        }

        var systemAddressTexts = systemAddresses.Select(static address => address.ToString()).ToArray();
        if (!IsLikelyDnsName(target))
        {
            if (systemException is not null)
            {
                throw new InvalidOperationException($"系统 DNS 解析失败：{systemException.Message}", systemException);
            }

            return new AddressResolutionInfo(
                systemAddresses,
                systemAddressTexts,
                "system-dns",
                systemAddresses.Count == 0
                    ? "系统 DNS 未返回可用地址。"
                    : $"使用系统 DNS 解析结果：{DescribeAddresses(systemAddressTexts)}。");
        }

        var routableSystemAddresses = FilterPublicRoutableAddresses(systemAddresses);
        if (routableSystemAddresses.Count > 0)
        {
            return new AddressResolutionInfo(
                routableSystemAddresses,
                systemAddressTexts,
                systemAddresses.Count == routableSystemAddresses.Count ? "system-dns" : "system-dns-filtered",
                systemAddresses.Count == routableSystemAddresses.Count
                    ? $"使用系统 DNS 解析结果：{DescribeIpAddresses(routableSystemAddresses)}。"
                    : $"系统 DNS 同时返回了公网与保留地址，已自动过滤并保留公网可路由地址：{DescribeIpAddresses(routableSystemAddresses)}。");
        }

        if (IsKnownLocalOnlyHost(target) && systemAddresses.Count > 0)
        {
            return new AddressResolutionInfo(
                systemAddresses,
                systemAddressTexts,
                "system-dns-fallback",
                $"目标看起来是局域网 / 本地域名，系统 DNS 返回 {DescribeAddresses(systemAddressTexts)}；将继续使用系统解析结果。");
        }

        var publicDnsLookupHost = NormalizeDnsHostForNetworkApis(target);
        var publicDnsAddresses = await ResolvePublicDnsAddressesAsync(publicDnsLookupHost, cancellationToken);
        var routablePublicDnsAddresses = FilterPublicRoutableAddresses(publicDnsAddresses);
        if (routablePublicDnsAddresses.Count > 0)
        {
            var systemAddressText = systemAddressTexts.Length == 0
                ? systemException is null
                    ? "系统 DNS 未返回地址"
                    : $"系统 DNS 解析失败：{systemException.Message}"
                : $"系统 DNS 返回 {DescribeAddresses(systemAddressTexts)}";
            var reason = systemAddressTexts.Length > 0 && ContainsSyntheticBenchmarkAddress(systemAddresses)
                ? "系统 DNS 返回了保留的 Fake-IP / Benchmark 地址"
                : "系统 DNS 没有返回可公网路由地址";

            return new AddressResolutionInfo(
                routablePublicDnsAddresses,
                systemAddressTexts,
                "public-doh",
                $"{reason}（{systemAddressText}），已自动回退公共 DoH：{DescribeIpAddresses(routablePublicDnsAddresses)}。");
        }

        if (systemAddresses.Count > 0 && ContainsSyntheticBenchmarkAddress(systemAddresses))
        {
            var lookupNote = string.Equals(publicDnsLookupHost, target, StringComparison.OrdinalIgnoreCase)
                ? "公共 DoH 未返回任何 A / AAAA 记录"
                : $"公共 DoH（查询 {publicDnsLookupHost}）未返回任何 A / AAAA 记录";

            return new AddressResolutionInfo(
                Array.Empty<IPAddress>(),
                systemAddressTexts,
                "blocked-fake-ip",
                $"系统 DNS 返回了保留的 Fake-IP / Benchmark 地址 {DescribeAddresses(systemAddressTexts)}，且{lookupNote}；为避免误扫 198.18/15 假地址，已停止本次端口扫描。");
        }

        if (systemAddresses.Count > 0)
        {
            return new AddressResolutionInfo(
                systemAddresses,
                systemAddressTexts,
                "system-dns-fallback",
                $"系统 DNS 仅返回不可公网路由地址 {DescribeAddresses(systemAddressTexts)}，且公共 DoH 也未拿到可用结果；已继续使用系统解析结果，结果可能受本地 DNS 或 Fake-IP 影响。");
        }

        if (systemException is not null)
        {
            throw new InvalidOperationException($"系统 DNS 解析失败：{systemException.Message}；公共 DoH 也未返回可用地址。", systemException);
        }

        return new AddressResolutionInfo(
            Array.Empty<IPAddress>(),
            Array.Empty<string>(),
            "unresolved",
            "系统 DNS 与公共 DoH 都未返回可用的 IPv4 / IPv6 地址。");
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveSystemTargetAddressesAsync(string target)
        => NormalizeIpAddresses(await Dns.GetHostAddressesAsync(target));

    private static async Task<IReadOnlyList<IPAddress>> ResolvePublicDnsAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var resolverTasks = new[]
        {
            ResolveGoogleDnsAddressesAsync(host, cancellationToken),
            ResolveCloudflareDnsAddressesAsync(host, cancellationToken)
        };

        await Task.WhenAll(resolverTasks);

        List<IPAddress> addresses = [];
        foreach (var resolverTask in resolverTasks)
        {
            addresses.AddRange(resolverTask.Result);
        }

        return NormalizeIpAddresses(addresses);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveGoogleDnsAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var lookupTasks = new[]
        {
            QueryDnsJsonAsync($"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A", acceptsDnsJson: false, cancellationToken),
            QueryDnsJsonAsync($"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=AAAA", acceptsDnsJson: false, cancellationToken)
        };

        await Task.WhenAll(lookupTasks);

        List<IPAddress> addresses = [];
        foreach (var lookupTask in lookupTasks)
        {
            addresses.AddRange(lookupTask.Result);
        }

        return NormalizeIpAddresses(addresses);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveCloudflareDnsAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var lookupTasks = new[]
        {
            QueryDnsJsonAsync($"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A", acceptsDnsJson: true, cancellationToken),
            QueryDnsJsonAsync($"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=AAAA", acceptsDnsJson: true, cancellationToken)
        };

        await Task.WhenAll(lookupTasks);

        List<IPAddress> addresses = [];
        foreach (var lookupTask in lookupTasks)
        {
            addresses.AddRange(lookupTask.Result);
        }

        return NormalizeIpAddresses(addresses);
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryDnsJsonAsync(string url, bool acceptsDnsJson, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (acceptsDnsJson)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
            }

            using var response = await PublicDnsHttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseDnsJsonAddresses(json);
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static IReadOnlyList<IPAddress> ParseDnsJsonAddresses(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<IPAddress>();
            }

            return NormalizeIpAddresses(
                answers.EnumerateArray()
                    .Select(answer =>
                    {
                        if (!answer.TryGetProperty("type", out var typeElement) ||
                            !answer.TryGetProperty("data", out var dataElement) ||
                            dataElement.ValueKind != JsonValueKind.String)
                        {
                            return null;
                        }

                        var recordType = typeElement.ValueKind == JsonValueKind.Number ? typeElement.GetInt32() : -1;
                        var value = dataElement.GetString();
                        if (recordType is not 1 and not 28 || string.IsNullOrWhiteSpace(value))
                        {
                            return null;
                        }

                        return IPAddress.TryParse(value, out var address) ? address : null;
                    })
                    .Where(static address => address is not null)
                    .Cast<IPAddress>());
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static IReadOnlyList<IPAddress> NormalizeIpAddresses(IEnumerable<IPAddress> addresses)
        => addresses
            .Where(static address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .DistinctBy(static address => address.ToString())
            .OrderBy(static address => address.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0)
            .ThenBy(static address => address.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<IPAddress> FilterPublicRoutableAddresses(IEnumerable<IPAddress> addresses)
        => NormalizeIpAddresses(addresses.Where(IsPublicRoutableAddress));

    private static bool ContainsSyntheticBenchmarkAddress(IEnumerable<IPAddress> addresses)
        => addresses.Any(IsSyntheticBenchmarkAddress);

    private static bool IsPublicRoutableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIpv4Address(address),
            AddressFamily.InterNetworkV6 => IsPublicIpv6Address(address),
            _ => false
        };
    }

    private static bool IsPublicIpv4Address(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return !(
            bytes[0] == 10 ||
            bytes[0] == 0 ||
            bytes[0] == 127 ||
            (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
            (bytes[0] == 198 && bytes[1] is 18 or 19) ||
            (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
            (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
            bytes[0] >= 224);
    }

    private static bool IsPublicIpv6Address(IPAddress address)
    {
        if (address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal ||
            address.Equals(IPAddress.IPv6None) ||
            address.Equals(IPAddress.IPv6Loopback))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return false;
        }

        return !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
    }

    private static bool IsSyntheticBenchmarkAddress(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork &&
           address.GetAddressBytes() is [198, 18 or 19, ..];

    private static string NormalizeDnsHostForNetworkApis(string target)
    {
        var normalizedTarget = target.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalizedTarget) ||
            IPAddress.TryParse(normalizedTarget, out _) ||
            !IsLikelyDnsName(normalizedTarget))
        {
            return normalizedTarget;
        }

        if (normalizedTarget.All(static ch => ch <= 0x7F))
        {
            return normalizedTarget;
        }

        try
        {
            return new System.Globalization.IdnMapping().GetAscii(normalizedTarget);
        }
        catch (ArgumentException)
        {
            return normalizedTarget;
        }
    }

    private static bool IsKnownLocalOnlyHost(string target)
    {
        var normalizedTarget = target.Trim().TrimEnd('.');
        return normalizedTarget.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".home", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeIpAddresses(IReadOnlyList<IPAddress> addresses)
        => DescribeAddresses(addresses.Select(static address => address.ToString()).ToArray());

    private static string DescribeAddresses(IReadOnlyList<string> addresses)
    {
        if (addresses.Count == 0)
        {
            return "无";
        }

        return addresses.Count <= 4
            ? string.Join(", ", addresses)
            : string.Join(", ", addresses.Take(4)) + $" 等 {addresses.Count} 个地址";
    }

    private static HttpClient CreatePublicDnsHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/1.0 (Windows desktop diagnostics)");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private static string NormalizeCustomPortsText(string? customPortsText)
        => string.IsNullOrWhiteSpace(customPortsText)
            ? string.Empty
            : customPortsText.Trim();

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static bool IsLikelyDnsName(string target)
        => !IPAddress.TryParse(target, out _) && target.Contains('.', StringComparison.Ordinal);

    private static string BuildPseudoCommandLine(string target, PortScanProfile profile, string effectivePortsText)
        => $"builtin://port-scan --target {target} --ports {effectivePortsText} --timeout {profile.ConnectTimeoutMilliseconds} --concurrency {profile.MaxConcurrency} --protocols {(profile.EnableUdpProbe ? "tcp+udp" : "tcp")} --probes {profile.ProbeSummaryText.Replace(" / ", "+", StringComparison.Ordinal)}";

    private static string BuildRawOutput(
        string target,
        PortScanProfile profile,
        string customPortsText,
        string effectivePortsText,
        IReadOnlyList<string> resolvedAddresses,
        IReadOnlyList<string> systemResolvedAddresses,
        string resolutionSource,
        string resolutionSummary,
        IReadOnlyList<PortScanFinding> findings,
        int attemptedEndpointCount,
        string summary,
        IReadOnlyList<int> udpPorts)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Engine: {EngineName} {EngineVersion}");
        builder.AppendLine($"Target: {target}");
        builder.AppendLine($"Resolution source: {resolutionSource}");
        builder.AppendLine($"Resolved addresses: {(resolvedAddresses.Count == 0 ? "none" : string.Join(", ", resolvedAddresses))}");
        builder.AppendLine($"System-resolved addresses: {(systemResolvedAddresses.Count == 0 ? "none" : string.Join(", ", systemResolvedAddresses))}");
        builder.AppendLine($"Resolution note: {resolutionSummary}");
        builder.AppendLine($"Profile: {profile.DisplayName} ({profile.Key})");
        builder.AppendLine($"Custom ports: {(string.IsNullOrWhiteSpace(customPortsText) ? "none" : customPortsText)}");
        builder.AppendLine($"Effective ports: {effectivePortsText}");
        builder.AppendLine($"UDP probe ports: {(udpPorts.Count == 0 ? "none" : string.Join(", ", udpPorts))}");
        builder.AppendLine($"Concurrency: {profile.MaxConcurrency}");
        builder.AppendLine($"Connect timeout: {profile.ConnectTimeoutMilliseconds} ms");
        builder.AppendLine($"Probes: {profile.ProbeSummaryText}");
        builder.AppendLine($"Attempted endpoints: {attemptedEndpointCount}");
        builder.AppendLine();

        if (findings.Count == 0)
        {
            builder.AppendLine("No open TCP/UDP endpoints detected.");
        }
        else
        {
            foreach (var finding in findings)
            {
                builder.Append($"OPEN {finding.Endpoint}/{finding.Protocol}");
                builder.Append($" latency={finding.ConnectLatencyMilliseconds}ms");
                builder.Append($" service={finding.ServiceHint}");

                if (!string.IsNullOrWhiteSpace(finding.Banner))
                {
                    builder.Append($" banner=\"{finding.Banner}\"");
                }

                if (!string.IsNullOrWhiteSpace(finding.TlsSummary))
                {
                    builder.Append($" tls=\"{finding.TlsSummary}\"");
                }

                if (!string.IsNullOrWhiteSpace(finding.HttpSummary))
                {
                    builder.Append($" app=\"{finding.HttpSummary}\"");
                }

                if (!string.IsNullOrWhiteSpace(finding.ProbeNotes))
                {
                    builder.Append($" notes=\"{finding.ProbeNotes}\"");
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Summary: {summary}");
        return builder.ToString();
    }

    private static string FormatEndpoint(string address, int port)
        => address.Contains(':', StringComparison.Ordinal) && !address.StartsWith("[", StringComparison.Ordinal)
            ? $"[{address}]:{port}"
            : $"{address}:{port}";

    private static string ShortenText(string value, int maxLength)
    {
        var normalized = SanitizeText(value);
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..(maxLength - 1)] + "…";
    }

    private static string SanitizeText(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (var character in value)
        {
            if (character == '\r' || character == '\n' || character == '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder
            .ToString()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal)
            .Trim();
    }
}
