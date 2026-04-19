using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class StunProbeService
{
    private const string DefaultServerHost = "stun.cloudflare.com";
    private const string SecondaryFallbackServerHost = "stun.l.google.com";
    private const string LegacyDefaultServerHost = "stun.miwifi.com";

    public async Task<StunProbeResult> ProbeAsync(string serverHost, int serverPort = 3478, CancellationToken cancellationToken = default)
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
                    return await ProbeEndpointAsync(candidateHost, endpoint, resolvedAddresses, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
        }

        return BuildFailure(
            requestedHost,
            serverPort,
            resolvedAddressTexts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            lastError ?? "STUN Binding 请求没有收到响应。");
    }

    private static async Task<StunProbeResult> ProbeEndpointAsync(
        string serverHost,
        IPEndPoint primaryEndpoint,
        IReadOnlyList<IPAddress> resolvedAddresses,
        CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        List<StunNatBindingTestResult> tests = [];

        var primaryTest = await RunBindingTestAsync(client, primaryEndpoint, ChangeRequestMode.None, "测试 I：基础 Binding", cancellationToken);
        tests.Add(primaryTest.ToPublicModel());

        if (!primaryTest.Success || primaryTest.Response is null)
        {
            return BuildFailure(
                serverHost,
                primaryEndpoint.Port,
                resolvedAddresses.Select(address => address.ToString()).ToArray(),
                primaryTest.Error ?? "STUN 基础 Binding 失败。",
                tests);
        }

        var response = primaryTest.Response;
        var alternateEndpoint = response.AlternateEndpoint;

        var changeIpPortTest = await RunBindingTestAsync(
            client,
            primaryEndpoint,
            ChangeRequestMode.ChangeIpAndPort,
            "测试 II：请求服务器切换 IP 与端口",
            cancellationToken);
        tests.Add(changeIpPortTest.ToPublicModel());

        BindingTestOutcome? alternateBasicTest = null;
        if (alternateEndpoint is not null)
        {
            alternateBasicTest = await RunBindingTestAsync(
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
            changePortOnlyTest = await RunBindingTestAsync(
                client,
                primaryEndpoint,
                ChangeRequestMode.ChangePortOnly,
                "测试 III：请求服务器仅切换端口",
                cancellationToken);
            tests.Add(changePortOnlyTest.ToPublicModel());
        }

        var mappingBehaviorHint = BuildMappingBehaviorHint(response, alternateBasicTest?.Response);
        var natType = ClassifyNatType(
            primaryTest.LocalEndpoint,
            response,
            changeIpPortTest,
            alternateBasicTest,
            changePortOnlyTest);
        var natTypeSummary = BuildNatTypeSummary(natType, primaryTest, changeIpPortTest, alternateBasicTest, changePortOnlyTest);
        var classificationConfidence = BuildClassificationConfidence(response, changeIpPortTest, alternateBasicTest, changePortOnlyTest);
        var coverageSummary = BuildCoverageSummary(response, primaryTest, changeIpPortTest, alternateBasicTest, changePortOnlyTest);
        var reviewRecommendation = BuildReviewRecommendation(response, changeIpPortTest, alternateBasicTest, changePortOnlyTest, natType);

        return new StunProbeResult(
            DateTimeOffset.Now,
            serverHost,
            primaryEndpoint.Port,
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

    private static StunProbeResult BuildFailure(
        string serverHost,
        int serverPort,
        IReadOnlyList<string> resolvedAddresses,
        string error,
        IReadOnlyList<StunNatBindingTestResult>? tests = null)
        => new(
            DateTimeOffset.Now,
            serverHost,
            serverPort,
            resolvedAddresses,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "UDP 受限或 STUN 不可达",
            "没有完成基础 Binding，无法继续进行 NAT 类型归类。",
            "低",
            "基础 Binding 未完成，因此没有进入 CHANGE-REQUEST 与备用地址测试阶段。",
            "先检查本机防火墙、路由器和运营商网络是否允许 UDP 3478；如仍失败，建议更换支持 RFC 5780 的 STUN 服务器复测。",
            null,
            new Dictionary<string, string>(),
            tests ?? Array.Empty<StunNatBindingTestResult>(),
            error);

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
    {
        if (string.Equals(requestedHost, LegacyDefaultServerHost, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                DefaultServerHost,
                SecondaryFallbackServerHost,
                LegacyDefaultServerHost
            ];
        }

        return [requestedHost];
    }
}
