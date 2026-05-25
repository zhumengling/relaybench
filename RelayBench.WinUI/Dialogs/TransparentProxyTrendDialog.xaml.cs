using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class TransparentProxyTrendDialog : ContentDialog
{
    public TransparentProxyTrendDialog(TransparentProxyViewModel viewModel, ElementTheme theme)
    {
        Snapshot = viewModel.CreateTrendSnapshot(theme);
        InitializeComponent();
        ApplyCharts();
    }

    public TransparentProxyTrendSnapshot Snapshot { get; }

    private void ApplyCharts()
    {
        ThroughputChart.Series = Snapshot.ThroughputSeries;
        ThroughputChart.YAxes = Snapshot.ThroughputYAxes;
        ThroughputChart.XAxes = Snapshot.ThroughputXAxes;
        LatencyChart.Series = Snapshot.LatencySeries;
        LatencyChart.YAxes = Snapshot.LatencyYAxes;
        LatencyChart.XAxes = Snapshot.LatencyXAxes;
    }
}
