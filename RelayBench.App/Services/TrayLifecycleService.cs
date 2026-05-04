using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using RelayBench.App.Infrastructure;
using RelayBench.App.Views;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RelayBench.App.Services;

internal sealed class TrayLifecycleService : IDisposable
{
    private readonly Window _owner;
    private readonly Func<bool> _isTransparentProxyRunning;
    private readonly Func<bool> _isTokenMeterVisible;
    private readonly Action<bool> _restoreMainWindow;
    private readonly Action _toggleTransparentProxy;
    private readonly Action _toggleTokenMeter;
    private readonly Func<Task> _exitAsync;
    private Forms.NotifyIcon? _notifyIcon;
    private TrayMenuWindow? _trayMenuWindow;
    private bool _hasShownBackgroundHint;

    public TrayLifecycleService(
        Window owner,
        Func<bool> isTransparentProxyRunning,
        Func<bool> isTokenMeterVisible,
        Action<bool> restoreMainWindow,
        Action toggleTransparentProxy,
        Action toggleTokenMeter,
        Func<Task> exitAsync)
    {
        _owner = owner;
        _isTransparentProxyRunning = isTransparentProxyRunning;
        _isTokenMeterVisible = isTokenMeterVisible;
        _restoreMainWindow = restoreMainWindow;
        _toggleTransparentProxy = toggleTransparentProxy;
        _toggleTokenMeter = toggleTokenMeter;
        _exitAsync = exitAsync;
    }

    public void Initialize()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "RelayBench",
            Icon = CreateTrayIcon(),
            Visible = true,
            ContextMenuStrip = null
        };
        _notifyIcon.MouseDoubleClick += NotifyIcon_OnMouseDoubleClick;
        _notifyIcon.MouseUp += NotifyIcon_OnMouseUp;
        UpdateState();
    }

    public void UpdateState()
    {
        var isProxyRunning = _isTransparentProxyRunning();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = isProxyRunning
                ? "RelayBench - \u900f\u660e\u4ee3\u7406\u8fd0\u884c\u4e2d"
                : "RelayBench";
        }

        if (_trayMenuWindow is { IsVisible: true } trayMenuWindow)
        {
            ApplyTrayMenuState(trayMenuWindow);
        }
    }

    public void CloseMenu()
        => _trayMenuWindow?.RequestClose();

    public void ShowBackgroundHint()
    {
        if (_hasShownBackgroundHint || _notifyIcon is null)
        {
            return;
        }

        _hasShownBackgroundHint = true;
        _notifyIcon.ShowBalloonTip(
            2600,
            "RelayBench \u6b63\u5728\u540e\u53f0\u8fd0\u884c",
            "\u53f3\u952e\u6258\u76d8\u56fe\u6807\u53ef\u9000\u51fa\uff0c\u53cc\u51fb\u53ef\u6062\u590d\u4e3b\u7a97\u53e3\u3002",
            Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_trayMenuWindow is not null)
        {
            _trayMenuWindow.Close();
            _trayMenuWindow = null;
        }

        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }

    private void NotifyIcon_OnMouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        CloseMenu();
        _restoreMainWindow(false);
    }

    private void NotifyIcon_OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Right)
        {
            ShowMenu();
        }
    }

    private void ShowMenu()
    {
        if (!_owner.Dispatcher.CheckAccess())
        {
            _owner.Dispatcher.Invoke(ShowMenu);
            return;
        }

        var cursorPositionPixels = Forms.Cursor.Position;
        var transform = GetDeviceToDipTransform();
        var cursorPosition = transform.Transform(new Point(cursorPositionPixels.X, cursorPositionPixels.Y));
        var workArea = GetWorkAreaInDip(cursorPositionPixels, transform);

        if (_trayMenuWindow is { IsVisible: true } visibleMenu)
        {
            ApplyTrayMenuState(visibleMenu);
            visibleMenu.PreparePlacement(cursorPosition, workArea);
            visibleMenu.Activate();
            return;
        }

        TrayMenuWindow menu = new();
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
        menu.PreparePlacement(cursorPosition, workArea);
        menu.Show();
        menu.Activate();
    }

    private void ApplyTrayMenuState(TrayMenuWindow menu)
        => menu.ApplyState(_isTransparentProxyRunning(), _isTokenMeterVisible());

    private Matrix GetDeviceToDipTransform()
    {
        try
        {
            var source = PresentationSource.FromVisual(_owner);
            if (source?.CompositionTarget is not null)
            {
                return source.CompositionTarget.TransformFromDevice;
            }
        }
        catch
        {
        }

        return Matrix.Identity;
    }

    private static Rect GetWorkAreaInDip(Drawing.Point cursorPositionPixels, Matrix transformFromDevice)
    {
        var workingArea = Forms.Screen.FromPoint(cursorPositionPixels).WorkingArea;
        var topLeft = transformFromDevice.Transform(new Point(workingArea.Left, workingArea.Top));
        var bottomRight = transformFromDevice.Transform(new Point(workingArea.Right, workingArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static Drawing.Icon CreateTrayIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                return Drawing.Icon.ExtractAssociatedIcon(processPath) ?? Drawing.SystemIcons.Application;
            }
        }
        catch
        {
        }

        return Drawing.SystemIcons.Application;
    }

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
}
