using System.Windows.Media.Imaging;

namespace NetTest.App.ViewModels;

internal enum ProxyChartViewMode
{
    None,
    SingleLatency,
    StabilityTrend,
    BatchComparison,
    BatchDeepComparison
}

internal sealed record ProxyChartDialogSnapshot(
    string Title,
    string Intro,
    string Summary,
    string CapabilitySummary,
    string CapabilityDetail,
    string GuideSummary,
    string StatusSummary,
    string EmptyStateText,
    BitmapSource? Image);

public sealed partial class MainWindowViewModel
{
    private ProxyChartViewMode _activeProxyChartViewMode;
    private ProxyChartDialogSnapshot? _proxySingleChartSnapshot;
    private ProxyChartDialogSnapshot? _proxyTrendChartSnapshot;
    private ProxyChartDialogSnapshot? _proxyBatchChartSnapshot;
    private ProxyChartDialogSnapshot? _proxyBatchDeepChartSnapshot;

    public bool CanToggleProxyChartView
        => !IsBusy &&
           _activeProxyChartViewMode is ProxyChartViewMode.SingleLatency or ProxyChartViewMode.StabilityTrend &&
           _proxySingleChartSnapshot?.Image is not null &&
           _proxyTrendChartSnapshot?.Image is not null;

    public string ProxyChartToggleButtonText
        => _activeProxyChartViewMode == ProxyChartViewMode.StabilityTrend
            ? "切换单次延迟图"
            : "切换稳定趋势图";

    public BitmapSource? BatchComparisonChartImage
        => _proxyBatchChartSnapshot?.Image;

    public bool HasBatchComparisonChart
        => BatchComparisonChartImage is not null;

    public string BatchComparisonChartStatusSummary
        => _proxyBatchChartSnapshot?.StatusSummary ?? "完成快速对比后，这里显示排行榜图表。";

    public BitmapSource? BatchDeepComparisonChartImage
        => _proxyBatchDeepChartSnapshot?.Image;

    public bool HasBatchDeepComparisonChart
        => BatchDeepComparisonChartImage is not null;

    public string BatchDeepComparisonChartStatusSummary
        => _proxyBatchDeepChartSnapshot?.StatusSummary ?? "勾选排行榜列表项并开始深度测试后，这里显示候选站点深度测试总览图。";

    private bool CanToggleProxyChartViewAction()
        => CanToggleProxyChartView;

    private bool CanToggleProxyChartImageOnlyModeAction()
        => CanToggleProxyChartImageOnlyMode;

    private Task ToggleProxyChartViewAsync()
    {
        if (_activeProxyChartViewMode == ProxyChartViewMode.StabilityTrend)
        {
            ActivateProxyChartView(ProxyChartViewMode.SingleLatency);
        }
        else
        {
            ActivateProxyChartView(ProxyChartViewMode.StabilityTrend);
        }

        return Task.CompletedTask;
    }

    private Task ToggleProxyChartImageOnlyModeAsync()
    {
        IsProxyChartImageOnlyMode = !IsProxyChartImageOnlyMode;
        RefreshProxyChartViewState();
        return Task.CompletedTask;
    }

    private void SetProxyChartSnapshot(
        ProxyChartViewMode mode,
        ProxyChartDialogSnapshot snapshot,
        bool activate)
    {
        switch (mode)
        {
            case ProxyChartViewMode.SingleLatency:
                _proxySingleChartSnapshot = snapshot;
                break;
            case ProxyChartViewMode.StabilityTrend:
                _proxyTrendChartSnapshot = snapshot;
                break;
            case ProxyChartViewMode.BatchComparison:
                _proxyBatchChartSnapshot = snapshot;
                OnPropertyChanged(nameof(BatchComparisonChartImage));
                OnPropertyChanged(nameof(HasBatchComparisonChart));
                OnPropertyChanged(nameof(BatchComparisonChartStatusSummary));
                break;
            case ProxyChartViewMode.BatchDeepComparison:
                _proxyBatchDeepChartSnapshot = snapshot;
                OnPropertyChanged(nameof(BatchDeepComparisonChartImage));
                OnPropertyChanged(nameof(HasBatchDeepComparisonChart));
                OnPropertyChanged(nameof(BatchDeepComparisonChartStatusSummary));
                break;
        }

        if (activate || ShouldAutoApplyProxyChart(mode))
        {
            ApplyProxyChartSnapshot(mode, snapshot);
            return;
        }

        RefreshProxyChartViewState();
    }

    private bool ShouldAutoApplyProxyChart(ProxyChartViewMode mode)
    {
        if (ProxyChartDialogImage is null)
        {
            return true;
        }

        if (_activeProxyChartViewMode == ProxyChartViewMode.None ||
            _activeProxyChartViewMode == mode)
        {
            return true;
        }

        return mode is ProxyChartViewMode.BatchComparison or ProxyChartViewMode.BatchDeepComparison;
    }

    private void ActivateProxyChartView(ProxyChartViewMode mode)
    {
        var snapshot = ResolveProxyChartSnapshot(mode);
        if (snapshot is null)
        {
            return;
        }

        ApplyProxyChartSnapshot(mode, snapshot);
    }

    private ProxyChartDialogSnapshot? ResolveProxyChartSnapshot(ProxyChartViewMode mode)
        => mode switch
        {
            ProxyChartViewMode.SingleLatency => _proxySingleChartSnapshot,
            ProxyChartViewMode.StabilityTrend => _proxyTrendChartSnapshot,
            ProxyChartViewMode.BatchComparison => _proxyBatchChartSnapshot,
            ProxyChartViewMode.BatchDeepComparison => _proxyBatchDeepChartSnapshot,
            _ => null
        };

    private void ApplyProxyChartSnapshot(ProxyChartViewMode mode, ProxyChartDialogSnapshot snapshot)
    {
        _activeProxyChartViewMode = mode;
        ProxyChartDialogTitle = snapshot.Title;
        ProxyChartDialogIntro = snapshot.Intro;
        ProxyChartDialogSummary = snapshot.Summary;
        ProxyChartDialogCapabilitySummary = snapshot.CapabilitySummary;
        ProxyChartDialogCapabilityDetail = snapshot.CapabilityDetail;
        ProxyChartDialogGuideSummary = snapshot.GuideSummary;
        ProxyChartDialogStatusSummary = snapshot.StatusSummary;
        ProxyChartDialogEmptyStateText = snapshot.EmptyStateText;
        ProxyChartDialogImage = snapshot.Image;
        RefreshProxyChartViewState();
    }

    private void RefreshProxyChartViewState()
    {
        OnPropertyChanged(nameof(IsProxyChartImageOnlyMode));
        OnPropertyChanged(nameof(CanToggleProxyChartView));
        OnPropertyChanged(nameof(CanToggleProxyChartImageOnlyMode));
        OnPropertyChanged(nameof(ProxyChartToggleButtonText));
        OnPropertyChanged(nameof(ProxyChartImageOnlyModeButtonText));
        ToggleProxyChartViewCommand.RaiseCanExecuteChanged();
        ToggleProxyChartImageOnlyModeCommand.RaiseCanExecuteChanged();
    }
}


