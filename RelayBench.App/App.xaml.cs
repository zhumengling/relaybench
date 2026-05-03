using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RelayBench.App.Infrastructure;

namespace RelayBench.App;

public partial class App : Application
{
    private static string StartupLogPath => RelayBenchPaths.StartupLogPath;
    private Mutex? _singleInstanceMutex;
    private SingleInstanceActivationService? _singleInstanceActivationService;
    private bool _ownsSingleInstanceMutex;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryBecomePrimaryInstance())
        {
            _ = SingleInstanceActivationService.TrySendActivationRequestAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult();
            Shutdown(0);
            return;
        }

        ResetStartupLog();
        WriteStartupLog("应用启动开始。");
        WriteStartupLog($"应用目录：{AppContext.BaseDirectory}");
        WriteStartupLog($"工作目录：{RelayBenchPaths.RootDirectory}");

        try
        {
            ScrollChromeStyleEnforcer.Register();
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new MainWindow();
            MainWindow.Show();
            StartSingleInstanceActivationService();

            WriteStartupLog("主窗口已创建并显示。");
        }
        catch (Exception ex)
        {
            WriteStartupLog(BuildExceptionText("启动失败。", ex));
            MessageBox.Show(
                $"RelayBench 启动失败，请查看日志：{TryGetStartupLogPath()}",
                "RelayBench",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WriteStartupLog($"应用退出。Code={e.ApplicationExitCode}");
        DisposeSingleInstanceResources();
        base.OnExit(e);
    }

    private bool TryBecomePrimaryInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                SingleInstanceActivationService.MutexName,
                out var createdNew);
            _ownsSingleInstanceMutex = createdNew;
            if (createdNew)
            {
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }
        catch (Exception ex)
        {
            TryWriteStartupLog(path =>
            {
                EnsureStartupLogDirectory(path);
                File.AppendAllText(
                    path,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] Single instance guard failed, continuing startup.{Environment.NewLine}{ex}{Environment.NewLine}",
                    new UTF8Encoding(false));
            });
            return true;
        }
    }

    private void StartSingleInstanceActivationService()
    {
        _singleInstanceActivationService = new SingleInstanceActivationService(() =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow mainWindow)
                {
                    mainWindow.RestoreFromExternalActivation();
                }
            });
        });
        _singleInstanceActivationService.Start();
    }

    private void DisposeSingleInstanceResources()
    {
        _singleInstanceActivationService?.Dispose();
        _singleInstanceActivationService = null;
        if (_singleInstanceMutex is null)
        {
            return;
        }

        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteStartupLog(BuildExceptionText("UI 线程未处理异常。", e.Exception));
        MessageBox.Show(
            $"RelayBench 运行中出现未处理异常，请查看日志：{TryGetStartupLogPath()}",
            "RelayBench",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(-1);
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteStartupLog(BuildExceptionText("AppDomain 未处理异常。", exception));
        }
        else
        {
            WriteStartupLog($"AppDomain 未处理异常。Terminating={e.IsTerminating}");
        }
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteStartupLog(BuildExceptionText("TaskScheduler 未观察到的异常。", e.Exception));
        e.SetObserved();
    }

    private static void ResetStartupLog()
    {
        TryWriteStartupLog(path => File.WriteAllText(path, string.Empty, new UTF8Encoding(false)));
    }

    private static void WriteStartupLog(string message)
    {
        TryWriteStartupLog(path =>
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}";
            File.AppendAllText(path, line, new UTF8Encoding(false));
        });
    }

    private static void TryWriteStartupLog(Action<string> write)
    {
        try
        {
            var path = StartupLogPath;
            EnsureStartupLogDirectory(path);
            write(path);
        }
        catch
        {
        }
    }

    private static void EnsureStartupLogDirectory(string startupLogPath)
    {
        var directory = Path.GetDirectoryName(startupLogPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string TryGetStartupLogPath()
    {
        try
        {
            return StartupLogPath;
        }
        catch
        {
            return "startup log unavailable";
        }
    }

    private static string BuildExceptionText(string title, Exception exception)
        => $"{title}{Environment.NewLine}{exception}";
}
