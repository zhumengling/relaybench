using System.Diagnostics;
using System.Runtime.InteropServices;
using RelayBench.Services.Infrastructure;
using RelayBench.WinUI.Desktop;
using Drawing = System.Drawing;

namespace RelayBench.WinUI.Services;

internal sealed class TrayLifecycleService : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint WmUser = 0x0400;
    private const uint WmTrayIcon = WmUser + 41;
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int GwlpWndProc = -4;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NotifyIconVersion4 = 4;
    private const uint NiifInfo = 0x00000001;
    private static readonly IntPtr IdiApplication = new(32512);

    private readonly MainWindow _owner;
    private readonly IntPtr _ownerHwnd;
    private readonly Func<bool> _isTransparentProxyRunning;
    private readonly Func<bool> _isTokenMeterRequested;
    private readonly Func<string> _endpointSummary;
    private readonly Func<string> _tokenSummary;
    private readonly Action<bool> _restoreMainWindow;
    private readonly Action _toggleTransparentProxy;
    private readonly Action _toggleTokenMeter;
    private readonly Func<Task> _exitAsync;
    private readonly WndProcDelegate _wndProcDelegate;
    private IntPtr _oldWndProc;
    private IntPtr _trayIcon;
    private TrayMenuWindow? _trayMenuWindow;
    private bool _hasShownBackgroundHint;
    private bool _isInitialized;

    public TrayLifecycleService(
        MainWindow owner,
        IntPtr ownerHwnd,
        Func<bool> isTransparentProxyRunning,
        Func<bool> isTokenMeterRequested,
        Func<string> endpointSummary,
        Func<string> tokenSummary,
        Action<bool> restoreMainWindow,
        Action toggleTransparentProxy,
        Action toggleTokenMeter,
        Func<Task> exitAsync)
    {
        _owner = owner;
        _ownerHwnd = ownerHwnd;
        _isTransparentProxyRunning = isTransparentProxyRunning;
        _isTokenMeterRequested = isTokenMeterRequested;
        _endpointSummary = endpointSummary;
        _tokenSummary = tokenSummary;
        _restoreMainWindow = restoreMainWindow;
        _toggleTransparentProxy = toggleTransparentProxy;
        _toggleTokenMeter = toggleTokenMeter;
        _exitAsync = exitAsync;
        _wndProcDelegate = WndProc;
    }

    public void Initialize()
    {
        if (_isInitialized || _ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        _oldWndProc = SetWindowLongPtr(
            _ownerHwnd,
            GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        _trayIcon = LoadIcon(IntPtr.Zero, IdiApplication);

        var data = CreateNotifyData(NifMessage | NifIcon | NifTip);
        data.hIcon = _trayIcon;
        data.szTip = BuildTrayTip();
        Shell_NotifyIcon(NimAdd, ref data);

        data.uVersionOrTimeout = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);

        _isInitialized = true;
        UpdateState();
    }

    public void UpdateState()
    {
        if (!_isInitialized)
        {
            return;
        }

        var data = CreateNotifyData(NifTip | NifIcon);
        data.hIcon = _trayIcon;
        data.szTip = BuildTrayTip();
        Shell_NotifyIcon(NimModify, ref data);

        if (_trayMenuWindow is not null)
        {
            ApplyTrayMenuState(_trayMenuWindow);
        }
    }

    public void CloseMenu()
        => _trayMenuWindow?.RequestClose();

    public void ApplyTheme(Microsoft.UI.Xaml.ElementTheme theme)
        => _trayMenuWindow?.ApplyTheme(theme);

    public void ShowBackgroundHint()
    {
        if (_hasShownBackgroundHint || !_isInitialized)
        {
            return;
        }

        _hasShownBackgroundHint = true;
        var data = CreateNotifyData(NifInfo);
        data.szInfoTitle = "RelayBench 正在后台运行";
        data.szInfo = "右键托盘图标可打开控制菜单，双击可恢复主窗口。";
        data.dwInfoFlags = NiifInfo;
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_trayMenuWindow is not null)
        {
            _trayMenuWindow.RequestClose();
            _trayMenuWindow = null;
        }

        if (_isInitialized)
        {
            var data = CreateNotifyData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            _isInitialized = false;
        }

        if (_oldWndProc != IntPtr.Zero && _ownerHwnd != IntPtr.Zero)
        {
            SetWindowLongPtr(_ownerHwnd, GwlpWndProc, _oldWndProc);
            _oldWndProc = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTrayIcon && IsTrayIconCallback(wParam, lParam))
        {
            var notificationCode = GetNotificationCode(lParam);
            if (notificationCode == WmLButtonDblClk)
            {
                _owner.DispatcherQueue.TryEnqueue(() =>
                {
                    CloseMenu();
                    _restoreMainWindow(false);
                });
                return IntPtr.Zero;
            }

            if (notificationCode is WmRButtonUp or WmContextMenu)
            {
                _owner.DispatcherQueue.TryEnqueue(ShowMenuAtCursor);
                return IntPtr.Zero;
            }
        }

        return _oldWndProc == IntPtr.Zero
            ? DefWindowProc(hwnd, msg, wParam, lParam)
            : CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam);
    }

    private void ShowMenuAtCursor()
    {
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        CloseMenu();

        var menu = new TrayMenuWindow();
        _trayMenuWindow = menu;
        menu.OpenMainWindowRequested += (_, _) => _restoreMainWindow(false);
        menu.ToggleTransparentProxyRequested += (_, _) => _toggleTransparentProxy();
        menu.ToggleTokenMeterRequested += (_, _) => _toggleTokenMeter();
        menu.OpenLogDirectoryRequested += (_, _) => OpenRelayBenchLogDirectory();
        menu.ExitRequested += async (_, _) => await _exitAsync();
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayMenuWindow, menu))
            {
                _trayMenuWindow = null;
            }
        };

        ApplyTrayMenuState(menu);
        menu.PreparePlacement(new Drawing.Point(cursor.X, cursor.Y));
        menu.Activate();
    }

    private void ApplyTrayMenuState(TrayMenuWindow menu)
        => menu.ApplyState(
            _isTransparentProxyRunning(),
            _isTokenMeterRequested(),
            _endpointSummary(),
            _tokenSummary());

    private string BuildTrayTip()
        => _isTransparentProxyRunning()
            ? "RelayBench - 透明代理运行中"
            : "RelayBench";

    private static bool IsTrayIconCallback(IntPtr wParam, IntPtr lParam)
    {
        if (wParam.ToInt64() == TrayIconId)
        {
            return true;
        }

        return GetIconId(lParam) == TrayIconId;
    }

    private static int GetNotificationCode(IntPtr lParam)
        => unchecked((short)(lParam.ToInt64() & 0xFFFF));

    private static uint GetIconId(IntPtr lParam)
        => unchecked((uint)((lParam.ToInt64() >> 16) & 0xFFFF));

    private NOTIFYICONDATA CreateNotifyData(uint flags)
        => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _ownerHwnd,
            uID = TrayIconId,
            uFlags = flags,
            uCallbackMessage = WmTrayIcon,
            szTip = "RelayBench",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

    private static void OpenRelayBenchLogDirectory()
    {
        try
        {
            var directory = Path.GetDirectoryName(RelayBenchPaths.StartupLogPath) ?? RelayBenchPaths.RootDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TrayLifecycleService.OpenRelayBenchLogDirectory", ex);
        }
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8)
        {
            return SetWindowLongPtr64(hwnd, index, value);
        }

        return new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr previousWndProc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}
