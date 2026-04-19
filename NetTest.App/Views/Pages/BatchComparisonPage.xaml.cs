using System.Windows.Controls;
using NetTest.App.ViewModels;

namespace NetTest.App.Views.Pages;

public partial class BatchComparisonPage : UserControl
{
    public BatchComparisonPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateBatchChartViewportWidth();
            UpdateBatchDeepChartViewportWidth();
        };
        SizeChanged += (_, _) =>
        {
            UpdateBatchChartViewportWidth();
            UpdateBatchDeepChartViewportWidth();
        };
    }

    private void BatchComparisonChartPreviewHost_OnSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        => UpdateBatchChartViewportWidth();

    private void BatchDeepComparisonChartPreviewHost_OnSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        => UpdateBatchDeepChartViewportWidth();

    private void UpdateBatchChartViewportWidth()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var viewportWidth = BatchComparisonChartPreviewHost?.ActualWidth ?? 0;

        if (viewportWidth <= 0)
        {
            viewportWidth = ActualWidth - 32;
        }

        viewModel.UpdateProxyBatchChartViewportWidth(viewportWidth);
    }

    private void UpdateBatchDeepChartViewportWidth()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var viewportWidth = BatchDeepComparisonChartPreviewHost?.ActualWidth ?? 0;

        if (viewportWidth <= 0)
        {
            viewportWidth = ActualWidth - 32;
        }

        viewModel.UpdateProxyBatchDeepChartViewportWidth(viewportWidth);
    }
}
