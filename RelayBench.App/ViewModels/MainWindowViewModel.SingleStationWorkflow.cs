namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool IsSingleStationQuickMode
        => string.Equals(SelectedSingleStationModeKey, "quick", StringComparison.Ordinal);

    public bool IsSingleStationStabilityMode
        => string.Equals(SelectedSingleStationModeKey, "stability", StringComparison.Ordinal);

    public bool IsSingleStationDeepMode
        => string.Equals(SelectedSingleStationModeKey, "deep", StringComparison.Ordinal);

    public bool IsSingleStationConcurrencyMode
        => string.Equals(SelectedSingleStationModeKey, "concurrency", StringComparison.Ordinal);

    public string SingleStationModeDescription
        => SelectedSingleStationModeKey switch
        {
            "stability" => "多轮看成功率和波动",
            "deep" => "测兼容、缓存、多模态、对照",
            "concurrency" => "测并发上限、限流点、P95、tok/s",
            _ => "测可用性、延迟、TTFT、tok/s"
        };

    public string SingleStationPrimaryButtonText
        => SelectedSingleStationModeKey switch
        {
            "stability" => "\u5F00\u59CB\u7A33\u5B9A\u6027\u6D4B\u8BD5",
            "deep" => "\u5F00\u59CB\u6DF1\u5EA6\u6D4B\u8BD5",
            "concurrency" => "\u5F00\u59CB\u5E76\u53D1\u538B\u6D4B",
            _ => "\u5F00\u59CB\u5FEB\u901F\u6D4B\u8BD5"
        };

    public string SingleStationResultSummary
        => SelectedSingleStationModeKey switch
        {
            "stability" => ProxyStabilitySummary,
            "concurrency" => ProxyConcurrencySummary,
            _ => ProxySummary
        };

    public string SingleStationResultDetail
        => SelectedSingleStationModeKey switch
        {
            "stability" => ProxyStabilityDetail,
            "concurrency" => ProxyConcurrencyDetail,
            _ => ProxyDetail
        };

    private Task RunSelectedSingleStationModeAsync()
        => SelectedSingleStationModeKey switch
        {
            "stability" => RunProxySeriesWithValidationAsync(),
            "deep" => RunProxyDeepWithValidationAsync(),
            "concurrency" => RunProxyConcurrencyWithValidationAsync(),
            _ => RunProxyWithValidationAsync()
        };

    private void NotifySingleStationModeStateChanged()
    {
        OnPropertyChanged(nameof(IsSingleStationQuickMode));
        OnPropertyChanged(nameof(IsSingleStationStabilityMode));
        OnPropertyChanged(nameof(IsSingleStationDeepMode));
        OnPropertyChanged(nameof(IsSingleStationConcurrencyMode));
        OnPropertyChanged(nameof(SingleStationModeDescription));
        OnPropertyChanged(nameof(SingleStationPrimaryButtonText));
        OnPropertyChanged(nameof(SingleStationResultSummary));
        OnPropertyChanged(nameof(SingleStationResultDetail));
    }
}
