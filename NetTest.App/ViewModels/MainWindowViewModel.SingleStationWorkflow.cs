namespace NetTest.App.ViewModels;

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
            "stability" => "\u591A\u8F6E\u89C2\u5BDF\u6210\u529F\u7387\u3001\u6CE2\u52A8\u4E0E\u8FDE\u7EED\u5931\u8D25\uFF0C\u9002\u5408\u5224\u65AD\u5F53\u524D\u63A5\u53E3\u7A33\u4E0D\u7A33\u3002",
            "deep" => "\u9A8C\u8BC1\u534F\u8BAE\u517C\u5BB9\u3001\u6D41\u5F0F\u5B8C\u6574\u6027\u3001\u591A\u6A21\u6001\u3001\u7F13\u5B58\u4E0E\u5B98\u65B9\u5BF9\u7167\u7B49\u9AD8\u7EA7\u80FD\u529B\u3002",
            "concurrency" => "\u56FA\u5B9A\u4EE5 1 / 2 / 4 / 8 / 16 \u6863\u5E76\u53D1\uFF0C\u89C2\u5BDF\u7A33\u5B9A\u4E0A\u9650\u3001\u9650\u6D41\u8D77\u70B9\u3001p95 TTFT \u548C tok/s \u53D8\u5316\u3002",
            _ => "\u4EC5\u9002\u5408\u804A\u5929\u6A21\u578B\u3002\u5FEB\u901F\u5224\u65AD\u5F53\u524D\u63A5\u53E3\u662F\u5426\u53EF\u7528\u3001\u9996\u5B57\u54CD\u5E94\u662F\u5426\u7A33\u5B9A\u3002"
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
