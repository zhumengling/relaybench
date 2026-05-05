using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed class TransparentProxyModelPoolViewModel : ObservableObject
{
    public TransparentProxyModelPoolViewModel(TransparentProxyModelPoolSnapshot snapshot)
    {
        ModelName = snapshot.ModelName;
        IsPassThrough = snapshot.IsPassThrough;
        RouteCount = snapshot.RouteCount;
        MemberCount = snapshot.MemberCount;
        HealthyMembers = snapshot.HealthyMembers;
        OpenCircuitMembers = snapshot.OpenCircuitMembers;
        Sent = snapshot.Sent;
        Success = snapshot.Success;
        Failed = snapshot.Failed;
        BestLatencyMs = snapshot.BestLatencyMs;
        ProtocolSummary = snapshot.ProtocolSummary;
        Members = snapshot.Members
            .Select(static member => new TransparentProxyModelPoolMemberViewModel(member))
            .ToArray();
    }

    public string ModelName { get; }

    public bool IsPassThrough { get; }

    public int RouteCount { get; }

    public int MemberCount { get; }

    public int HealthyMembers { get; }

    public int OpenCircuitMembers { get; }

    public int Sent { get; }

    public int Success { get; }

    public int Failed { get; }

    public long BestLatencyMs { get; }

    public string ProtocolSummary { get; }

    public IReadOnlyList<TransparentProxyModelPoolMemberViewModel> Members { get; }

    public string DisplayModelName
        => IsPassThrough ? "按请求透传" : ModelName;

    public string MemberCountText
        => IsPassThrough
            ? $"{RouteCount} 路兜底"
            : $"{RouteCount} 路 / {MemberCount} 映射";

    public string HealthText
    {
        get
        {
            if (MemberCount == 0)
            {
                return "空";
            }

            if (HealthyMembers <= 0)
            {
                return "不可用";
            }

            if (OpenCircuitMembers > 0 || HealthyMembers < MemberCount)
            {
                return "降级";
            }

            return "可用";
        }
    }

    public string HealthBrush
        => HealthText switch
        {
            "可用" => "#24A148",
            "降级" => "#F1C21B",
            "不可用" => "#DA1E28",
            _ => "#64748B"
        };

    public string MetricsText
    {
        get
        {
            var latency = BestLatencyMs <= 0 ? "-" : $"{BestLatencyMs} ms";
            return $"请求 {Sent} / 成功 {Success} / 失败 {Failed} / 最快 {latency}";
        }
    }

    public string MembersPreviewText
        => string.Join("；", Members
            .Take(8)
            .Select(static member => $"{member.RouteName}: {member.UpstreamModel}"));

    public string ToolTipText
        => string.Join(Environment.NewLine, Members
            .Take(24)
            .Select(static member => member.ToolTipLine));
}
