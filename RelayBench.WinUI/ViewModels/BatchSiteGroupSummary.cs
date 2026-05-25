namespace RelayBench.WinUI.ViewModels;

using Microsoft.UI.Xaml;

public sealed record BatchSiteGroupSummary(
    string Name,
    string Detail,
    string CountText,
    string EnabledText,
    string MissingText,
    string FirstEndpoint,
    string ModelText,
    string ProtocolText = "未探测",
    bool IsSelected = false)
{
    public bool IsPlaceholder => string.Equals(Name, "暂无入口组", StringComparison.Ordinal);

    public string SelectionStateText => IsPlaceholder
        ? "待录入"
        : IsSelected
            ? "编辑中"
            : "点击编辑";

    public string Tooltip => IsPlaceholder
        ? "请先新增一行或批量粘贴入口。"
        : IsSelected
            ? $"正在编辑入口组“{Name}”。"
            : $"点击载入入口组“{Name}”继续编辑。";

    public string ProtocolTooltip => IsPlaceholder
        ? "尚未录入入口，暂无协议探测结果。"
        : string.IsNullOrWhiteSpace(ProtocolText) || string.Equals(ProtocolText, "未探测", StringComparison.Ordinal)
            ? "拉取模型后会后台真实探测 Responses / Anthropic / Chat 协议。"
            : $"协议支持：{ProtocolText}";

    public double SelectionMarkerOpacity => IsSelected ? 1 : 0;

    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlaceholderVisibility => !IsSelected && IsPlaceholder ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NormalVisibility => !IsSelected && !IsPlaceholder ? Visibility.Visible : Visibility.Collapsed;
}
