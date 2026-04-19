using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetTest.App.ViewModels;

namespace NetTest.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        Loaded += (_, _) => ScheduleProxyChartViewportWidthUpdate();
        SizeChanged += (_, _) => ScheduleProxyChartViewportWidthUpdate();
        ProxyChartImageScrollViewer.IsVisibleChanged += (_, _) => ScheduleProxyChartViewportWidthUpdate();
    }

    private void LiveOutputTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }

    private void ProxyChartImageScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
        => ScheduleProxyChartViewportWidthUpdate();

    private void ScheduleProxyChartViewportWidthUpdate()
        => Dispatcher.BeginInvoke(
            UpdateProxyChartViewportWidth,
            DispatcherPriority.Loaded);

    private void UpdateProxyChartViewportWidth()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var viewportWidth = ProxyChartImageScrollViewer?.ViewportWidth ?? 0;
        if (viewportWidth <= 0)
        {
            viewportWidth = ProxyChartImageScrollViewer?.ActualWidth ?? 0;
        }

        if (viewportWidth <= 0 && ProxyChartImageScrollViewer?.Parent is FrameworkElement parent)
        {
            viewportWidth = parent.ActualWidth;
        }

        if (viewportWidth <= 0)
        {
            viewportWidth = ActualWidth - 84;
        }

        viewModel.UpdateProxyChartViewportWidth(viewportWidth);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PersistState();
        }

        base.OnClosing(e);
    }
}
