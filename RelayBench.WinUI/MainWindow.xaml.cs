using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RelayBench.Services.Infrastructure;
using RelayBench.WinUI.Dialogs;
using RelayBench.WinUI.Pages;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using RelayBench.WinUI.ViewModels;
using RelayBench.WinUI.Desktop;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace RelayBench.WinUI;

public sealed partial class MainWindow : Window
{
    private const int StartupWindowWidthDips = 1640;
    private const int StartupWindowHeightDips = 920;
    private const int MinimumStartupWindowWidthDips = 1180;
    private const int MinimumStartupWindowHeightDips = 720;
    private const int StartupWindowEdgePaddingDips = 24;
    private const double CompactChromeWidthThreshold = 1280d;
    private const double VeryCompactChromeWidthThreshold = 720d;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdtEffectiveDpi = 0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public ViewModels.ShellViewModel Shell => App.ShellViewModel;

    private readonly CommandPaletteAggregator _paletteAggregator;
    private readonly IntPtr _hwnd;
    private readonly AppWindow? _appWindow;
    private readonly FloatingTokenMeterWindowState _tokenMeterWindowState;
    private FloatingTokenMeterWindow? _floatingTokenMeterWindow;
    private TrayLifecycleService? _trayLifecycleService;
    private AboutDialog? _aboutDialog;
    private bool _isTokenMeterRequested;
    private bool _isExitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = TryGetAppWindow(_hwnd);
        if (_appWindow is not null)
        {
            _appWindow.Closing += MainAppWindow_Closing;
            _appWindow.Changed += MainAppWindow_Changed;
        }
        ConfigureInitialWindowBounds();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            ApplyWindowChromeTheme();
            ApplyChildWindowThemes();
        };
        ApplyWindowChromeTheme();
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootGrid_KeyDown), true);
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        RootGrid.Loaded += (_, _) => ApplyResponsiveChrome(RootGrid.ActualWidth);

        // Phase 21: Restore last visited page from persisted state
        var lastPage = App.Settings.Current.LastVisitedPage;
        var startPage = ResolvePageType(lastPage);
        ContentFrame.Navigate(startPage);
        SelectNavigationItem(startPage);

        Activated += MainWindow_Activated;

        // Build command palette sources
        var navigationSource = new PageNavigationSource(pageType =>
        {
            NavigateToPage(pageType);
        });

        var commandsSource = new CommandsSource(new CommandPaletteItem[]
        {
            new("切换代理", "命令", () => _ = App.TransparentProxyViewModel.ToggleProxyAsync()),
            new("打开设置", "命令", () => NavigateToPage(typeof(SettingsPage))),
        });

        _paletteAggregator = new CommandPaletteAggregator(new ICommandPaletteSource[] { navigationSource, commandsSource });
        App.TransparentProxyViewModel.PropertyChanged += OnTransparentProxyPropertyChanged;
        App.TransparentProxyViewModel.ModelPool.CollectionChanged += (_, _) => RefreshShellProxyStatus();
        _tokenMeterWindowState = FloatingTokenMeterWindowState.Load();
        _isTokenMeterRequested =
            !_tokenMeterWindowState.HasVisibilityPreference ||
            _tokenMeterWindowState.WasRequested;
        InitializeTrayLifecycle();
        Shell.AboutDialogOpenRequested += OnShellAboutDialogOpenRequested;
        Shell.AboutDialogCloseRequested += OnShellAboutDialogCloseRequested;
        Shell.ProjectHomepageOpenRequested += OnShellProjectHomepageOpenRequested;
        Shell.NavigationRailToggleRequested += OnShellNavigationRailToggleRequested;
        Closed += MainWindow_Closed;
        RefreshShellProxyStatus();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs args)
        => ApplyResponsiveChrome(args.NewSize.Width);

    private void MainAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        ApplyResponsiveChrome(PixelsToDips(sender.Size.Width, GetDpiScaleForWindow(_hwnd)));
    }

    private void ApplyResponsiveChrome(double width)
    {
        var isCompact = width > 0 && width < CompactChromeWidthThreshold;
        var isVeryCompact = width > 0 && width < VeryCompactChromeWidthThreshold;

        AppTitleBar.Padding = isCompact ? new Thickness(8, 0, 8, 0) : new Thickness(14, 0, 14, 0);
        CommandPaletteBox.MinWidth = isCompact ? 0 : 220;
        CommandPaletteBox.MaxWidth = isVeryCompact ? 180 : isCompact ? 320 : 520;
        CommandPaletteBox.Visibility = isVeryCompact ? Visibility.Collapsed : Visibility.Visible;
        EnvironmentStatusPill.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;

        NavView.IsPaneToggleButtonVisible = !isVeryCompact;
        NavView.OpenPaneLength = isVeryCompact ? 48 : 200;
        NavView.PaneDisplayMode = isCompact
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;

        if (isCompact)
        {
            NavView.IsPaneOpen = false;
        }
        else
        {
            NavView.IsPaneOpen = true;
        }

        StatusAssistantSummary.Visibility = isVeryCompact ? Visibility.Collapsed : Visibility.Visible;
        StatusThroughputSummary.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        StatusProxyAddress.Visibility = isVeryCompact ? Visibility.Collapsed : Visibility.Visible;
        StatusModeSummary.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        StatusSystemProxy.Visibility = Visibility.Visible;
        StatusUptime.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        StatusVersion.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        StatusUpdateButton.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        App.ShellViewModel.StartUptimeTimer();
        SyncFloatingTokenMeterWithProxyState();
    }

    internal ElementTheme CurrentThemeForChildWindows
        => ResolveEffectiveTheme();

    internal void ApplyTheme(ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;
        ApplyWindowChromeTheme();
        ApplyChildWindowThemes();
    }

    private void ApplyWindowChromeTheme()
    {
        if (_appWindow is null)
        {
            return;
        }

        var isDark = ResolveEffectiveTheme() == ElementTheme.Dark;
        var titleBar = _appWindow.TitleBar;

        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ResolveTitleBarBrushColor("AppChromePrimaryTextBrush", isDark ? Colors.White : Colors.Black);
        titleBar.ButtonInactiveForegroundColor = ResolveTitleBarBrushColor("AppChromeSecondaryTextBrush", isDark ? Colors.LightGray : Colors.Gray);
        titleBar.ButtonHoverBackgroundColor = ResolveTitleBarBrushColor("AppChromeIconButtonBackgroundPointerOverBrush", Colors.Transparent);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonPressedBackgroundColor = ResolveTitleBarBrushColor("AppChromeIconButtonBackgroundPressedBrush", Colors.Transparent);
        titleBar.ButtonPressedForegroundColor = titleBar.ButtonForegroundColor;
    }

    private static Color ResolveTitleBarBrushColor(string resourceKey, Color fallback)
        => Application.Current.Resources.TryGetValue(resourceKey, out var value) &&
           value is SolidColorBrush brush
            ? brush.Color
            : fallback;

    private ElementTheme ResolveEffectiveTheme()
    {
        var requestedTheme = RootGrid.RequestedTheme;
        if (requestedTheme is ElementTheme.Light or ElementTheme.Dark)
        {
            return requestedTheme;
        }

        return RootGrid.ActualTheme;
    }

    private void ApplyChildWindowThemes()
    {
        var theme = CurrentThemeForChildWindows;
        _trayLifecycleService?.ApplyTheme(theme);
        _floatingTokenMeterWindow?.ApplyTheme(theme);
    }

    private void OnTransparentProxyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransparentProxyViewModel.IsRunning)
            or nameof(TransparentProxyViewModel.ListenAddress)
            or nameof(TransparentProxyViewModel.P50Latency)
            or nameof(TransparentProxyViewModel.TokenSpeed)
            or nameof(TransparentProxyViewModel.ActiveConnections))
        {
            RefreshShellProxyStatus();
        }

        if (e.PropertyName is nameof(TransparentProxyViewModel.IsRunning))
        {
            DispatcherQueue.TryEnqueue(SyncFloatingTokenMeterWithProxyState);
        }
    }

    private void RefreshShellProxyStatus()
    {
        var proxy = App.TransparentProxyViewModel;
        Shell.StatusBarProxyAddress = proxy.IsRunning ? proxy.LocalEndpoint : "--";
        Shell.StatusBarMode = proxy.IsRunning
            ? "\u900f\u660e\u4ee3\u7406 (HTTP & HTTPS)"
            : "\u672a\u8fde\u63a5";
        Shell.ApplyProxyRuntimeStatus(proxy.IsRunning);
        Shell.StatusBarAssistantSummary = $"模型池 {proxy.ModelPool.Count} | {proxy.P50Latency}";
        Shell.StatusBarThroughputSummary = $"TTFT {proxy.P50Latency} | {proxy.TokenSpeed} | \u8fde\u63a5 {proxy.ActiveConnections}";
        _trayLifecycleService?.UpdateState();
    }

    private void CommandPaletteBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var results = _paletteAggregator.Query(sender.Text);
            sender.ItemsSource = results;
        }
    }

    private void CommandPaletteBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is CommandPaletteItem item)
        {
            item.Invoke();
            sender.Text = string.Empty;
            sender.ItemsSource = null;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.K || !IsControlKeyDown())
        {
            return;
        }

        FocusCommandPalette();
        args.Handled = true;
    }

    private void FlyoutButton_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not Button button ||
            args.Key is not (VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA or VirtualKey.Application))
        {
            return;
        }

        button.Flyout?.ShowAt(button);
        args.Handled = true;
    }

    private void FocusCommandPalette()
    {
        CommandPaletteBox.Focus(FocusState.Programmatic);
    }

    private static bool IsControlKeyDown()
        => (GetKeyState((int)VirtualKey.Control) & 0x8000) != 0;

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

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
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public uint CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private async void OnShellAboutDialogOpenRequested(object? sender, EventArgs e)
        => await ShowAboutDialogAsync();

    private void OnShellAboutDialogCloseRequested(object? sender, EventArgs e)
        => _aboutDialog?.Hide();

    private void OnShellProjectHomepageOpenRequested(object? sender, EventArgs e)
        => OpenProjectHomepage();

    private void OnShellNavigationRailToggleRequested(object? sender, EventArgs e)
    {
        if (RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < VeryCompactChromeWidthThreshold)
        {
            NavView.IsPaneOpen = false;
            return;
        }

        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private async Task ShowAboutDialogAsync()
    {
        if (_aboutDialog is not null)
        {
            return;
        }

        var dialog = new AboutDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = CurrentThemeForChildWindows
        };
        _aboutDialog = dialog;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_aboutDialog, dialog))
            {
                _aboutDialog = null;
            }
        }
    }

    private static void OpenProjectHomepage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ShellViewModel.ProjectHomepageUrl,
                UseShellExecute = true
            });
            App.TransparentProxyViewModel.StatusText = "产品主页已打开";
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("MainWindow.OpenProjectHomepage", ex);
            App.TransparentProxyViewModel.StatusText = $"打开产品主页失败：{ex.Message}";
        }
    }

    private void CopyRuntimeSummaryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(BuildRuntimeSummary());
        Clipboard.SetContent(package);
        App.TransparentProxyViewModel.StatusText = "运行摘要已复制";
    }

    private void OpenDataDirectoryMenuItem_Click(object sender, RoutedEventArgs e)
        => OpenDirectory(RelayBenchPaths.DataDirectory);

    private void OpenConfigDirectoryMenuItem_Click(object sender, RoutedEventArgs e)
        => OpenDirectory(RelayBenchPaths.ConfigDirectory);

    private void OpenRootDirectoryMenuItem_Click(object sender, RoutedEventArgs e)
        => OpenDirectory(RelayBenchPaths.RootDirectory);

    private static void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("MainWindow.OpenDirectory", ex);
        }
    }

    private static string BuildRuntimeSummary()
        => string.Join(
            Environment.NewLine,
            "RelayBench 运行摘要",
            $"版本: {App.ShellViewModel.StatusBarVersion}",
            $"运行时: {RuntimeInformation.FrameworkDescription} / {RuntimeInformation.ProcessArchitecture}",
            $"项目根目录: {RelayBenchPaths.RootDirectory}",
            $"数据目录: {RelayBenchPaths.DataDirectory}",
            $"配置目录: {RelayBenchPaths.ConfigDirectory}",
            $"透明代理: {(App.TransparentProxyViewModel.IsRunning ? App.TransparentProxyViewModel.LocalEndpoint : "已停止")}",
            $"Token 计: {App.TransparentProxyViewModel.TokenMeterModeText} {App.TransparentProxyViewModel.TokenMeterPrimaryText}");

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

    private void ConfigureInitialWindowBounds()
    {
        if (_hwnd == IntPtr.Zero || _appWindow is null)
        {
            return;
        }

        var scale = GetDpiScaleForWindow(_hwnd);
        var workArea = GetMonitorWorkArea(_hwnd);
        var edgePadding = DipsToPixels(StartupWindowEdgePaddingDips, scale);
        var workWidth = Math.Max(1, workArea.Right - workArea.Left);
        var workHeight = Math.Max(1, workArea.Bottom - workArea.Top);
        var availableWidth = Math.Max(1, workWidth - edgePadding * 2);
        var availableHeight = Math.Max(1, workHeight - edgePadding * 2);
        var minimumWidth = Math.Min(DipsToPixels(MinimumStartupWindowWidthDips, scale), availableWidth);
        var minimumHeight = Math.Min(DipsToPixels(MinimumStartupWindowHeightDips, scale), availableHeight);
        var width = Math.Max(minimumWidth, Math.Min(DipsToPixels(StartupWindowWidthDips, scale), availableWidth));
        var height = Math.Max(minimumHeight, Math.Min(DipsToPixels(StartupWindowHeightDips, scale), availableHeight));
        var left = workArea.Left + Math.Max(0, (workWidth - width) / 2);
        var top = workArea.Top + Math.Max(0, (workHeight - height) / 2);

        SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate);
    }

    private static NativeRect GetMonitorWorkArea(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo
        {
            CbSize = (uint)Marshal.SizeOf<MonitorInfo>()
        };

        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return info.Work;
        }

        return new NativeRect(0, 0, StartupWindowWidthDips, StartupWindowHeightDips);
    }

    private static double GetDpiScaleForWindow(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi >= 96)
        {
            return Math.Max(1d, dpi / 96d);
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero &&
            GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return Math.Max(1d, dpiX / 96d);
        }

        return 1d;
    }

    private static int DipsToPixels(int dips, double scale)
        => Math.Max(1, (int)Math.Round(dips * scale));

    private static double PixelsToDips(int pixels, double scale)
        => Math.Max(1d, pixels / Math.Max(1d, scale));

    private void InitializeTrayLifecycle()
    {
        _trayLifecycleService ??= new TrayLifecycleService(
            this,
            _hwnd,
            () => App.TransparentProxyViewModel.IsRunning,
            () => _isTokenMeterRequested || _floatingTokenMeterWindow is not null,
            () => App.TransparentProxyViewModel.IsRunning
                ? App.TransparentProxyViewModel.LocalEndpoint
                : "后台就绪",
            () => $"{App.TransparentProxyViewModel.TokenMeterModeText} · {App.TransparentProxyViewModel.TokenMeterPrimaryText}",
            RestoreMainWindow,
            () => _ = ToggleTransparentProxyFromTrayAsync(),
            ToggleFloatingTokenMeterFromTray,
            ExitFromTrayAsync);
        _trayLifecycleService.Initialize();
    }

    private void MainAppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExitRequested)
        {
            return;
        }

        args.Cancel = true;
        HideMainWindowToTray();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Shell.AboutDialogOpenRequested -= OnShellAboutDialogOpenRequested;
        Shell.AboutDialogCloseRequested -= OnShellAboutDialogCloseRequested;
        Shell.ProjectHomepageOpenRequested -= OnShellProjectHomepageOpenRequested;
        Shell.NavigationRailToggleRequested -= OnShellNavigationRailToggleRequested;
        _trayLifecycleService?.Dispose();
        _trayLifecycleService = null;
        _floatingTokenMeterWindow?.Close();
        _floatingTokenMeterWindow = null;
    }

    private void HideMainWindowToTray()
    {
        _trayLifecycleService?.CloseMenu();
        ShowWindow(_hwnd, SwHide);
        _trayLifecycleService?.ShowBackgroundHint();
    }

    internal void RestoreFromExternalActivation()
    {
        _trayLifecycleService?.CloseMenu();
        RestoreMainWindow(showTransparentProxy: false);
    }

    private void RestoreMainWindow(bool showTransparentProxy)
    {
        ShowWindow(_hwnd, SwRestore);
        ShowWindow(_hwnd, SwShow);
        Activate();
        SetForegroundWindow(_hwnd);

        if (showTransparentProxy)
        {
            NavigateToPage(typeof(TransparentProxyPage));
        }
    }

    private async Task ToggleTransparentProxyFromTrayAsync()
    {
        await App.TransparentProxyViewModel.ToggleProxyAsync();
        _trayLifecycleService?.UpdateState();
    }

    private void ToggleFloatingTokenMeterFromTray()
    {
        if (_isTokenMeterRequested || _floatingTokenMeterWindow is not null)
        {
            HideFloatingTokenMeterFromUi();
        }
        else
        {
            ShowFloatingTokenMeterFromUi();
        }

        _trayLifecycleService?.UpdateState();
    }

    private async Task ExitFromTrayAsync()
    {
        _isExitRequested = true;
        _trayLifecycleService?.Dispose();
        _trayLifecycleService = null;

        if (App.TransparentProxyViewModel.IsRunning)
        {
            await App.TransparentProxyViewModel.ToggleProxyAsync();
        }

        _floatingTokenMeterWindow?.Close();
        _floatingTokenMeterWindow = null;
        Close();
    }

    private void ShowTokenMeterMenuItem_Click(object sender, RoutedEventArgs e)
        => ShowFloatingTokenMeterFromUi();

    private void HideTokenMeterMenuItem_Click(object sender, RoutedEventArgs e)
        => HideFloatingTokenMeterFromUi();

    private void ResetTokenMeterMenuItem_Click(object sender, RoutedEventArgs e)
        => App.TransparentProxyViewModel.ResetTokenMeterCommand.Execute(null);

    private void CopyTokenMeterSummaryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(App.TransparentProxyViewModel.TokenMeterMetricsSummary);
        Clipboard.SetContent(package);
        App.TransparentProxyViewModel.StatusText = "Token 计摘要已复制";
    }

    private void SyncFloatingTokenMeterWithProxyState()
    {
        if (App.TransparentProxyViewModel.IsRunning)
        {
            if (_isTokenMeterRequested)
            {
                ShowFloatingTokenMeter();
            }

            return;
        }

        HideFloatingTokenMeter(closeOnly: true);
    }

    private void ShowFloatingTokenMeterFromUi()
    {
        _isTokenMeterRequested = true;
        if (App.TransparentProxyViewModel.IsRunning)
        {
            ShowFloatingTokenMeter();
        }
        else
        {
            App.TransparentProxyViewModel.StatusText = "透明代理启动后会显示 Token 计";
        }

        CaptureAndSaveTokenMeterWindowState();
    }

    private void HideFloatingTokenMeterFromUi()
    {
        _isTokenMeterRequested = false;
        HideFloatingTokenMeter(closeOnly: false);
        CaptureAndSaveTokenMeterWindowState();
    }

    private void ShowFloatingTokenMeter()
    {
        if (!App.TransparentProxyViewModel.IsRunning)
        {
            return;
        }

        if (_floatingTokenMeterWindow is null)
        {
            var window = new FloatingTokenMeterWindow();
            _floatingTokenMeterWindow = window;
            window.ApplyTheme(CurrentThemeForChildWindows);
            window.ApplyState(_tokenMeterWindowState);
            window.OpenMainWindowRequested += (_, _) =>
            {
                RestoreMainWindow(showTransparentProxy: true);
            };
            window.ResetRequested += (_, _) => App.TransparentProxyViewModel.ResetTokenMeterCommand.Execute(null);
            window.PlacementChanged += (_, _) => CaptureAndSaveTokenMeterWindowState();
            window.SettingsChanged += (_, _) => CaptureAndSaveTokenMeterWindowState();
            window.HideRequested += (_, _) => HideFloatingTokenMeterFromUi();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_floatingTokenMeterWindow, window))
                {
                    CaptureAndSaveTokenMeterWindowState();
                    _floatingTokenMeterWindow = null;
                }
            };
        }

        _floatingTokenMeterWindow.Activate();
        _trayLifecycleService?.UpdateState();
    }

    private void HideFloatingTokenMeter(bool closeOnly)
    {
        if (!closeOnly)
        {
            _isTokenMeterRequested = false;
        }

        if (_floatingTokenMeterWindow is not null)
        {
            _floatingTokenMeterWindow.SetMousePassThrough(false);
            _floatingTokenMeterWindow.Close();
            _floatingTokenMeterWindow = null;
        }

        _trayLifecycleService?.UpdateState();
    }

    private void CaptureAndSaveTokenMeterWindowState()
    {
        _tokenMeterWindowState.WasRequested = _isTokenMeterRequested;
        _tokenMeterWindowState.HasVisibilityPreference = true;
        _floatingTokenMeterWindow?.CapturePlacementInto(_tokenMeterWindowState);
        _tokenMeterWindowState.Save();
    }

    public void NavigateToPage(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }

        SelectNavigationItem(pageType);
        PersistNavigationState(pageType);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            var pageType = tag switch
            {
                "dashboard" => typeof(DashboardPage),
                "single" => typeof(SingleStationPage),
                "safety" => typeof(DataSafetyPage),
                "batch" => typeof(BatchComparisonPage),
                "proxy" => typeof(TransparentProxyPage),
                "chat" => typeof(ModelChatPage),
                "apps" => typeof(ApplicationCenterPage),
                "network" => typeof(NetworkReviewPage),
                "history" => typeof(HistoryReportsPage),
                "settings" => typeof(SettingsPage),
                _ => typeof(DashboardPage)
            };

            if (ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType);
                PersistNavigationState(pageType);
            }

            if (RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < CompactChromeWidthThreshold)
            {
                NavView.IsPaneOpen = false;
            }
        }
    }

    /// <summary>
    /// Phase 21: Persists the current navigation page to settings for restore on next launch.
    /// </summary>
    private static void PersistNavigationState(Type pageType)
    {
        var pageName = PageTypeToName(pageType);
        _ = App.Settings.UpdateAsync(s => s with { LastVisitedPage = pageName });
    }

    private static string PageTypeToName(Type pageType)
    {
        if (pageType == typeof(DashboardPage)) return "Dashboard";
        if (pageType == typeof(SingleStationPage)) return "SingleStation";
        if (pageType == typeof(DataSafetyPage)) return "DataSafety";
        if (pageType == typeof(BatchComparisonPage)) return "BatchComparison";
        if (pageType == typeof(TransparentProxyPage)) return "TransparentProxy";
        if (pageType == typeof(ModelChatPage)) return "ModelChat";
        if (pageType == typeof(ApplicationCenterPage)) return "ApplicationCenter";
        if (pageType == typeof(NetworkReviewPage)) return "NetworkReview";
        if (pageType == typeof(HistoryReportsPage)) return "HistoryReports";
        if (pageType == typeof(SettingsPage)) return "Settings";
        return "Dashboard";
    }

    private void SelectNavigationItem(Type pageType)
    {
        var tag = PageTypeToTag(pageType);
        if (string.IsNullOrWhiteSpace(tag))
        {
            NavView.SelectedItem = null;
            return;
        }

        var item = NavView.MenuItems
            .Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            NavView.SelectedItem = item;
        }
    }

    private static string PageTypeToTag(Type pageType)
    {
        if (pageType == typeof(DashboardPage)) return "dashboard";
        if (pageType == typeof(SingleStationPage)) return "single";
        if (pageType == typeof(DataSafetyPage)) return "safety";
        if (pageType == typeof(BatchComparisonPage)) return "batch";
        if (pageType == typeof(TransparentProxyPage)) return "proxy";
        if (pageType == typeof(ModelChatPage)) return "chat";
        if (pageType == typeof(ApplicationCenterPage)) return "apps";
        if (pageType == typeof(NetworkReviewPage)) return "network";
        if (pageType == typeof(HistoryReportsPage)) return "history";
        if (pageType == typeof(SettingsPage)) return "settings";
        return string.Empty;
    }

    private static Type ResolvePageType(string pageName) => pageName switch
    {
        "SingleStation" => typeof(SingleStationPage),
        "DataSafety" => typeof(DataSafetyPage),
        "BatchComparison" => typeof(BatchComparisonPage),
        "TransparentProxy" => typeof(TransparentProxyPage),
        "ModelChat" => typeof(ModelChatPage),
        "ApplicationCenter" => typeof(ApplicationCenterPage),
        "NetworkReview" => typeof(NetworkReviewPage),
        "HistoryReports" => typeof(HistoryReportsPage),
        "Settings" => typeof(SettingsPage),
        _ => typeof(DashboardPage)
    };
}
