namespace NetTest.App.ViewModels;

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
                ? "当前还没有可查看的图表。"
                : ProxyChartDialogStatusSummary;
            return Task.CompletedTask;
        }

        ResetProxyTrendChartAutoOpenSuppression();
        IsProxyTrendChartOpen = true;
        return Task.CompletedTask;
    }

    private Task OpenBatchComparisonChartAsync()
    {
        if (_proxyBatchChartSnapshot?.Image is null)
        {
            StatusMessage = "当前还没有可放大的快速对比图表。";
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
            StatusMessage = "当前还没有可放大的候选深测总览图。";
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
