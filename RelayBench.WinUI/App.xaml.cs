using Microsoft.UI.Xaml;
using RelayBench.Services.Infrastructure;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    public static ShellViewModel ShellViewModel { get; } = new();
    public static TransparentProxyViewModel TransparentProxyViewModel { get; } = new();

    /// <summary>
    /// The application-wide settings store. Loaded before the shell window is shown.
    /// Access via <c>((App)App.Current).Settings</c> or <see cref="App.Settings"/>.
    /// </summary>
    public static SettingsStore Settings { get; } = new();

    private SingleInstanceGuard? _singleInstanceGuard;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppDiagnosticLog.Write("App.UnhandledException", e.Exception);
        e.Handled = true;
    }

    private static void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        AppDiagnosticLog.Write("AppDomain.UnhandledException", e.ExceptionObject?.ToString() ?? "Unknown unhandled exception");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppDiagnosticLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Enforce single-instance (Requirement 5.1, 5.2).
        _singleInstanceGuard = new SingleInstanceGuard();
        if (!_singleInstanceGuard.TryAcquire())
        {
            SingleInstanceGuard.ActivateExistingInstance();
            Application.Current.Exit();
            return;
        }

        // Load persisted settings before showing the shell (Requirement 10.2).
        // If the file is missing or corrupt, defaults are applied (Requirement 10.4).
        await Settings.LoadAsync();
        TransparentProxyViewModel.SetStrategyRepository(new StrategyRepository());
        await TransparentProxyViewModel.LoadStrategiesAsync();
        TransparentProxyViewModel.SetRouteRepository(new RouteRepository());
        await TransparentProxyViewModel.LoadRoutesAsync();
        TransparentProxyViewModel.InitializeOAuthState();

        MainWindow = new MainWindow();
        ThemeService.SetTheme(ThemeService.ResolveTheme(Settings.Current.Theme));
        _singleInstanceGuard.StartActivationListener(() =>
        {
            var window = MainWindow;
            window?.DispatcherQueue.TryEnqueue(window.RestoreFromExternalActivation);
        });
        MainWindow.Closed += OnMainWindowClosed;
        MainWindow.Activate();
    }

    private void OnMainWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        // Release the single-instance mutex on exit (Requirement 5.4).
        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;
    }
}
