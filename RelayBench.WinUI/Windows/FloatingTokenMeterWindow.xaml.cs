using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RelayBench.Services.Infrastructure;
using RelayBench.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace RelayBench.WinUI.Desktop;

public sealed partial class FloatingTokenMeterWindow : Window
{
    private const int WindowWidthDips = 196;
    private const int WindowHeightDips = 68;
    private const int EdgePaddingDips = 14;
    private const int DefaultOffsetRightDips = 28;
    private const int DefaultOffsetTopDips = 126;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdtEffectiveDpi = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopMost = new(-1);

    private readonly DispatcherTimer _clockTimer = new();
    private readonly IntPtr _hwnd;
    private readonly AppWindow? _appWindow;
    private bool _isPositionLocked;
    private bool _isMousePassThrough;
    private bool _isDragging;
    private bool _isPointerOverChrome;
    private POINT _dragStartCursor;
    private RECT _dragStartWindow;

    public FloatingTokenMeterWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = TryGetAppWindow(_hwnd);
        ConfigureChrome();

        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => ViewModel.RefreshTokenMeterIdleState();
        Closed += OnClosed;
    }

    public TransparentProxyViewModel ViewModel => App.TransparentProxyViewModel;

    public event EventHandler? OpenMainWindowRequested;

    public event EventHandler? HideRequested;

    public event EventHandler? ResetRequested;

    public event EventHandler? PlacementChanged;

    public event EventHandler? SettingsChanged;

    public void ApplyTheme(ElementTheme theme)
        => RootSurface.RequestedTheme = theme;

    public void ApplyState(FloatingTokenMeterWindowState state)
    {
        SetPositionLocked(state.IsPositionLocked, notify: false);

        var scale = GetDpiScaleForWindow();
        var windowWidthPixels = DipsToPixels(WindowWidthDips, scale);
        var windowHeightPixels = DipsToPixels(WindowHeightDips, scale);
        var edgePaddingPixels = DipsToPixels(EdgePaddingDips, scale);
        var workArea = GetCurrentWorkArea();
        var left = state.Left ?? workArea.Right - windowWidthPixels - DipsToPixels(DefaultOffsetRightDips, scale);
        var top = state.Top ?? workArea.Top + DipsToPixels(DefaultOffsetTopDips, scale);
        MoveWindow(
            Clamp(left, workArea.Left + edgePaddingPixels, workArea.Right - windowWidthPixels - edgePaddingPixels),
            Clamp(top, workArea.Top + edgePaddingPixels, workArea.Bottom - windowHeightPixels - edgePaddingPixels));
    }

    public void CapturePlacementInto(FloatingTokenMeterWindowState state)
    {
        if (GetWindowRect(_hwnd, out var rect))
        {
            state.Left = rect.Left;
            state.Top = rect.Top;
        }

        state.IsPositionLocked = _isPositionLocked;
    }

    public void SetMousePassThrough(bool isEnabled)
    {
        _isMousePassThrough = isEnabled;
        MousePassThroughMenuItem.IsChecked = isEnabled;
        SetWindowTransparent(isEnabled);
    }

    public void SetPositionLocked(bool isLocked)
        => SetPositionLocked(isLocked, notify: true);

    private static AppWindow? TryGetAppWindow(IntPtr hwnd)
    {
        try
        {
            return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("FloatingTokenMeterWindow.TryGetAppWindow", ex);
            return null;
        }
    }

    private void ConfigureChrome()
    {
        var scale = GetDpiScaleForWindow();
        _appWindow?.Resize(new SizeInt32(
            DipsToPixels(WindowWidthDips, scale),
            DipsToPixels(WindowHeightDips, scale)));

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

    private void RootChrome_Loaded(object sender, RoutedEventArgs e)
    {
        RootChrome.Opacity = 1d;
        RootTransform.ScaleX = 1d;
        RootTransform.ScaleY = 1d;
        RootTransform.TranslateY = 0d;
        UpdateInteractionOverlay(isPressed: false);
        _clockTimer.Start();
    }

    private void RootChrome_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void RootChrome_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA)
        {
            OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (e.Key is VirtualKey.Application)
        {
            RootChrome.ContextFlyout?.ShowAt(RootChrome);
            e.Handled = true;
        }
    }

    private void RootChrome_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootChrome);
        if (!point.Properties.IsLeftButtonPressed || _isPositionLocked || _isMousePassThrough)
        {
            return;
        }

        if (!GetCursorPos(out _dragStartCursor) || !GetWindowRect(_hwnd, out _dragStartWindow))
        {
            return;
        }

        _isDragging = true;
        UpdateInteractionOverlay(isPressed: true);
        RootChrome.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void RootChrome_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverChrome = true;
        UpdateInteractionOverlay(isPressed: _isDragging);
    }

    private void RootChrome_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverChrome = false;
        if (!_isDragging)
        {
            UpdateInteractionOverlay(isPressed: false);
        }
    }

    private void RootChrome_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || !GetCursorPos(out var current))
        {
            return;
        }

        var nextLeft = _dragStartWindow.Left + current.X - _dragStartCursor.X;
        var nextTop = _dragStartWindow.Top + current.Y - _dragStartCursor.Y;
        MoveWindow(nextLeft, nextTop);
        e.Handled = true;
    }

    private void RootChrome_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompleteDrag(e);
    }

    private void RootChrome_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        CompleteDrag(e);
    }

    private void CompleteDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        RootChrome.ReleasePointerCapture(e.Pointer);
        SnapToNearestEdge();
        UpdateInteractionOverlay(isPressed: false);
        PlacementChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void SnapToNearestEdge()
    {
        if (!GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var scale = GetDpiScaleForWindow();
        var windowWidthPixels = DipsToPixels(WindowWidthDips, scale);
        var windowHeightPixels = DipsToPixels(WindowHeightDips, scale);
        var edgePaddingPixels = DipsToPixels(EdgePaddingDips, scale);
        var workArea = GetCurrentWorkArea();
        var centerX = rect.Left + windowWidthPixels / 2;
        var targetLeft = centerX < workArea.Left + (workArea.Right - workArea.Left) / 2
            ? workArea.Left + edgePaddingPixels
            : workArea.Right - windowWidthPixels - edgePaddingPixels;
        var targetTop = Clamp(
            rect.Top,
            workArea.Top + edgePaddingPixels,
            workArea.Bottom - windowHeightPixels - edgePaddingPixels);
        MoveWindow(targetLeft, targetTop);
    }

    private RECT GetCurrentWorkArea()
    {
        var monitor = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
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

    private void SetPositionLocked(bool isLocked, bool notify)
    {
        _isPositionLocked = isLocked;
        LockPositionMenuItem.IsChecked = isLocked;
        if (notify)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetWindowTransparent(bool isEnabled)
    {
        var exStyle = GetWindowLong(_hwnd, GwlExStyle);
        exStyle = isEnabled
            ? exStyle | WsExTransparent
            : exStyle & ~WsExTransparent;
        SetWindowLong(_hwnd, GwlExStyle, exStyle);

        if (isEnabled)
        {
            _isPointerOverChrome = false;
            UpdateInteractionOverlay(isPressed: false);
        }
    }

    private void UpdateInteractionOverlay(bool isPressed)
    {
        InteractionOverlay.Opacity = !isPressed && _isPointerOverChrome && !_isMousePassThrough
                ? 0.42d
                : 0d;
        PressedInteractionOverlay.Opacity = isPressed && !_isMousePassThrough
            ? 0.64d
            : 0d;
    }

    private void OpenMainWindowMenuItem_Click(object sender, RoutedEventArgs e)
        => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);

    private void LockPositionMenuItem_Click(object sender, RoutedEventArgs e)
        => SetPositionLocked(LockPositionMenuItem.IsChecked);

    private void MousePassThroughMenuItem_Click(object sender, RoutedEventArgs e)
        => SetMousePassThrough(MousePassThroughMenuItem.IsChecked);

    private void ResetCounterMenuItem_Click(object sender, RoutedEventArgs e)
        => ResetRequested?.Invoke(this, EventArgs.Empty);

    private void CopySummaryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ViewModel.TokenMeterMetricsSummary);
        Clipboard.SetContent(package);
        ViewModel.StatusText = "Token 计摘要已复制";
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
        => HideRequested?.Invoke(this, EventArgs.Empty);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _clockTimer.Stop();
    }

    private static int Clamp(int value, int min, int max)
        => max < min ? min : Math.Clamp(value, min, max);

    private double GetDpiScaleForWindow()
    {
        var dpi = GetDpiForWindow(_hwnd);
        if (dpi >= 96)
        {
            return dpi / 96.0;
        }

        var monitor = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
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

        return 1.0;
    }

    private static int DipsToPixels(double dips, double scale)
        => Math.Max(1, (int)Math.Ceiling(dips * Math.Max(1.0, scale)));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

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
