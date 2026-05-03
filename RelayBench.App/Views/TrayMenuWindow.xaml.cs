using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RelayBench.App.Views;

public partial class TrayMenuWindow : Window
{
    private const double EdgePadding = 10d;
    private bool _isClosing;
    private bool _isLoaded;
    private Point _cursorPosition;
    private Rect _workArea;

    public TrayMenuWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? OpenMainWindowRequested;

    public event EventHandler? ToggleTransparentProxyRequested;

    public event EventHandler? ToggleTokenMeterRequested;

    public event EventHandler? OpenLogDirectoryRequested;

    public event EventHandler? ExitRequested;

    public void PreparePlacement(Point cursorPosition, Rect workArea)
    {
        _cursorPosition = cursorPosition;
        _workArea = workArea;
        if (_isLoaded)
        {
            ApplyPlacement();
        }
    }

    public void ApplyState(bool isTransparentProxyRunning, bool isTokenMeterVisible)
    {
        ProxyStatusTextBlock.Text = isTransparentProxyRunning
            ? "透明代理运行中"
            : "后台待命，双击托盘恢复窗口";
        ProxyStatusPillTextBlock.Text = isTransparentProxyRunning ? "运行中" : "待命";
        ProxyStatusDot.Fill = new SolidColorBrush(isTransparentProxyRunning
            ? Color.FromRgb(34, 197, 94)
            : Color.FromRgb(148, 163, 184));

        ProxyActionGlyphTextBlock.Text = isTransparentProxyRunning ? "\uE71A" : "\uE768";
        ProxyActionTitleTextBlock.Text = isTransparentProxyRunning ? "停止透明代理" : "启动透明代理";
        ProxyActionMetaTextBlock.Text = isTransparentProxyRunning
            ? "结束本地入口调度"
            : "打开本地透明代理入口";

        TokenMeterActionButton.IsEnabled = isTransparentProxyRunning;
        TokenMeterTitleTextBlock.Text = !isTransparentProxyRunning
            ? "Token 悬浮窗"
            : isTokenMeterVisible
                ? "隐藏 Token 悬浮窗"
                : "显示 Token 悬浮窗";
        TokenMeterMetaTextBlock.Text = isTransparentProxyRunning
            ? "实时 token / tok/s"
            : "启动透明代理后可用";
    }

    public void RequestClose()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(120);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacity = new DoubleAnimation(0d, duration)
        {
            EasingFunction = ease
        };
        var translate = new DoubleAnimation(8d, duration)
        {
            EasingFunction = ease
        };
        var scale = new DoubleAnimation(0.985d, duration)
        {
            EasingFunction = ease
        };

        opacity.Completed += (_, _) => Close();
        RootChrome.BeginAnimation(OpacityProperty, opacity);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, translate);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ApplyPlacement();
        PlayOpenAnimation();
    }

    private void Window_OnDeactivated(object? sender, EventArgs e)
        => RequestClose();

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        RequestClose();
    }

    private void OpenMainWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        RequestClose();
        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ProxyActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        RequestClose();
        ToggleTransparentProxyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TokenMeterActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        RequestClose();
        ToggleTokenMeterRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenLogDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        RequestClose();
        OpenLogDirectoryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e)
    {
        RequestClose();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPlacement()
    {
        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 320d;
        var workArea = _workArea == Rect.Empty ? SystemParameters.WorkArea : _workArea;

        var left = _cursorPosition.X - width + 14d;
        var top = _cursorPosition.Y - height - 12d;
        if (top < workArea.Top + EdgePadding)
        {
            top = _cursorPosition.Y + 12d;
        }

        Left = Clamp(left, workArea.Left + EdgePadding, workArea.Right - width - EdgePadding);
        Top = Clamp(top, workArea.Top + EdgePadding, workArea.Bottom - height - EdgePadding);
    }

    private void PlayOpenAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            RootChrome.Opacity = 1d;
            RootTranslate.Y = 0d;
            RootScale.ScaleX = 1d;
            RootScale.ScaleY = 1d;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1d, duration)
        {
            EasingFunction = ease
        });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0d, duration)
        {
            EasingFunction = ease
        });
        var scale = new DoubleAnimation(1d, duration)
        {
            EasingFunction = ease
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private static double Clamp(double value, double min, double max)
        => Math.Min(Math.Max(value, min), max);
}
