using System.Windows.Media.Imaging;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

internal enum ProxyChartViewMode
{
    None,
    SingleLatency,
    StabilityTrend,
    ConcurrencyPressure,
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
    BitmapSource? Image,
    IReadOnlyList<ProxyChartHitRegion>? HitRegions = null,
    IReadOnlyList<ProxyChartActivityRegion>? ActivityRegions = null,
    IReadOnlyList<ProxySingleCapabilityChartItem>? SingleCapabilityItems = null,
    IReadOnlyList<ProxyBatchComparisonChartItem>? BatchComparisonItems = null,
    IReadOnlyList<ProxyTrendEntry>? StabilityTrendItems = null,
    int StabilityTrendRequestedRounds = 0,
    bool IsStabilityTrendRunning = false,
    IReadOnlyList<ProxyConcurrencyChartItem>? ConcurrencyItems = null);

public sealed partial class MainWindowViewModel
{
    private ProxyChartViewMode _activeProxyChartViewMode;
    private ProxyChartDialogSnapshot? _proxySingleChartSnapshot;
    private ProxyChartDialogSnapshot? _proxyTrendChartSnapshot;
    private ProxyChartDialogSnapshot? _proxyConcurrencyChartSnapshot;
    private ProxyChartDialogSnapshot? _proxyBatchChartSnapshot;
    private ProxyChartDialogSnapshot? _proxyBatchDeepChartSnapshot;
    private IReadOnlyList<ProxyChartHitRegion> _proxyChartDialogHitRegions = Array.Empty<ProxyChartHitRegion>();
    private IReadOnlyList<ProxyChartActivityRegion> _proxyChartDialogActivityRegions = Array.Empty<ProxyChartActivityRegion>();
    private IReadOnlyList<ProxySingleCapabilityChartRowViewModel> _proxyChartDialogSingleCapabilityRows =
        Array.Empty<ProxySingleCapabilityChartRowViewModel>();
    private IReadOnlyList<ProxyBatchComparisonChartRowViewModel> _proxyChartDialogBatchComparisonRows =
        Array.Empty<ProxyBatchComparisonChartRowViewModel>();
    private IReadOnlyList<ProxyStabilityTrendChartRowViewModel> _proxyChartDialogStabilityTrendRows =
        Array.Empty<ProxyStabilityTrendChartRowViewModel>();
    private IReadOnlyList<ProxyConcurrencyChartRowViewModel> _proxyChartDialogConcurrencyRows =
        Array.Empty<ProxyConcurrencyChartRowViewModel>();
    private readonly HashSet<string> _revealedSingleCapabilityMetricKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revealedBatchComparisonRowKeys = new(StringComparer.Ordinal);

    public bool CanToggleProxyChartView
        => !IsBusy &&
           _activeProxyChartViewMode is ProxyChartViewMode.SingleLatency or ProxyChartViewMode.StabilityTrend &&
           _proxySingleChartSnapshot?.Image is not null &&
           _proxyTrendChartSnapshot?.Image is not null;

    public string ProxyChartToggleButtonText
        => _activeProxyChartViewMode == ProxyChartViewMode.StabilityTrend
            ? "\u5207\u6362\u5355\u6B21\u5EF6\u8FDF\u56FE"
            : "\u5207\u6362\u7A33\u5B9A\u8D8B\u52BF\u56FE";

    public BitmapSource? BatchComparisonChartImage
        => _proxyBatchChartSnapshot?.Image;

    public bool HasBatchComparisonChart
        => BatchComparisonChartImage is not null;

    public string BatchComparisonChartStatusSummary
        => _proxyBatchChartSnapshot?.StatusSummary ?? "\u5B8C\u6210\u5FEB\u901F\u5BF9\u6BD4\u540E\uFF0C\u8FD9\u91CC\u4F1A\u663E\u793A\u6392\u884C\u699C\u56FE\u8868\u3002";

    public BitmapSource? ProxyConcurrencyChartImage
        => _proxyConcurrencyChartSnapshot?.Image;

    public bool HasProxyConcurrencyChart
        => ProxyConcurrencyChartImage is not null;

    public string ProxyConcurrencyChartStatusSummary
        => _proxyConcurrencyChartSnapshot?.StatusSummary ?? "\u5B8C\u6210\u5E76\u53D1\u538B\u6D4B\u540E\uFF0C\u8FD9\u91CC\u4F1A\u663E\u793A\u5E76\u53D1\u6863\u4F4D\u56FE\u8868\u3002";

    public BitmapSource? BatchDeepComparisonChartImage
        => _proxyBatchDeepChartSnapshot?.Image;

    public bool HasBatchDeepComparisonChart
        => BatchDeepComparisonChartImage is not null;

    public string BatchDeepComparisonChartStatusSummary
        => _proxyBatchDeepChartSnapshot?.StatusSummary ?? "\u52FE\u9009\u5019\u9009\u9879\u5E76\u5F00\u59CB\u6DF1\u5EA6\u6D4B\u8BD5\u540E\uFF0C\u8FD9\u91CC\u4F1A\u663E\u793A\u5019\u9009\u7AD9\u70B9\u6DF1\u6D4B\u603B\u89C8\u56FE\u3002";

    internal IReadOnlyList<ProxyChartHitRegion> CurrentProxyChartHitRegions
        => _proxyChartDialogHitRegions;

    internal IReadOnlyList<ProxyChartActivityRegion> CurrentProxyChartActivityRegions
        => _proxyChartDialogActivityRegions;

    public IReadOnlyList<ProxySingleCapabilityChartRowViewModel> ProxyChartDialogSingleCapabilityRows
        => _proxyChartDialogSingleCapabilityRows;

    public IReadOnlyList<ProxyBatchComparisonChartRowViewModel> ProxyChartDialogBatchComparisonRows
        => _proxyChartDialogBatchComparisonRows;

    public IReadOnlyList<ProxyStabilityTrendChartRowViewModel> ProxyChartDialogStabilityTrendRows
        => _proxyChartDialogStabilityTrendRows;

    public IReadOnlyList<ProxyConcurrencyChartRowViewModel> ProxyChartDialogConcurrencyRows
        => _proxyChartDialogConcurrencyRows;

    public bool IsNativeSingleCapabilityChartVisible
        => _activeProxyChartViewMode == ProxyChartViewMode.SingleLatency &&
           _proxyChartDialogSingleCapabilityRows.Count > 0;

    public bool IsNativeBatchComparisonChartVisible
        => _activeProxyChartViewMode == ProxyChartViewMode.BatchComparison &&
           _proxyChartDialogBatchComparisonRows.Count > 0;

    public bool IsNativeStabilityTrendChartVisible
        => _activeProxyChartViewMode == ProxyChartViewMode.StabilityTrend &&
           _proxyChartDialogStabilityTrendRows.Count > 0;

    public bool IsNativeConcurrencyChartVisible
        => _activeProxyChartViewMode == ProxyChartViewMode.ConcurrencyPressure &&
           _proxyChartDialogConcurrencyRows.Count > 0;

    public bool IsBitmapProxyChartVisible
        => HasProxyChartDialogImage &&
           !IsNativeSingleCapabilityChartVisible &&
           !IsNativeBatchComparisonChartVisible &&
           !IsNativeStabilityTrendChartVisible &&
           !IsNativeConcurrencyChartVisible;

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
            case ProxyChartViewMode.ConcurrencyPressure:
                _proxyConcurrencyChartSnapshot = snapshot;
                OnPropertyChanged(nameof(ProxyConcurrencyChartImage));
                OnPropertyChanged(nameof(HasProxyConcurrencyChart));
                OnPropertyChanged(nameof(ProxyConcurrencyChartStatusSummary));
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

        return mode is ProxyChartViewMode.ConcurrencyPressure or ProxyChartViewMode.BatchComparison or ProxyChartViewMode.BatchDeepComparison;
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
            ProxyChartViewMode.ConcurrencyPressure => _proxyConcurrencyChartSnapshot,
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
        _proxyChartDialogHitRegions = snapshot.HitRegions ?? Array.Empty<ProxyChartHitRegion>();
        _proxyChartDialogActivityRegions = snapshot.ActivityRegions ?? Array.Empty<ProxyChartActivityRegion>();
        _proxyChartDialogSingleCapabilityRows = snapshot.SingleCapabilityItems is null
            ? Array.Empty<ProxySingleCapabilityChartRowViewModel>()
            : ProxySingleCapabilityChartRowViewModel.CreateRows(
                snapshot.SingleCapabilityItems,
                ResolveNewSingleCapabilityMetricRevealKeys(snapshot.SingleCapabilityItems));
        _proxyChartDialogBatchComparisonRows = snapshot.BatchComparisonItems is null
            ? Array.Empty<ProxyBatchComparisonChartRowViewModel>()
            : ProxyBatchComparisonChartRowViewModel.CreateRows(
                snapshot.BatchComparisonItems,
                ResolveNewBatchComparisonRevealKeys(snapshot.BatchComparisonItems));
        _proxyChartDialogStabilityTrendRows = snapshot.StabilityTrendItems is null
            ? Array.Empty<ProxyStabilityTrendChartRowViewModel>()
            : ProxyStabilityTrendChartRowViewModel.CreateRows(
                snapshot.StabilityTrendItems,
                snapshot.StabilityTrendRequestedRounds,
                snapshot.IsStabilityTrendRunning);
        _proxyChartDialogConcurrencyRows = snapshot.ConcurrencyItems is null
            ? Array.Empty<ProxyConcurrencyChartRowViewModel>()
            : ProxyConcurrencyChartRowViewModel.CreateRows(snapshot.ConcurrencyItems);
        RefreshProxyChartViewState();
    }

    private IReadOnlySet<string> ResolveNewSingleCapabilityMetricRevealKeys(
        IReadOnlyList<ProxySingleCapabilityChartItem> items)
    {
        if (items.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var newKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!item.IsCompleted || item.MetricValueMs is not > 0)
            {
                continue;
            }

            var key = ProxySingleCapabilityChartRowViewModel.CreateMetricRevealKey(item);
            if (_revealedSingleCapabilityMetricKeys.Add(key))
            {
                newKeys.Add(key);
            }
        }

        return newKeys;
    }

    private IReadOnlySet<string> ResolveNewBatchComparisonRevealKeys(
        IReadOnlyList<ProxyBatchComparisonChartItem> items)
    {
        if (items.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var newKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.RunCount <= 0)
            {
                continue;
            }

            var key = ProxyBatchComparisonChartRowViewModel.CreateRevealKey(item);
            if (_revealedBatchComparisonRowKeys.Add(key))
            {
                newKeys.Add(key);
            }
        }

        return newKeys;
    }

    private void RefreshProxyChartViewState()
    {
        OnPropertyChanged(nameof(IsProxyChartImageOnlyMode));
        OnPropertyChanged(nameof(ProxyChartDialogSingleCapabilityRows));
        OnPropertyChanged(nameof(ProxyChartDialogBatchComparisonRows));
        OnPropertyChanged(nameof(ProxyChartDialogStabilityTrendRows));
        OnPropertyChanged(nameof(ProxyChartDialogConcurrencyRows));
        OnPropertyChanged(nameof(IsNativeSingleCapabilityChartVisible));
        OnPropertyChanged(nameof(IsNativeBatchComparisonChartVisible));
        OnPropertyChanged(nameof(IsNativeStabilityTrendChartVisible));
        OnPropertyChanged(nameof(IsNativeConcurrencyChartVisible));
        OnPropertyChanged(nameof(IsBitmapProxyChartVisible));
        OnPropertyChanged(nameof(CanToggleProxyChartView));
        OnPropertyChanged(nameof(CanToggleProxyChartImageOnlyMode));
        OnPropertyChanged(nameof(ProxyChartToggleButtonText));
        OnPropertyChanged(nameof(ProxyChartImageOnlyModeButtonText));
        ToggleProxyChartViewCommand.RaiseCanExecuteChanged();
        ToggleProxyChartImageOnlyModeCommand.RaiseCanExecuteChanged();
    }
}
