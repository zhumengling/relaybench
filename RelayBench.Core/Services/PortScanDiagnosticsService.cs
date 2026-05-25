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

    internal static IReadOnlyList<int> ResolvePorts(
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


}
