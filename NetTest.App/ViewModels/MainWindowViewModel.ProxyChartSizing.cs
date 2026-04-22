namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private double _proxyChartViewportWidth;
    private double _proxyBatchChartViewportWidth;
    private double _proxyBatchDeepChartViewportWidth;

    internal void UpdateProxyChartViewportWidth(double width)
    {
        var normalized = NormalizeViewportWidth(width);
        if (normalized <= 0 || Math.Abs(_proxyChartViewportWidth - normalized) < 1)
        {
            return;
        }

        _proxyChartViewportWidth = normalized;
        RefreshProxyChartsForViewportContextChange();
    }

    internal void UpdateProxyBatchChartViewportWidth(double width)
    {
        var normalized = NormalizeViewportWidth(width);
        if (normalized <= 0 || Math.Abs(_proxyBatchChartViewportWidth - normalized) < 1)
        {
            return;
        }

        _proxyBatchChartViewportWidth = normalized;
        RefreshProxyChartsForViewportContextChange();
    }

    internal void UpdateProxyBatchDeepChartViewportWidth(double width)
    {
        var normalized = NormalizeViewportWidth(width);
        if (normalized <= 0 || Math.Abs(_proxyBatchDeepChartViewportWidth - normalized) < 1)
        {
            return;
        }

        _proxyBatchDeepChartViewportWidth = normalized;
        RefreshProxyChartsForViewportContextChange();
    }

    private void RefreshProxyChartsForViewportContextChange()
    {
        if (IsProxyTrendChartOpen)
        {
            switch (_activeProxyChartViewMode)
            {
                case ProxyChartViewMode.BatchComparison:
                    if (_currentProxyBatchLiveRows.Count > 0)
                    {
                        UpdateProxyBatchChartLive(_currentProxyBatchLiveRows, _currentProxyBatchLiveTargetCount);
                    }
                    else if (_proxyBatchChartSnapshot?.Image is not null)
                    {
                        RefreshProxyBatchComparisonDialog();
                    }

                    break;
                case ProxyChartViewMode.ConcurrencyPressure:
                    if (_proxyConcurrencyChartSnapshot?.Image is not null)
                    {
                        RefreshProxyConcurrencyChartSnapshot(activate: true);
                    }

                    break;
                case ProxyChartViewMode.BatchDeepComparison:
                    if (_proxyBatchDeepChartSnapshot?.Image is not null)
                    {
                        RefreshBatchDeepComparisonDialog(activate: true);
                    }

                    break;
                case ProxyChartViewMode.StabilityTrend when _lastProxyStabilityResult is not null && !IsBusy:
                    ShowFinalProxySeriesChart(_lastProxyStabilityResult);
                    break;
                case ProxyChartViewMode.SingleLatency when _lastProxySingleResult is not null && !IsBusy:
                    ShowFinalSingleProxyChart(_lastProxySingleResult);
                    break;
            }

            return;
        }

        if (_currentProxyBatchLiveRows.Count > 0 || _proxyBatchChartSnapshot?.Image is not null)
        {
            if (_currentProxyBatchLiveRows.Count > 0)
            {
                UpdateProxyBatchChartLive(_currentProxyBatchLiveRows, _currentProxyBatchLiveTargetCount);
            }
            else
            {
                RefreshProxyBatchComparisonDialog();
            }
        }

        if (_proxyConcurrencyChartSnapshot?.Image is not null)
        {
            RefreshProxyConcurrencyChartSnapshot(activate: false);
        }

        if (_proxyBatchDeepChartSnapshot?.Image is not null)
        {
            RefreshBatchDeepComparisonDialog(activate: false);
        }
    }

    private int ResolvePreferredSingleChartWidth()
    {
        if (_proxyChartViewportWidth <= 0)
        {
            return 1040;
        }

        return (int)Math.Clamp(Math.Floor(_proxyChartViewportWidth - 6), 860, 2600);
    }

    private int ResolvePreferredBatchChartWidth()
    {
        var preferredWidth =
            IsProxyTrendChartOpen && _activeProxyChartViewMode == ProxyChartViewMode.BatchComparison && _proxyChartViewportWidth > 0
                ? _proxyChartViewportWidth
                : _proxyBatchChartViewportWidth;

        if (preferredWidth <= 0)
        {
            preferredWidth = _proxyChartViewportWidth;
        }

        if (preferredWidth <= 0)
        {
            return 1520;
        }

        return (int)Math.Clamp(Math.Floor(preferredWidth - 6), 860, 2600);
    }

    private int ResolvePreferredBatchDeepChartWidth()
    {
        var preferredWidth =
            IsProxyTrendChartOpen && _activeProxyChartViewMode == ProxyChartViewMode.BatchDeepComparison && _proxyChartViewportWidth > 0
                ? _proxyChartViewportWidth
                : _proxyBatchDeepChartViewportWidth;

        if (preferredWidth <= 0)
        {
            preferredWidth = _proxyChartViewportWidth;
        }

        if (preferredWidth <= 0)
        {
            return 1320;
        }

        return (int)Math.Clamp(Math.Floor(preferredWidth - 6), 980, 3200);
    }

    private int ResolvePreferredConcurrencyChartWidth()
    {
        if (_proxyChartViewportWidth <= 0)
        {
            return 1320;
        }

        return (int)Math.Clamp(Math.Floor(_proxyChartViewportWidth - 6), 980, 3200);
    }

    private static double NormalizeViewportWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            return 0;
        }

        return Math.Max(0, width);
    }
}
