using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetTest.App.Services;
using NetTest.App.ViewModels;

namespace NetTest.App;

public partial class MainWindow : Window
{
    private readonly ToolTip _proxyChartHoverToolTip = new()
    {
        Placement = PlacementMode.Mouse,
        StaysOpen = true,
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(208, 213, 221)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 8, 10, 8),
        HasDropShadow = true
    };

    private ProxyChartHitRegion? _activeProxyChartHitRegion;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        ProxyChartDialogImageControl.ToolTip = _proxyChartHoverToolTip;
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

    private void ProxyChartDialogImage_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Image image ||
            DataContext is not MainWindowViewModel viewModel ||
            viewModel.CurrentProxyChartHitRegions.Count == 0)
        {
            HideProxyChartHitToolTip();
            return;
        }

        var position = e.GetPosition(image);
        var hitRegion = viewModel.CurrentProxyChartHitRegions.FirstOrDefault(region => region.Bounds.Contains(position));
        if (hitRegion is null)
        {
            HideProxyChartHitToolTip();
            return;
        }

        if (_proxyChartHoverToolTip.IsOpen &&
            _activeProxyChartHitRegion is not null &&
            string.Equals(_activeProxyChartHitRegion.Title, hitRegion.Title, StringComparison.Ordinal) &&
            string.Equals(_activeProxyChartHitRegion.Description, hitRegion.Description, StringComparison.Ordinal))
        {
            return;
        }

        _activeProxyChartHitRegion = hitRegion;
        _proxyChartHoverToolTip.Content = BuildProxyChartHitToolTipContent(hitRegion);
        _proxyChartHoverToolTip.IsOpen = true;
    }

    private void ProxyChartDialogImage_OnMouseLeave(object sender, MouseEventArgs e)
        => HideProxyChartHitToolTip();

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

    private void HideProxyChartHitToolTip()
    {
        _activeProxyChartHitRegion = null;
        if (_proxyChartHoverToolTip.IsOpen)
        {
            _proxyChartHoverToolTip.IsOpen = false;
        }
    }

    private static object BuildProxyChartHitToolTipContent(ProxyChartHitRegion hitRegion)
    {
        var panel = new StackPanel
        {
            MaxWidth = 320
        };

        panel.Children.Add(new TextBlock
        {
            Text = hitRegion.Title,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Black
        });

        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            Text = hitRegion.Description,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 84, 103))
        });

        return panel;
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
