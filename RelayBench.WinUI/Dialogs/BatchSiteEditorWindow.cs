using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.ViewModels;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace RelayBench.WinUI.Dialogs;

public sealed class BatchSiteEditorWindow : Window
{
    private const int PreferredWidthDips = 1260;
    private const int PreferredHeightDips = 820;
    private const int MinimumWidthDips = 720;
    private const int MinimumHeightDips = 560;
    private const int OwnerMarginDips = 72;
    private const int GwlHwndParent = -8;

    private readonly BatchComparisonViewModel _viewModel;
    private readonly BatchSiteEditorDialog _content;
    private readonly IntPtr _hwnd;
    private bool _dialogPresenterConfigured;

    public BatchSiteEditorWindow(BatchComparisonViewModel viewModel)
    {
        _viewModel = viewModel;
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "\u5165\u53e3\u7ec4\u7ef4\u62a4";
        ExtendsContentIntoTitleBar = false;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        _content = new BatchSiteEditorDialog(viewModel);
        ApplyTheme(ThemeService.GetCurrentTheme());
        _content.CloseRequested += OnCloseRequested;
        Content = _content;
        _viewModel.ProxyBatchEditorCloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    public void ApplyTheme(ElementTheme theme)
        => _content.RequestedTheme = theme;

    public void ShowCentered()
    {
        CenterOverMainWindow();
        Activate();
        _ = ShowWindow(_hwnd, SwShow);
        _ = SetForegroundWindow(_hwnd);
    }

    public void CenterOverMainWindow()
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var ownerHwnd = App.MainWindow is null
            ? IntPtr.Zero
            : WindowNative.GetWindowHandle(App.MainWindow);

        var scale = GetDpiScale(ownerHwnd != IntPtr.Zero ? ownerHwnd : hwnd);
        var preferredWidth = DipsToPixels(PreferredWidthDips, scale);
        var preferredHeight = DipsToPixels(PreferredHeightDips, scale);
        var minWidth = DipsToPixels(MinimumWidthDips, scale);
        var minHeight = DipsToPixels(MinimumHeightDips, scale);

        var ownerRect = TryGetWindowRect(ownerHwnd, out var rect)
            ? rect
            : new NativeRect(0, 0, DipsToPixels(1440, scale), DipsToPixels(900, scale));

        var ownerWidth = Math.Max(1, ownerRect.Right - ownerRect.Left);
        var ownerHeight = Math.Max(1, ownerRect.Bottom - ownerRect.Top);
        var ownerMargin = DipsToPixels(OwnerMarginDips, scale);
        var width = Math.Clamp(Math.Min(preferredWidth, ownerWidth - ownerMargin), minWidth, preferredWidth);
        var height = Math.Clamp(Math.Min(preferredHeight, ownerHeight - ownerMargin), minHeight, preferredHeight);

        var x = ownerRect.Left + Math.Max(0, (ownerWidth - width) / 2);
        var y = ownerRect.Top + Math.Max(0, (ownerHeight - height) / 2);

        ConfigureOwnedDialog(hwnd, ownerHwnd, minWidth, minHeight);
        _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpNoZOrder);
    }

    private void ConfigureOwnedDialog(IntPtr hwnd, IntPtr ownerHwnd, int minWidth, int minHeight)
    {
        var appWindow = TryGetAppWindow(hwnd);
        if (appWindow is not null)
        {
            appWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

            if (!_dialogPresenterConfigured)
            {
                var presenter = OverlappedPresenter.CreateForDialog();
                presenter.IsModal = false;
                presenter.IsResizable = true;
                presenter.PreferredMinimumWidth = minWidth;
                presenter.PreferredMinimumHeight = minHeight;
                appWindow.SetPresenter(presenter);
                _dialogPresenterConfigured = true;
            }
            else if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsModal = false;
                presenter.PreferredMinimumWidth = minWidth;
                presenter.PreferredMinimumHeight = minHeight;
            }
        }

        if (ownerHwnd != IntPtr.Zero)
        {
            SetWindowOwner(hwnd, ownerHwnd);
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _viewModel.ProxyBatchEditorCloseRequested -= OnCloseRequested;
    }

    private static bool TryGetWindowRect(IntPtr hwnd, out NativeRect rect)
    {
        rect = default;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
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

    private static void SetWindowOwner(IntPtr hwnd, IntPtr ownerHwnd)
        => _ = SetWindowLongPtr(hwnd, GwlHwndParent, ownerHwnd);

    private static double GetDpiScale(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd);
        return Math.Max(1d, dpi / 96d);
    }

    private static int DipsToPixels(double dips, double scale)
        => Math.Max(1, (int)Math.Round(dips * scale, MidpointRounding.AwayFromZero));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8)
        {
            return SetWindowLongPtr64(hwnd, index, value);
        }

        return new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    }

    private const uint SwpNoZOrder = 0x0004;
    private const int SwShow = 5;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left { get; }
        public int Top { get; }
        public int Right { get; }
        public int Bottom { get; }
    }
}
