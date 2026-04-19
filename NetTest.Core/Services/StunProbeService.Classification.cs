using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class StunProbeService
{
    private static string BuildMappingBehaviorHint(StunResponse primaryResponse, StunResponse? secondaryResponse)
    {
        if (secondaryResponse is null)
        {
            return primaryResponse.AlternateEndpoint is null
                ? "服务器未返回 OTHER-ADDRESS / CHANGED-ADDRESS，无法继续比较映射行为。"
                : "已拿到备用地址，但跟进测试未成功，映射行为仍需复核。";
        }

        if (string.Equals(primaryResponse.MappedAddress, secondaryResponse.MappedAddress, StringComparison.OrdinalIgnoreCase))
        {
            return "主地址和备用地址返回了相同的公网映射，映射行为看起来较稳定。";
        }

        return "主地址和备用地址返回了不同的公网映射，疑似存在对称映射行为。";
    }

    private static string ClassifyNatType(
        IPEndPoint? localEndpoint,
        StunResponse primaryResponse,
        BindingTestOutcome changeIpPortTest,
        BindingTestOutcome? alternateBasicTest,
        BindingTestOutcome? changePortOnlyTest)
    {
        var behindNat = !IsSameEndpoint(localEndpoint, primaryResponse.MappedEndPoint);

        if (!behindNat)
        {
            return changeIpPortTest.Success ? "开放互联网" : "对称 UDP 防火墙 / 过滤未知";
        }

        if (changeIpPortTest.Success)
        {
            return "完全锥形 NAT";
        }

        if (alternateBasicTest?.Success == true && alternateBasicTest.Response is not null)
        {
            if (!string.Equals(primaryResponse.MappedAddress, alternateBasicTest.Response.MappedAddress, StringComparison.OrdinalIgnoreCase))
            {
                return "对称 NAT";
            }

            if (changePortOnlyTest?.Success == true)
            {
                return "受限锥形 NAT";
            }

            return "端口受限锥形 NAT";
        }

        return primaryResponse.AlternateEndpoint is null
            ? "NAT 已检测到，但服务器能力不足，无法细分类型"
            : "NAT 已检测到，但辅助测试不足，类型待复核";
    }

    private static string BuildNatTypeSummary(
        string natType,
        BindingTestOutcome primaryTest,
        BindingTestOutcome changeIpPortTest,
        BindingTestOutcome? alternateBasicTest,
        BindingTestOutcome? changePortOnlyTest)
    {
        List<string> parts =
        [
            $"归类结果：{natType}。",
            $"测试 I：{(primaryTest.Success ? "成功" : "失败")}。",
            $"测试 II：{(changeIpPortTest.Success ? "成功" : "失败")}。"
        ];

        if (alternateBasicTest is not null)
        {
            parts.Add($"测试 I'：{(alternateBasicTest.Success ? "成功" : "失败")}。");
        }

        if (changePortOnlyTest is not null)
        {
            parts.Add($"测试 III：{(changePortOnlyTest.Success ? "成功" : "失败")}。");
        }

        parts.Add("说明：该结果基于经典 STUN 探测路径做最佳努力推测；若服务器不支持 CHANGE-REQUEST 或备用地址，细分类型可能偏保守。");
        return string.Join(" ", parts);
    }

    private static string BuildClassificationConfidence(
        StunResponse primaryResponse,
        BindingTestOutcome changeIpPortTest,
        BindingTestOutcome? alternateBasicTest,
        BindingTestOutcome? changePortOnlyTest)
    {
        if (primaryResponse.AlternateEndpoint is null)
        {
            return "低：当前 STUN 服务器未提供 OTHER-ADDRESS / CHANGED-ADDRESS，只能完成基础映射判断。";
        }

        if (changeIpPortTest.Success && alternateBasicTest?.Success == true && changePortOnlyTest?.Success == true)
        {
            return "高：基础 Binding、切换 IP/端口、备用地址复测与切换端口测试都已完成。";
        }

        if (alternateBasicTest?.Success == true)
        {
            return "中：已具备备用地址复测，但过滤行为仍可能偏保守，建议结合复测确认。";
        }

        if (changeIpPortTest.Success)
        {
            return "中：已确认 CHANGE-REQUEST 可响应，但备用地址链路不完整。";
        }

        return "低：辅助测试不足，当前 NAT 归类更适合作为保守参考值。";
    }

    private static string BuildCoverageSummary(
        StunResponse primaryResponse,
        BindingTestOutcome primaryTest,
        BindingTestOutcome changeIpPortTest,
        BindingTestOutcome? alternateBasicTest,
        BindingTestOutcome? changePortOnlyTest)
    {
        List<string> parts =
        [
            $"测试 I 基础 Binding：{(primaryTest.Success ? "完成" : "失败")}",
            $"测试 II 切换 IP/端口：{(changeIpPortTest.Success ? "完成" : "失败")}",
            $"服务器是否给出备用地址：{(primaryResponse.AlternateEndpoint is null ? "否" : $"是（{primaryResponse.AlternateEndpoint}）")}",
            $"测试 I' 备用地址 Binding：{DescribeOptionalCoverage(alternateBasicTest)}",
            $"测试 III 切换端口：{DescribeOptionalCoverage(changePortOnlyTest)}"
        ];

        var xorMapped = primaryResponse.Attributes.ContainsKey("XOR-MAPPED-ADDRESS") ? "有" : "无";
        var otherAddress = primaryResponse.Attributes.ContainsKey("OTHER-ADDRESS") ? "有" : "无";
        var responseOrigin = primaryResponse.Attributes.ContainsKey("RESPONSE-ORIGIN") ? "有" : "无";
        parts.Add($"关键属性覆盖：XOR-MAPPED={xorMapped}，OTHER-ADDRESS={otherAddress}，RESPONSE-ORIGIN={responseOrigin}");
        return string.Join("；", parts) + "。";
    }

    private static string BuildReviewRecommendation(
        StunResponse primaryResponse,
        BindingTestOutcome changeIpPortTest,
        BindingTestOutcome? alternateBasicTest,
        BindingTestOutcome? changePortOnlyTest,
        string natType)
    {
        if (primaryResponse.AlternateEndpoint is null)
        {
            return "当前服务器更像普通 STUN，只能确认公网映射，无法完整区分受限锥形、端口受限锥形与更细过滤行为；建议换一个支持 RFC 5780 的 STUN 服务器复测。";
        }

        if (!changeIpPortTest.Success && alternateBasicTest?.Success != true)
        {
            return "CHANGE-REQUEST 与备用地址测试都不完整，当前结果偏保守；建议在相同网络下复测一次，并确认本地路由器没有额外 UDP 过滤。";
        }

        if (changePortOnlyTest is not null && !changePortOnlyTest.Success &&
            natType.Contains("端口受限", StringComparison.OrdinalIgnoreCase))
        {
            return "当前结果更偏向端口受限锥形 NAT；如中转站仍出现偶发 UDP 失败，可换一个 STUN 服务器交叉验证过滤行为。";
        }

        if (natType.Contains("待复核", StringComparison.OrdinalIgnoreCase) ||
            natType.Contains("无法细分", StringComparison.OrdinalIgnoreCase))
        {
            return "当前 NAT 已经确认存在，但细分依据不足；建议至少再换一个 STUN 服务器复测，并结合真实业务流量或语音/UDP 业务做交叉验证。";
        }

        return "当前归类覆盖度较完整，可把它作为这条网络的 UDP 环境参考；若后续实际业务仍异常，再结合中转站实测与路由结果继续排查。";
    }

    private static string DescribeOptionalCoverage(BindingTestOutcome? outcome)
    {
        if (outcome is null)
        {
            return "未执行（缺少备用地址）";
        }

        return outcome.Success ? "完成" : $"失败（{outcome.Error ?? "未返回响应"}）";
    }

    private static bool IsSameEndpoint(IPEndPoint? localEndpoint, IPEndPoint? mappedEndPoint)
    {
        if (localEndpoint is null || mappedEndPoint is null)
        {
            return false;
        }

        return localEndpoint.Address.Equals(mappedEndPoint.Address) &&
               localEndpoint.Port == mappedEndPoint.Port;
    }
}
