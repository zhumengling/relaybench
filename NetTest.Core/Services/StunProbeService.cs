using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class StunProbeService
{
    private const string DefaultServerHost = "stun.cloudflare.com";

    public async Task<StunProbeResult> ProbeAsync(
        string serverHost,
        StunTransportProtocol transportProtocol = StunTransportProtocol.Udp,
        int serverPort = 3478,
        CancellationToken cancellationToken = default)
    {
        var requestedHost = string.IsNullOrWhiteSpace(serverHost)
            ? DefaultServerHost
            : serverHost.Trim();
        string? lastError = null;
        List<string> resolvedAddressTexts = [];

        foreach (var candidateHost in BuildCandidateHosts(requestedHost))
        {
            var resolvedAddresses = await ResolveAddressesAsync(candidateHost);
            if (resolvedAddresses.Count == 0)
            {
                lastError = "无法解析到可用的 IPv4 STUN 地址。";
                continue;
            }

            resolvedAddressTexts.AddRange(resolvedAddresses.Select(address => address.ToString()));

            foreach (var address in resolvedAddresses)
            {
                var endpoint = new IPEndPoint(address, serverPort);

                try
                {
                    return transportProtocol == StunTransportProtocol.Tcp
                        ? await ProbeTcpEndpointAsync(candidateHost, endpoint, resolvedAddresses, cancellationToken)
                        : await ProbeUdpEndpointAsync(candidateHost, endpoint, resolvedAddresses, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
        }

        return BuildFailure(
            requestedHost,
            transportProtocol,
            serverPort,
            resolvedAddressTexts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            lastError ?? "STUN Binding 请求没有收到响应。");
    }

    private static async Task<StunProbeResult> ProbeUdpEndpointAsync(
        string serverHost,
        IPEndPoint primaryEndpoint,
        IReadOnlyList<IPAddress> resolvedAddresses,
        CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        List<StunNatBindingTestResult> tests = [];

        var primaryTest = await RunUdpBindingTestAsync(client, primaryEndpoint, ChangeRequestMode.None, "测试 I：基础 Binding", cancellationToken);
        tests.Add(primaryTest.ToPublicModel());

        if (!primaryTest.Success || primaryTest.Response is null)
        {
            return BuildFailure(
                serverHost,
                StunTransportProtocol.Udp,
                primaryEndpoint.Port,
                resolvedAddresses.Select(address => address.ToString()).ToArray(),
                primaryTest.Error ?? "STUN 基础 Binding 失败。",
                tests);
        }

        var response = primaryTest.Response;
        var alternateEndpoint = response.AlternateEndpoint;

        var changeIpPortTest = await RunUdpBindingTestAsync(
            client,
            primaryEndpoint,
            ChangeRequestMode.ChangeIpAndPort,
            "测试 II：请求服务器切换 IP 与端口",
            cancellationToken);
        tests.Add(changeIpPortTest.ToPublicModel());

        BindingTestOutcome? alternateBasicTest = null;
        if (alternateEndpoint is not null)
        {
            alternateBasicTest = await RunUdpBindingTestAsync(
                client,
                alternateEndpoint,
                ChangeRequestMode.None,
                "测试 I'：对备用地址做基础 Binding",
                cancellationToken);
            tests.Add(alternateBasicTest.ToPublicModel());
        }

        BindingTestOutcome? changePortOnlyTest = null;
        if (alternateEndpoint is not null)
        {
            changePortOnlyTest = await RunUdpBindingTestAsync(
                client,
                primaryEndpoint,
                ChangeRequestMode.ChangePortOnly,
                "测试 III：请求服务器仅切换端口",
                cancellationToken);
            tests.Add(changePortOnlyTest.ToPublicModel());
        }

        var mappingBehaviorHint = BuildMappingBehaviorHint(response, alternateBasicTest?.Response);
        var changeRequestConfirmed = WasChangeRequestConfirmed(primaryEndpoint, response, changeIpPortTest);
        var natType = ClassifyNatType(
            primaryTest.LocalEndpoint,
            response,
            changeIpPortTest,
            changeRequestConfirmed,
            alternateBasicTest,
            changePortOnlyTest);
        var natTypeSummary = BuildNatTypeSummary(natType, primaryTest, changeIpPortTest, changeRequestConfirmed, alternateBasicTest, changePortOnlyTest);
        var classificationConfidence = BuildClassificationConfidence(response, changeIpPortTest, changeRequestConfirmed, alternateBasicTest, changePortOnlyTest);
        var coverageSummary = BuildCoverageSummary(response, primaryTest, changeIpPortTest, changeRequestConfirmed, alternateBasicTest, changePortOnlyTest);
        var reviewRecommendation = BuildReviewRecommendation(response, changeIpPortTest, changeRequestConfirmed, alternateBasicTest, changePortOnlyTest, natType);

        return new StunProbeResult(
            DateTimeOffset.Now,
            serverHost,
            primaryEndpoint.Port,
            StunTransportProtocol.Udp,
            resolvedAddresses.Select(address => address.ToString()).ToArray(),
            true,
            primaryTest.LocalEndpoint?.ToString(),
            primaryEndpoint.ToString(),
            response.ResponseOrigin,
            response.MappedAddress,
            response.OtherAddress,
            response.ChangedAddress,
            mappingBehaviorHint,
            natType,
            natTypeSummary,
            classificationConfidence,
            coverageSummary,
            reviewRecommendation,
            primaryTest.RoundTrip,
            response.Attributes,
            tests,
            null);
    }

    private static async Task<StunProbeResult> ProbeTcpEndpointAsync(
        string serverHost,
        IPEndPoint primaryEndpoint,
        IReadOnlyList<IPAddress> resolvedAddresses,
        CancellationToken cancellationToken)
    {
        List<StunNatBindingTestResult> tests = [];
        var primaryTest = await RunTcpBindingTestAsync(primaryEndpoint, ChangeRequestMode.None, "测试 I：TCP 基础 Binding", cancellationToken);
        tests.Add(primaryTest.ToPublicModel());

        if (!primaryTest.Success || primaryTest.Response is null)
        {
            return BuildFailure(
                serverHost,
                StunTransportProtocol.Tcp,
                primaryEndpoint.Port,
                resolvedAddresses.Select(address => address.ToString()).ToArray(),
                primaryTest.Error ?? "TCP STUN 基础 Binding 失败。",
                tests);
        }

        var response = primaryTest.Response;
        var behindNat = !IsSameEndpoint(primaryTest.LocalEndpoint, response.MappedEndPoint);
        var natType = behindNat
            ? "检测到 NAT（TCP 基础映射）"
            : "开放互联网（TCP 基础映射）";

        return new StunProbeResult(
            DateTimeOffset.Now,
            serverHost,
            primaryEndpoint.Port,
            StunTransportProtocol.Tcp,
            resolvedAddresses.Select(address => address.ToString()).ToArray(),
            true,
            primaryTest.LocalEndpoint?.ToString(),
            primaryEndpoint.ToString(),
            response.ResponseOrigin,
            response.MappedAddress,
            response.OtherAddress,
            response.ChangedAddress,
            "当前为 TCP STUN，仅完成基础 Binding，可用于查看反射地址。",
            natType,
            "TCP STUN 已完成基础 Binding；由于没有执行 UDP 的 CHANGE-REQUEST 与备用地址测试，NAT 细分类仅供参考。",
            "低：当前为 TCP STUN，仅提供基础映射参考。",
            "测试 I TCP 基础 Binding：完成；TCP 模式下未执行 CHANGE-REQUEST、备用地址与切换端口测试。",
            "如果你需要判断打洞能力、受限锥形或端口受限锥形，请改用 UDP STUN 服务复测。",
            primaryTest.RoundTrip,
            response.Attributes,
            tests,
            null);
    }

    private static StunProbeResult BuildFailure(
        string serverHost,
        StunTransportProtocol transportProtocol,
        int serverPort,
        IReadOnlyList<string> resolvedAddresses,
        string error,
        IReadOnlyList<StunNatBindingTestResult>? tests = null)
    {
        var natType = transportProtocol == StunTransportProtocol.Tcp
            ? "TCP STUN 不可达"
            : "UDP 受限或 STUN 不可达";
        var natTypeSummary = transportProtocol == StunTransportProtocol.Tcp
            ? "没有完成 TCP 基础 Binding，无法继续给出映射结果。"
            : "没有完成基础 Binding，无法继续进行 NAT 类型归类。";
        var coverageSummary = transportProtocol == StunTransportProtocol.Tcp
            ? "TCP 基础 Binding 未完成，因此没有拿到可用的反射地址。"
            : "基础 Binding 未完成，因此没有进入 CHANGE-REQUEST 与备用地址测试阶段。";
        var reviewRecommendation = transportProtocol == StunTransportProtocol.Tcp
            ? "先检查本机到目标 STUN 服务器的 TCP 3478 连通性；如仍失败，建议换一个 TCP STUN 服务器，或改用 UDP STUN 复测。"
            : "先检查本机防火墙、路由器和运营商网络是否允许 UDP 3478；如仍失败，建议更换支持 RFC 5780 的 STUN 服务器复测。";

        return new StunProbeResult(
            DateTimeOffset.Now,
            serverHost,
            serverPort,
            transportProtocol,
            resolvedAddresses,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            natType,
            natTypeSummary,
            "低",
            coverageSummary,
            reviewRecommendation,
            null,
            new Dictionary<string, string>(),
            tests ?? Array.Empty<StunNatBindingTestResult>(),
            error);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAddressesAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            return addresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .ToArray();
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static IReadOnlyList<string> BuildCandidateHosts(string requestedHost)
        => [requestedHost];
}
