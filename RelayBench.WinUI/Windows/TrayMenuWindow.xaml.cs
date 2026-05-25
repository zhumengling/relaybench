using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RelayBench.WinUI.Services;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using Drawing = System.Drawing;

namespace RelayBench.WinUI.Desktop;

public sealed partial class TrayMenuWindow : Window
{
    private const int WindowWidthDips = 286;
    private const int FallbackWindowHeightDips = 408;
    private const int EdgePaddingDips = 10;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdtEffectiveDpi = 0;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopMost = new(-1);

    private readonly IntPtr _hwnd;
    private readonly AppWindow? _appWindow;
    private bool _canClose;
    private bool _canDismissOnDeactivate;
    private bool _isProxyRunning;

    public TrayMenuWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = TryGetAppWindow(_hwnd);
        ApplyTheme(ThemeService.GetCurrentTheme());
        ConfigureChrome();
        Activated += OnActivated;
    }

    public event EventHandler? OpenMainWindowRequested;

    public event EventHandler? ToggleTransparentProxyRequested;

    public event EventHandler? ToggleTokenMeterRequested;

    public event EventHandler? OpenLogDirectoryRequested;

    public event EventHandler? ExitRequested;

    public void ApplyTheme(ElementTheme theme)
    {
        RootSurface.RequestedTheme = theme;
        ApplyProxyStatusVisualState(_isProxyRunning);
    }

    public void ApplyState(bool isProxyRunning, bool isTokenMeterRequested, string endpoint, string tokenSummary)
    {
        _isProxyRunning = isProxyRunning;
        ProxyStatusTextBlock.Text = isProxyRunning ? endpoint : "后台就绪";
        ApplyProxyStatusVisualState(isProxyRunning);

        ProxyActionGlyph.Glyph = isProxyRunning ? "\uE769" : "\uE768";
        ProxyActionTitleTextBlock.Text = isProxyRunning ? "停止透明代理" : "启动透明代理";
        ProxyActionMetaTextBlock.Text = isProxyRunning ? "关闭本地统一入口" : "打开本地统一入口";

        TokenMeterTitleTextBlock.Text = isTokenMeterRequested ? "隐藏 Token 计" : "显示 Token 计";
        TokenMeterMetaTextBlock.Text = string.IsNullOrWhiteSpace(tokenSummary)
            ? "浮动 tok/s 监视器"
            : tokenSummary;
    }

    public void PreparePlacement(Drawing.Point cursorPositionPixels)
    {
        var workArea = GetWorkArea(cursorPositionPixels);
        var scale = GetDpiScale(cursorPositionPixels);
        var windowWidthPixels = DipsToPixels(WindowWidthDips, scale);
        var edgePaddingPixels = DipsToPixels(EdgePaddingDips, scale);
        var measuredHeight = MeasureMenuHeight(workArea, scale, edgePaddingPixels);
        ApplyHeightLimit(measuredHeight, scale);
        _appWindow?.Resize(new SizeInt32(windowWidthPixels, measuredHeight));

        var left = Clamp(
            cursorPositionPixels.X - windowWidthPixels + DipsToPixels(14, scale),
            workArea.Left + edgePaddingPixels,
            workArea.Right - windowWidthPixels - edgePaddingPixels);
        var top = Clamp(
            cursorPositionPixels.Y - measuredHeight + DipsToPixels(10, scale),
            workArea.Top + edgePaddingPixels,
            workArea.Bottom - measuredHeight - edgePaddingPixels);
        MoveWindow(left, top);
    }

    public void RequestClose()
    {
        _canClose = true;
        Close();
    }

    private static AppWindow? TryGetAppWindow(IntPtr hwnd)
    {
        try
        {
            return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        }
        catch
        {
            return null;
        }
    }

    private void ConfigureChrome()
    {
        var scale = GetDpiScaleForWindow();
        _appWindow?.Resize(new SizeInt32(
            DipsToPixels(WindowWidthDips, scale),
            DipsToPixels(FallbackWindowHeightDips, scale)));
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var exStyle = GetWindowLong(_hwnd, GwlExStyle);
        exStyle |= WsExToolWindow;
        exStyle &= ~WsExAppWindow;
        SetWindowLong(_hwnd, GwlExStyle, exStyle);
        SetWindowPos(_hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private int MeasureMenuHeight(RECT workArea, double scale, int edgePaddingPixels)
    {
        RootChrome.ClearValue(FrameworkElement.MaxHeightProperty);
        MenuScrollViewer.ClearValue(FrameworkElement.MaxHeightProperty);
        RootChrome.Measure(new Windows.Foundation.Size(WindowWidthDips, double.PositiveInfinity));

        var desiredHeight = (int)Math.Ceiling(RootChrome.DesiredSize.Height);
        if (desiredHeight <= 0)
        {
            desiredHeight = (int)Math.Ceiling(RootChrome.ActualHeight);
        }

        var unclippedHeightDips = desiredHeight > 0 ? desiredHeight : FallbackWindowHeightDips;
        var unclippedHeightPixels = DipsToPixels(unclippedHeightDips, scale);
        var availableHeight = Math.Max(1, workArea.Bottom - workArea.Top - (edgePaddingPixels * 2));
        return Math.Min(unclippedHeightPixels, availableHeight);
    }

    private void ApplyHeightLimit(int heightPixels, double scale)
    {
        var heightDips = PixelsToDips(heightPixels, scale);
        RootChrome.MaxHeight = heightDips;
        MenuScrollViewer.MaxHeight = Math.Max(
            1,
            heightDips - RootChrome.Padding.Top - RootChrome.Padding.Bottom - 2);
    }

    private void RootChrome_Loaded(object sender, RoutedEventArgs e)
    {
        RootSurface.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() => _canDismissOnDeactivate = true);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_canClose &&
            _canDismissOnDeactivate &&
            args.WindowActivationState == WindowActivationState.Deactivated)
        {
            RequestClose();
        }
    }

    private void RootSurface_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            RequestClose();
            e.Handled = true;
        }
    }

    private void OpenMainWindowButton_Click(object sender, RoutedEventArgs e)
    {
        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
        RequestClose();
    }

    private void ProxyActionButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTransparentProxyRequested?.Invoke(this, EventArgs.Empty);
        RequestClose();
    }

    private void TokenMeterActionButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTokenMeterRequested?.Invoke(this, EventArgs.Empty);
        RequestClose();
    }

    private void OpenLogDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        OpenLogDirectoryRequested?.Invoke(this, EventArgs.Empty);
        RequestClose();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
        RequestClose();
    }

    private RECT GetWorkArea(Drawing.Point cursorPositionPixels)
    {
        var monitor = MonitorFromPoint(new POINT { X = cursorPositionPixels.X, Y = cursorPositionPixels.Y }, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO
            {
                cbSize = Marshal.SizeOf<MONITORINFO>()
            };
            if (GetMonitorInfo(monitor, ref info))
            {
                return info.rcWork;
            }
        }

        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private void MoveWindow(int left, int top)
    {
        if (_appWindow is not null)
        {
            _appWindow.Move(new PointInt32(left, top));
        }
        else
        {
            SetWindowPos(_hwnd, HwndTopMost, left, top, 0, 0, SwpNoSize | SwpNoActivate);
        }

        SetWindowPos(_hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void ApplyProxyStatusVisualState(bool isProxyRunning)
    {
        var liveVisibility = isProxyRunning ? Visibility.Visible : Visibility.Collapsed;
        var idleVisibility = isProxyRunning ? Visibility.Collapsed : Visibility.Visible;

        ProxyStatusLiveDot.Visibility = liveVisibility;
        ProxyStatusLivePillTextBlock.Visibility = liveVisibility;
        ProxyStatusIdleDot.Visibility = idleVisibility;
        ProxyStatusIdlePillTextBlock.Visibility = idleVisibility;
    }

    private double GetDpiScale(Drawing.Point cursorPositionPixels)
    {
        var monitor = MonitorFromPoint(
            new POINT { X = cursorPositionPixels.X, Y = cursorPositionPixels.Y },
            MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            try
            {
                var result = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _);
                if (result == 0 && dpiX > 0)
                {
                    return dpiX / 96.0;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        return GetDpiScaleForWindow();
    }

    private double GetDpiScaleForWindow()
    {
        var dpi = GetDpiForWindow(_hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private static int DipsToPixels(double dips, double scale)
        => Math.Max(1, (int)Math.Ceiling(dips * scale));

    private static double PixelsToDips(int pixels, double scale)
        => scale > 0 ? pixels / scale : pixels;

    private static int Clamp(int value, int min, int max)
        => max < min ? min : Math.Clamp(value, min, max);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
