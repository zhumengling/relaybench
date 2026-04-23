using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string NetworkIssueRelayUnavailable = "interface-unavailable";
    private const string NetworkIssueHighTtft = "high-ttft";
    private const string NetworkIssueHighJitter = "high-jitter";
    private const string NetworkIssueGeoUnlock = "geo-unlock";
    private const string NetworkIssueDnsRouting = "dns-routing";

    private const string NetworkReviewToolNetwork = "basic-network";
    private const string NetworkReviewToolOfficialApi = "official-api";
    private const string NetworkReviewToolSpeed = "speed-test";
    private const string NetworkReviewToolRoute = "route-mtr";
    private const string NetworkReviewToolSplitRouting = "split-routing";
    private const string NetworkReviewToolIpRisk = "ip-risk";
    private const string NetworkReviewToolStun = "nat-stun";
    private const string NetworkReviewToolPortScan = "port-scan";

    private string _selectedNetworkReviewIssueKey = NetworkIssueRelayUnavailable;
    private string _selectedNetworkReviewToolKey = NetworkReviewToolNetwork;

    public string SelectedNetworkReviewIssueKey
    {
        get => _selectedNetworkReviewIssueKey;
        set
        {
            var normalized = NormalizeNetworkReviewIssueKey(value);
            if (SetProperty(ref _selectedNetworkReviewIssueKey, normalized))
            {
                SelectedNetworkReviewToolKey = GetPreferredNetworkReviewToolKey(normalized);
                OnPropertyChanged(nameof(CurrentPageSubtitle));
                OnPropertyChanged(nameof(NetworkReviewRecommendationSummary));
                OnPropertyChanged(nameof(NetworkReviewNextActionSummary));
                OnPropertyChanged(nameof(ShowBasicReview));
                OnPropertyChanged(nameof(ShowPerformanceReview));
                OnPropertyChanged(nameof(ShowExitReview));
                OnPropertyChanged(nameof(ShowAdvancedReview));
            }
        }
    }

    public string SelectedNetworkReviewToolKey
    {
        get => _selectedNetworkReviewToolKey;
        set
        {
            var normalized = NormalizeNetworkReviewToolKey(value);
            if (SetProperty(ref _selectedNetworkReviewToolKey, normalized))
            {
                NotifyNetworkReviewToolStateChanged();
                OnPropertyChanged(nameof(CurrentPageSubtitle));
            }
        }
    }

    public string SelectedNetworkReviewToolDisplayName
        => SelectedNetworkReviewToolKey switch
        {
            NetworkReviewToolOfficialApi => "网页 API",
            NetworkReviewToolSpeed => "测速",
            NetworkReviewToolRoute => "路由 / MTR",
            NetworkReviewToolSplitRouting => "IP 与分流",
            NetworkReviewToolIpRisk => "IP 风险",
            NetworkReviewToolStun => "NAT / STUN",
            NetworkReviewToolPortScan => "端口扫描",
            _ => "基础网络"
        };

    public string SelectedNetworkReviewToolGroupName
        => SelectedNetworkReviewToolKey switch
        {
            NetworkReviewToolOfficialApi or NetworkReviewToolNetwork => "基础复核",
            NetworkReviewToolSpeed or NetworkReviewToolRoute => "性能复核",
            NetworkReviewToolSplitRouting or NetworkReviewToolIpRisk => "出口复核",
            _ => "高级复核"
        };

    public string SelectedNetworkReviewToolDescription
        => SelectedNetworkReviewToolKey switch
        {
            NetworkReviewToolOfficialApi => "看网页入口和官方 API 是否可达",
            NetworkReviewToolSpeed => "看延迟、抖动、带宽、丢包",
            NetworkReviewToolRoute => "看逐跳路径和异常跳点",
            NetworkReviewToolSplitRouting => "看出口、DNS 和分流命中",
            NetworkReviewToolIpRisk => "看出口 IP、ASN 和风险标记",
            NetworkReviewToolStun => "看 NAT 类型和打洞条件",
            NetworkReviewToolPortScan => "看端口可达和服务指纹",
            _ => "看本机网络、DNS 和公网出口"
        };

    public bool IsNetworkReviewBasicNetworkSelected
        => string.Equals(SelectedNetworkReviewToolKey, NetworkReviewToolNetwork, StringComparison.Ordinal);

    public bool IsNetworkReviewOfficialApiSelected
        => string.Equals(SelectedNetworkReviewToolKey, NetworkReviewToolOfficialApi, StringComparison.Ordinal);

    public bool IsNetworkReviewSpeedTestSelected
        => string.Equals(SelectedNetworkReviewToolKey, NetworkReviewToolSpeed, StringComparison.Ordinal);

    public bool IsNetworkReviewRouteSelected
        => string.Equals(SelectedNetworkReviewToolKey, NetworkReviewToolRoute, StringComparison.Ordinal);

    public bool IsNetworkReviewSplitRoutingSelected
        => string.Equals(SelectedNetworkReviewToolKey, NetworkReviewToolSplitRouting, StringComparison.Ordinal);

    public bool IsNetworkReviewIpRiskSelected
        => string.Equals(SelectedNetworkReviewToolKey, NetworkReviewToolIpRisk, StringComparison.Ordinal);

    public bool IsNetworkReviewStunSelected
        => string.Equals(SelectedNetworkReviewToolKey, NetworkReviewToolStun, StringComparison.Ordinal);

    public bool IsNetworkReviewPortScanSelected
        => string.Equals(SelectedNetworkReviewToolKey, NetworkReviewToolPortScan, StringComparison.Ordinal);

    public string NetworkReviewRecommendationSummary
        => SelectedNetworkReviewIssueKey switch
        {
            NetworkIssueHighTtft => "优先做性能复核：先测速，再看路由 / MTR。",
            NetworkIssueHighJitter => "优先做性能复核：关注波动、抖动与链路不稳定。",
            NetworkIssueGeoUnlock => "优先做出口复核：看出口 IP、DNS 与分流路径。",
            NetworkIssueDnsRouting => "优先做出口复核，其次做基础复核确认本机网络状态。",
            _ => "优先做基础复核：先确认本机网络与官方链路，再判断是不是接口本身不可用。"
        };

    public string NetworkReviewNextActionSummary
        => SelectedNetworkReviewIssueKey switch
        {
            NetworkIssueHighTtft => "如果测速和路由都正常，但 TTFT 仍然偏高，更像是接口上游排队、模型拥塞或转发策略问题。",
            NetworkIssueHighJitter => "如果本地测速抖动、路由抖动都明显，优先按链路问题处理；如果本地稳定，则继续怀疑接口波动。",
            NetworkIssueGeoUnlock => "如果出口 IP、DNS 和分流结果不符合预期，更像是本地出口或分流策略问题，而不是接口能力问题。",
            NetworkIssueDnsRouting => "如果 DNS 解析结果与实际出口不一致，优先排查本地分流、DNS 劫持或策略路由；确认后再回看接口表现。",
            _ => "如果基础网络和官方链路都正常，但接口仍失败，那么更像是接口自身不可用或授权异常。"
        };

    public bool ShowBasicReview
        => SelectedNetworkReviewIssueKey is NetworkIssueRelayUnavailable or NetworkIssueDnsRouting;

    public bool ShowPerformanceReview
        => SelectedNetworkReviewIssueKey is NetworkIssueHighTtft or NetworkIssueHighJitter;

    public bool ShowExitReview
        => SelectedNetworkReviewIssueKey is NetworkIssueGeoUnlock or NetworkIssueDnsRouting;

    public bool ShowAdvancedReview
        => SelectedNetworkReviewIssueKey == NetworkIssueRelayUnavailable;

    private void LoadNetworkReviewState(AppStateSnapshot snapshot)
    {
        _selectedNetworkReviewIssueKey = NormalizeNetworkReviewIssueKey(snapshot.NetworkReviewIssueKey);
        _selectedNetworkReviewToolKey = GetPreferredNetworkReviewToolKey(_selectedNetworkReviewIssueKey);
        OnPropertyChanged(nameof(SelectedNetworkReviewIssueKey));
        OnPropertyChanged(nameof(SelectedNetworkReviewToolKey));
        OnPropertyChanged(nameof(CurrentPageSubtitle));
        OnPropertyChanged(nameof(NetworkReviewRecommendationSummary));
        OnPropertyChanged(nameof(NetworkReviewNextActionSummary));
        OnPropertyChanged(nameof(ShowBasicReview));
        OnPropertyChanged(nameof(ShowPerformanceReview));
        OnPropertyChanged(nameof(ShowExitReview));
        OnPropertyChanged(nameof(ShowAdvancedReview));
        NotifyNetworkReviewToolStateChanged();
    }

    private void ApplyNetworkReviewStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.NetworkReviewIssueKey = NormalizeNetworkReviewIssueKey(SelectedNetworkReviewIssueKey);
    }

    private static string NormalizeNetworkReviewIssueKey(string? value)
        => value switch
        {
            NetworkIssueHighTtft => NetworkIssueHighTtft,
            NetworkIssueHighJitter => NetworkIssueHighJitter,
            NetworkIssueGeoUnlock => NetworkIssueGeoUnlock,
            NetworkIssueDnsRouting => NetworkIssueDnsRouting,
            _ => NetworkIssueRelayUnavailable
        };

    private static string NormalizeNetworkReviewToolKey(string? value)
        => value switch
        {
            NetworkReviewToolOfficialApi => NetworkReviewToolOfficialApi,
            NetworkReviewToolSpeed => NetworkReviewToolSpeed,
            NetworkReviewToolRoute => NetworkReviewToolRoute,
            NetworkReviewToolSplitRouting => NetworkReviewToolSplitRouting,
            NetworkReviewToolIpRisk => NetworkReviewToolIpRisk,
            NetworkReviewToolStun => NetworkReviewToolStun,
            NetworkReviewToolPortScan => NetworkReviewToolPortScan,
            _ => NetworkReviewToolNetwork
        };

    private static string GetPreferredNetworkReviewToolKey(string issueKey)
        => issueKey switch
        {
            NetworkIssueHighTtft or NetworkIssueHighJitter => NetworkReviewToolSpeed,
            NetworkIssueGeoUnlock or NetworkIssueDnsRouting => NetworkReviewToolSplitRouting,
            _ => NetworkReviewToolNetwork
        };

    private void NotifyNetworkReviewToolStateChanged()
    {
        OnPropertyChanged(nameof(SelectedNetworkReviewToolDisplayName));
        OnPropertyChanged(nameof(SelectedNetworkReviewToolGroupName));
        OnPropertyChanged(nameof(SelectedNetworkReviewToolDescription));
        OnPropertyChanged(nameof(IsNetworkReviewBasicNetworkSelected));
        OnPropertyChanged(nameof(IsNetworkReviewOfficialApiSelected));
        OnPropertyChanged(nameof(IsNetworkReviewSpeedTestSelected));
        OnPropertyChanged(nameof(IsNetworkReviewRouteSelected));
        OnPropertyChanged(nameof(IsNetworkReviewSplitRoutingSelected));
        OnPropertyChanged(nameof(IsNetworkReviewIpRiskSelected));
        OnPropertyChanged(nameof(IsNetworkReviewStunSelected));
        OnPropertyChanged(nameof(IsNetworkReviewPortScanSelected));
    }

    private string BuildNetworkReviewSubtitle()
        => $"{SelectedNetworkReviewToolGroupName} · {SelectedNetworkReviewToolDisplayName} · {SelectedNetworkReviewToolDescription}";

    private static string GetNetworkReviewIssueDisplayName(string key)
        => key switch
        {
            NetworkIssueHighTtft => "TTFT 很高",
            NetworkIssueHighJitter => "波动很大",
            NetworkIssueGeoUnlock => "地区 / 解锁异常",
            NetworkIssueDnsRouting => "DNS / 分流怀疑",
            _ => "接口不可用"
        };
}
