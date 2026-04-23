namespace RelayBench.Core.Services;

public static class StunNatClassificationHelper
{
    public static string ClassifyUdpNatType(
        bool behindNat,
        bool changeRequestSucceeded,
        bool changeRequestConfirmed,
        bool hasAlternateEndpoint,
        bool alternateBasicSucceeded,
        bool mappingChangedOnAlternate,
        bool changePortOnlySucceeded)
    {
        if (!behindNat)
        {
            return changeRequestConfirmed
                ? "开放互联网"
                : "对称 UDP 防火墙 / 过滤未知";
        }

        if (changeRequestSucceeded && changeRequestConfirmed)
        {
            return "完全锥形 NAT";
        }

        if (alternateBasicSucceeded)
        {
            if (mappingChangedOnAlternate)
            {
                return "对称 NAT";
            }

            return changePortOnlySucceeded
                ? "受限锥形 NAT"
                : "端口受限锥形 NAT";
        }

        if (!hasAlternateEndpoint)
        {
            return "NAT 已检测到，但服务器能力不足，无法细分类型";
        }

        if (changeRequestSucceeded && !changeRequestConfirmed)
        {
            return "NAT 已检测到，但 CHANGE-REQUEST 未被服务器确认，类型待复核";
        }

        return "NAT 已检测到，但辅助测试不足，类型待复核";
    }
}
