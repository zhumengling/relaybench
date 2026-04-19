namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool IsSingleStationQuickMode
        => string.Equals(SelectedSingleStationModeKey, "quick", StringComparison.Ordinal);

    public bool IsSingleStationStabilityMode
        => string.Equals(SelectedSingleStationModeKey, "stability", StringComparison.Ordinal);

    public bool IsSingleStationDeepMode
        => string.Equals(SelectedSingleStationModeKey, "deep", StringComparison.Ordinal);

    public string SingleStationModeDescription
        => SelectedSingleStationModeKey switch
        {
            "stability" => "多轮观察成功率、波动与连续失败，适合判断当前站点稳不稳。",
            "deep" => "验证协议兼容、流式完整性、多模态、缓存与官方对照等高级能力。",
            _ => "默认模式。快速判断当前站点是否可用、首字响应是否稳定。"
        };

    public string SingleStationPrimaryButtonText
        => SelectedSingleStationModeKey switch
        {
            "stability" => "开始稳定性测试",
            "deep" => "开始深度测试",
            _ => "开始快速测试"
        };

    public string SingleStationResultSummary
        => IsSingleStationStabilityMode ? ProxyStabilitySummary : ProxySummary;

    public string SingleStationResultDetail
        => IsSingleStationStabilityMode ? ProxyStabilityDetail : ProxyDetail;

    private Task RunSelectedSingleStationModeAsync()
        => SelectedSingleStationModeKey switch
        {
            "stability" => RunProxySeriesWithValidationAsync(),
            "deep" => RunProxyDeepWithValidationAsync(),
            _ => RunProxyWithValidationAsync()
        };

    private void NotifySingleStationModeStateChanged()
    {
        OnPropertyChanged(nameof(IsSingleStationQuickMode));
        OnPropertyChanged(nameof(IsSingleStationStabilityMode));
        OnPropertyChanged(nameof(IsSingleStationDeepMode));
        OnPropertyChanged(nameof(SingleStationModeDescription));
        OnPropertyChanged(nameof(SingleStationPrimaryButtonText));
        OnPropertyChanged(nameof(SingleStationResultSummary));
        OnPropertyChanged(nameof(SingleStationResultDetail));
    }
}
