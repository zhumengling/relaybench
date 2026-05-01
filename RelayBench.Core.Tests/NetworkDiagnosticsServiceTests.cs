using System.Net;
using System.Net.Http;
using System.Text;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Core.Support;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class NetworkDiagnosticsServiceTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("route diagnostics parses tracert hops with timeout samples", () =>
    {
        var hops = RouteDiagnosticsService.ParseTraceHops(
            """
              1     <1 ms    <1 ms    <1 ms  192.168.1.1
              2      *        *        *     Request timed out.
              3      8 ms     9 ms    10 ms  edge.example [203.0.113.8]
            """);

        AssertTrue(hops.Count == 3, $"Expected 3 hops, got {hops.Count}.");
        AssertTrue(hops[0].TraceRoundTripTimes.SequenceEqual([1L, 1L, 1L]), "The <1 ms tracert token should be treated as 1 ms.");
        AssertTrue(hops[1].TimedOut, "A fully timed out hop should be marked timed out.");
        AssertEqual(hops[2].Address ?? string.Empty, "203.0.113.8");
        }, group: "network");

        yield return new TestCase("route diagnostics parses dns json and filters routable addresses", () =>
    {
        var addresses = RouteDiagnosticsService.ParseDnsJsonAddresses(
            """
            {
              "Answer": [
                { "type": 1, "data": "8.8.8.8" },
                { "type": 28, "data": "2001:4860:4860::8888" },
                { "type": 5, "data": "alias.example" },
                { "type": 1, "data": "not-an-ip" }
              ]
            }
            """);

        AssertTrue(addresses.SequenceEqual(["2001:4860:4860::8888", "8.8.8.8"], StringComparer.OrdinalIgnoreCase), "Only valid A/AAAA records should be returned.");
        AssertTrue(RouteDiagnosticsService.IsPublicRoutableAddress(IPAddress.Parse("8.8.8.8")), "Public Google DNS should be routable.");
        AssertFalse(RouteDiagnosticsService.IsPublicRoutableAddress(IPAddress.Parse("192.168.1.1")), "RFC1918 addresses are not public routable.");
        AssertFalse(RouteDiagnosticsService.IsPublicRoutableAddress(IPAddress.Parse("198.18.0.1")), "Benchmark/Fake-IP range must be filtered.");
        }, group: "network");

        yield return new TestCase("stun response parser decodes xor mapped and alternate addresses", () =>
    {
        var transactionId = Enumerable.Range(1, 12).Select(static value => (byte)value).ToArray();
        var responseBytes = BuildStunSuccessResponse(
            transactionId,
            BuildXorMappedAddressAttribute("203.0.113.9", 54321),
            BuildMappedAddressAttribute(0x802C, "203.0.113.10", 3479),
            BuildMappedAddressAttribute(0x802B, "203.0.113.11", 3478));

        var response = StunProbeService.ParseResponse(responseBytes, transactionId);

        AssertEqual(response.MappedAddress ?? string.Empty, "203.0.113.9:54321");
        AssertEqual(response.OtherAddress ?? string.Empty, "203.0.113.10:3479");
        AssertEqual(response.ResponseOrigin ?? string.Empty, "203.0.113.11:3478");
        AssertTrue(response.Attributes.ContainsKey("XOR-MAPPED-ADDRESS"), "XOR-MAPPED-ADDRESS should be recorded.");
        }, group: "network");

        yield return new TestCase("stun nat classification distinguishes common nat outcomes", () =>
    {
        AssertContains(
            StunNatClassificationHelper.ClassifyUdpNatType(
                behindNat: false,
                changeRequestSucceeded: true,
                changeRequestConfirmed: true,
                hasAlternateEndpoint: true,
                alternateBasicSucceeded: false,
                mappingChangedOnAlternate: false,
                changePortOnlySucceeded: false),
            "互联网");
        AssertContains(
            StunNatClassificationHelper.ClassifyUdpNatType(
                behindNat: true,
                changeRequestSucceeded: false,
                changeRequestConfirmed: false,
                hasAlternateEndpoint: true,
                alternateBasicSucceeded: true,
                mappingChangedOnAlternate: true,
                changePortOnlySucceeded: false),
            "NAT");
        }, group: "network");

        yield return new TestCase("port scan parses custom ports and rejects invalid ranges", () =>
    {
        var service = new PortScanDiagnosticsService();
        var profile = service.GetDefaultProfile();
        var ports = PortScanDiagnosticsService.ResolvePorts(profile, "443, 80, 8000-8002", out var effective, out var error);

        AssertTrue(error is null, error ?? "Unexpected port parse error.");
        AssertTrue(ports.SequenceEqual([80, 443, 8000, 8001, 8002]), $"Unexpected ports: {string.Join(",", ports)}.");
        AssertEqual(effective, "80, 443, 8000, 8001, 8002");

        _ = PortScanDiagnosticsService.ResolvePorts(profile, "65536", out _, out var invalidError);
        AssertTrue(invalidError is not null, "Port 65536 should be rejected.");
        }, group: "network");

        yield return new TestCase("port scan normalizes idn hosts and filters fake ip addresses", () =>
    {
        AssertEqual(PortScanDiagnosticsService.NormalizeDnsHostForNetworkApis("例子.测试"), "xn--fsqu00a.xn--0zwm56d");
        AssertFalse(PortScanDiagnosticsService.IsPublicRoutableAddress(IPAddress.Parse("10.0.0.1")), "Private address should not be routable.");
        AssertFalse(PortScanDiagnosticsService.IsPublicRoutableAddress(IPAddress.Parse("198.19.1.1")), "Benchmark/Fake-IP range should not be routable.");
        AssertTrue(PortScanDiagnosticsService.IsPublicRoutableAddress(IPAddress.Parse("1.1.1.1")), "Public resolver should be routable.");
        }, group: "network");

        yield return new TestCase("cloudflare speed metrics calculate percentile jitter and impact labels", () =>
    {
        AssertTrue(CloudflareSpeedTestService.Percentile([10d, 20d, 30d], 0.5) == 20d, "Median percentile should be 20.");
        AssertTrue(CloudflareSpeedTestService.CalculateJitter([10d, 20d, 15d]) == 7.5d, "Jitter should average adjacent deltas.");
        AssertTrue(CloudflareSpeedTestService.ComputeLoadedLatencyIncrease(20d, 70d, 45d) == 50d, "Loaded latency increase should use the worst loaded side.");
        AssertContains(CloudflareSpeedTestService.LabelGptImpact(88), "优");
        AssertContains(CloudflareSpeedTestService.FormatBandwidth(12_500_000), "Mbps");
        }, group: "network");

        yield return new TestCase("exit ip risk helpers validate target ip cidr and verdict thresholds", () =>
    {
        AssertTrue(ExitIpRiskReviewService.TryNormalizeTargetIp("[8.8.8.8]", out var normalized, out var error), error ?? "IP should parse.");
        AssertEqual(normalized ?? string.Empty, "8.8.8.8");
        AssertTrue(ExitIpRiskReviewService.IsAddressInCidr(IPAddress.Parse("203.0.113.10"), "203.0.113.0/24"), "CIDR match should work.");
        AssertFalse(ExitIpRiskReviewService.IsAddressInCidr(IPAddress.Parse("203.0.114.10"), "203.0.113.0/24"), "Different CIDR should not match.");
        AssertContains(ExitIpRiskReviewService.BuildRiskVerdict(null, null, null, true, null, null), "高");
        AssertContains(ExitIpRiskReviewService.BuildRiskVerdict(true, null, null, null, null, 40d), "注意");
        AssertContains(ExitIpRiskReviewService.BuildRiskVerdict(false, false, false, false, false, 2d), "通过");
        }, group: "network");

        yield return new TestCase("exit ip risk public run rejects invalid target without network", async cancellationToken =>
    {
        var service = new ExitIpRiskReviewService();
        var result = await service.RunAsync("not-an-ip", null, cancellationToken);

        AssertTrue(result.Error is not null, "Invalid target should produce an error.");
        AssertTrue(result.Sources.Count == 0, "Invalid target should not query risk sources.");
        AssertContains(result.Summary, "IPv");
        }, group: "network");

        yield return new TestCase("client api diagnostics aggregates installed configured and reachable clients", async cancellationToken =>
    {
        var environment = new InMemoryClientApiDiagnosticEnvironment();
        environment.AddCommandPath("codex", @"C:\Tools\codex.exe");
        environment.WriteFile(
            Path.Combine(environment.UserProfilePath, ".codex", "config.toml"),
            "base_url = \"http://127.0.0.1:15721/v1\"\nPROXY_MANAGED = \"true\"");
        environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:7890");

        var transport = new FakeClientApiProbeTransport((url, method, provider) =>
            Task.FromResult(provider == "OpenAI"
                ? new ClientApiProbeResponse(200, TimeSpan.FromMilliseconds(20), "ok", "models", null)
                : new ClientApiProbeResponse(null, null, "failed", null, "connection refused")));
        var service = new ClientApiDiagnosticsService(environment, transport);

        var result = await service.RunAsync(null, cancellationToken);
        var codex = result.Checks.First(check => check.Name == "Codex CLI");
        var claude = result.Checks.First(check => check.Name == "Claude CLI");

        AssertTrue(result.InstalledCount >= 1, "At least Codex CLI should be installed.");
        AssertTrue(result.ConfiguredCount >= 1, "Codex config should be detected.");
        AssertTrue(result.ReachableCount >= 1, "OpenAI probes should be reachable.");
        AssertTrue(codex.Installed && codex.ConfigDetected && codex.Reachable, codex.Summary);
        AssertContains(codex.AccessPathLabel, "本地");
        AssertFalse(claude.Reachable, "Fake Anthropic transport should fail.");
        }, group: "network");

        yield return new TestCase("trace document parser keeps valid key value lines only", () =>
    {
        var values = TraceDocumentParser.Parse(
            """
            ip=203.0.113.9
            loc=US
            bad-line
            empty=
            colo = SJC
            """);

        AssertEqual(values["ip"], "203.0.113.9");
        AssertEqual(values["loc"], "US");
        AssertEqual(values["colo"], "SJC");
        AssertFalse(values.ContainsKey("empty"), "Empty trace values should be ignored.");
        }, group: "network");

        yield return new TestCase("split routing normalizes hosts and parses only valid DoH addresses", () =>
    {
        var hosts = SplitRoutingDiagnosticsService.NormalizeHosts(
        [
            "https://chatgpt.com/path",
            "api.openai.com",
            "api.openai.com/",
            "  "
        ]);
        var addresses = SplitRoutingDiagnosticsService.ParseDohAddresses(
            """
            {
              "Answer": [
                { "type": 1, "data": "1.1.1.1" },
                { "type": 28, "data": "2606:4700:4700::1111" },
                { "type": 5, "data": "alias.example" },
                { "type": 1, "data": "not-an-ip" }
              ]
            }
            """);

        AssertTrue(hosts.SequenceEqual(["chatgpt.com", "api.openai.com"], StringComparer.OrdinalIgnoreCase), $"Unexpected hosts: {string.Join(",", hosts)}.");
        AssertTrue(addresses.SequenceEqual(["1.1.1.1", "2606:4700:4700::1111"], StringComparer.OrdinalIgnoreCase), $"Unexpected DoH addresses: {string.Join(",", addresses)}.");
        }, group: "network");

        yield return new TestCase("split routing summarizes DNS agreement and split state", () =>
    {
        var sameSummary = SplitRoutingDiagnosticsService.BuildDnsComparisonSummary(
            "chatgpt.com",
            ["1.1.1.1"],
            ["1.1.1.1"],
            ["1.1.1.1"]);
        var splitView = new SplitRoutingDnsView(
            "chatgpt.com",
            ["1.1.1.1"],
            ["1.0.0.1"],
            ["1.1.1.1"],
            null,
            null,
            null,
            string.Empty,
            null);

        AssertContains(sameSummary, "一致");
        AssertTrue(SplitRoutingDiagnosticsService.IndicatesDnsSplit(splitView), "Different DoH/system answers should indicate DNS split.");
        AssertContains(BasicNetworkDiagnosticsService.TranslatePingStatus(System.Net.NetworkInformation.IPStatus.TimedOut), "超时");
        }, group: "network");
    }

    private static byte[] BuildStunSuccessResponse(byte[] transactionId, params byte[][] attributes)
    {
        var payloadLength = attributes.Sum(static attribute => attribute.Length);
        var response = new byte[20 + payloadLength];
        response[0] = 0x01;
        response[1] = 0x01;
        response[2] = (byte)((payloadLength >> 8) & 0xFF);
        response[3] = (byte)(payloadLength & 0xFF);
        response[4] = 0x21;
        response[5] = 0x12;
        response[6] = 0xA4;
        response[7] = 0x42;
        Array.Copy(transactionId, 0, response, 8, transactionId.Length);

        var offset = 20;
        foreach (var attribute in attributes)
        {
            Array.Copy(attribute, 0, response, offset, attribute.Length);
            offset += attribute.Length;
        }

        return response;
    }

    private static byte[] BuildMappedAddressAttribute(ushort type, string address, int port)
    {
        var ipBytes = IPAddress.Parse(address).GetAddressBytes();
        return
        [
            (byte)((type >> 8) & 0xFF), (byte)(type & 0xFF),
            0x00, 0x08,
            0x00, 0x01,
            (byte)((port >> 8) & 0xFF), (byte)(port & 0xFF),
            ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3]
        ];
    }

    private static byte[] BuildXorMappedAddressAttribute(string address, int port)
    {
        var ipBytes = IPAddress.Parse(address).GetAddressBytes();
        var magicCookie = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
        for (var index = 0; index < ipBytes.Length; index++)
        {
            ipBytes[index] ^= magicCookie[index];
        }

        var xorPort = port ^ 0x2112;
        return
        [
            0x00, 0x20,
            0x00, 0x08,
            0x00, 0x01,
            (byte)((xorPort >> 8) & 0xFF), (byte)(xorPort & 0xFF),
            ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3]
        ];
    }
}

internal sealed class InMemoryClientApiDiagnosticEnvironment : IClientApiDiagnosticEnvironment
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _environmentVariables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _commandPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _processNames = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryClientApiDiagnosticEnvironment()
    {
        UserProfilePath = Path.Combine(Path.GetTempPath(), "RelayBenchDiagnosticsTests", Guid.NewGuid().ToString("N"));
        RoamingAppDataPath = Path.Combine(UserProfilePath, "AppData", "Roaming");
        LocalAppDataPath = Path.Combine(UserProfilePath, "AppData", "Local");
        AddDirectory(UserProfilePath);
        AddDirectory(RoamingAppDataPath);
        AddDirectory(LocalAppDataPath);
    }

    public string UserProfilePath { get; }

    public string RoamingAppDataPath { get; }

    public string LocalAppDataPath { get; }

    public string? GetEnvironmentVariable(string name)
        => _environmentVariables.TryGetValue(name, out var value) ? value : null;

    public bool FileExists(string path)
        => _files.ContainsKey(NormalizePath(path));

    public bool DirectoryExists(string path)
        => _directories.Contains(NormalizePath(path));

    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var normalized = NormalizePath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _directories
            .Where(directory => string.Equals(Path.GetDirectoryName(directory), normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static directory => directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string? ReadFileText(string path)
        => _files.TryGetValue(NormalizePath(path), out var content) ? content : null;

    public IReadOnlyList<string> ResolveCommandPaths(string commandName)
        => _commandPaths.TryGetValue(commandName, out var paths) ? paths : Array.Empty<string>();

    public IReadOnlyList<string> GetRunningProcessNames()
        => _processNames.ToArray();

    public void SetEnvironmentVariable(string name, string value)
        => _environmentVariables[name] = value;

    public void AddCommandPath(string commandName, string path)
    {
        if (!_commandPaths.TryGetValue(commandName, out var paths))
        {
            paths = [];
            _commandPaths[commandName] = paths;
        }

        paths.Add(path);
    }

    public void AddProcessName(string processName)
        => _processNames.Add(processName);

    public void WriteFile(string path, string content)
    {
        var normalized = NormalizePath(path);
        var directory = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            AddDirectory(directory);
        }

        _files[normalized] = content;
    }

    private void AddDirectory(string path)
    {
        var current = NormalizePath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            _directories.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent ?? string.Empty;
        }
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path);
}

internal sealed class FakeClientApiProbeTransport(
    Func<Uri, HttpMethod, string, Task<ClientApiProbeResponse>> handler) : IClientApiProbeTransport
{
    public Task<ClientApiProbeResponse> ProbeAsync(
        Uri url,
        HttpMethod method,
        string provider,
        CancellationToken cancellationToken)
        => handler(url, method, provider);
}
