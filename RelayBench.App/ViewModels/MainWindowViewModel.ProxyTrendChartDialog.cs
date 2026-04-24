namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void ResetProxyTrendChartAutoOpenSuppression()
        => _isProxyTrendChartAutoOpenSuppressed = false;

    private void SuppressProxyTrendChartAutoOpen()
        => _isProxyTrendChartAutoOpenSuppressed = true;

    private bool AutoOpenProxyTrendChartIfAllowed()
    {
        if (_isProxyTrendChartAutoOpenSuppressed)
        {
            return false;
        }

        IsProxyTrendChartOpen = true;
        return true;
    }

    private Task OpenProxyTrendChartAsync()
    {
        if (ProxyChartDialogImage is null)
        {
            if (_proxySingleChartSnapshot?.Image is not null)
            {
                ActivateProxyChartView(ProxyChartViewMode.SingleLatency);
            }
            else if (_proxyTrendChartSnapshot?.Image is not null)
            {
                ActivateProxyChartView(ProxyChartViewMode.StabilityTrend);
            }
            else if (_proxyConcurrencyChartSnapshot?.Image is not null)
            {
                ActivateProxyChartView(ProxyChartViewMode.ConcurrencyPressure);
            }
            else if (_proxyBatchDeepChartSnapshot?.Image is not null)
            {
                ActivateProxyChartView(ProxyChartViewMode.BatchDeepComparison);
            }
            else if (_proxyBatchChartSnapshot?.Image is not null)
            {
                ActivateProxyChartView(ProxyChartViewMode.BatchComparison);
            }
        }

        if (!HasProxyChartDialogImage)
        {
            StatusMessage = string.IsNullOrWhiteSpace(ProxyChartDialogStatusSummary)
                ? "\u5F53\u524D\u8FD8\u6CA1\u6709\u53EF\u67E5\u770B\u7684\u56FE\u8868\u3002"
                : ProxyChartDialogStatusSummary;
            return Task.CompletedTask;
        }

        ResetProxyTrendChartAutoOpenSuppression();
        IsProxyTrendChartOpen = true;
        return Task.CompletedTask;
    }

    private Task OpenCurrentSingleStationChartAsync()
        => SelectedSingleStationModeKey switch
        {
            SingleStationModeStability => OpenSelectedProxyChartAsync(
                ProxyChartViewMode.StabilityTrend,
                _proxyTrendChartSnapshot,
                "当前还没有稳定性测试图表，请先运行稳定性测试。"),
            SingleStationModeConcurrency => OpenSelectedProxyChartAsync(
                ProxyChartViewMode.ConcurrencyPressure,
                _proxyConcurrencyChartSnapshot,
                ProxyConcurrencyChartStatusSummary),
            _ => OpenProxySingleChartAsync()
        };

    private Task OpenSelectedProxyChartAsync(
        ProxyChartViewMode mode,
        ProxyChartDialogSnapshot? snapshot,
        string emptyMessage)
    {
        if (snapshot?.Image is null)
        {
            StatusMessage = emptyMessage;
            return Task.CompletedTask;
        }

        ActivateProxyChartView(mode);
        ResetProxyTrendChartAutoOpenSuppression();
        IsProxyTrendChartOpen = true;
        RefreshProxyChartsForViewportContextChange();
        return Task.CompletedTask;
    }

    private Task OpenProxySingleChartAsync()
    {
        if (_proxySingleChartSnapshot?.Image is null)
        {
            StatusMessage = string.IsNullOrWhiteSpace(ProxyChartDialogStatusSummary)
                ? "当前还没有可查看的接口诊断图表。"
                : ProxyChartDialogStatusSummary;
            return Task.CompletedTask;
        }

        ActivateProxyChartView(ProxyChartViewMode.SingleLatency);
        ResetProxyTrendChartAutoOpenSuppression();
        IsProxyTrendChartOpen = true;
        RefreshProxyChartsForViewportContextChange();
        return Task.CompletedTask;
    }

    private Task OpenProxyConcurrencyChartAsync()
    {
        if (_proxyConcurrencyChartSnapshot?.Image is null)
        {
            StatusMessage = ProxyConcurrencyChartStatusSummary;
            return Task.CompletedTask;
        }

        ActivateProxyChartView(ProxyChartViewMode.ConcurrencyPressure);
        ResetProxyTrendChartAutoOpenSuppression();
        IsProxyTrendChartOpen = true;
        RefreshProxyChartsForViewportContextChange();
        return Task.CompletedTask;
    }

    private Task OpenBatchComparisonChartAsync()
    {
        if (_proxyBatchChartSnapshot?.Image is null)
        {
            StatusMessage = "\u5F53\u524D\u8FD8\u6CA1\u6709\u53EF\u653E\u5927\u7684\u5FEB\u901F\u5BF9\u6BD4\u56FE\u8868\u3002";
            return Task.CompletedTask;
        }

        ActivateProxyChartView(ProxyChartViewMode.BatchComparison);
        ResetProxyTrendChartAutoOpenSuppression();
        IsProxyTrendChartOpen = true;
        RefreshProxyChartsForViewportContextChange();
        return Task.CompletedTask;
    }

    private Task OpenBatchDeepComparisonChartAsync()
    {
        if (_proxyBatchDeepChartSnapshot?.Image is null)
        {
            StatusMessage = "\u5F53\u524D\u8FD8\u6CA1\u6709\u53EF\u653E\u5927\u7684\u5019\u9009\u6DF1\u6D4B\u603B\u89C8\u56FE\u3002";
            return Task.CompletedTask;
        }

        ActivateProxyChartView(ProxyChartViewMode.BatchDeepComparison);
        ResetProxyTrendChartAutoOpenSuppression();
        IsProxyTrendChartOpen = true;
        RefreshProxyChartsForViewportContextChange();
        return Task.CompletedTask;
    }

    private Task CloseProxyTrendChartAsync()
    {
        SuppressProxyTrendChartAutoOpen();
        IsProxyChartImageOnlyMode = false;
        IsProxyTrendChartOpen = false;
        RefreshProxyChartsForViewportContextChange();
        return Task.CompletedTask;
    }

    private void OpenProxyTrendChartIfAvailable()
    {
        if (HasProxyChartDialogImage)
        {
            AutoOpenProxyTrendChartIfAllowed();
        }
    }
}
