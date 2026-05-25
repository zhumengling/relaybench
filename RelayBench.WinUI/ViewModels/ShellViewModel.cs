using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RelayBench.WinUI.Services;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    public const string ProjectHomepageUrl = "https://github.com/zhumengling/relaybench";

    private readonly DateTimeOffset _startTime = DateTimeOffset.Now;
    private DispatcherQueueTimer? _timer;
    private DispatcherQueue? _dispatcherQueue;

    [ObservableProperty] public partial string StatusBarProxyAddress { get; set; } = "127.0.0.1:8080";
    [ObservableProperty] public partial string StatusBarMode { get; set; } = "未连接";
    [ObservableProperty] public partial bool StatusBarSystemProxyEnabled { get; set; }
    [ObservableProperty] public partial string StatusBarSystemProxyText { get; set; } = "本地代理未启动";
    [ObservableProperty] public partial Visibility StatusBarSystemProxyLiveVisibility { get; set; } = Visibility.Collapsed;
    [ObservableProperty] public partial Visibility StatusBarSystemProxyIdleVisibility { get; set; } = Visibility.Visible;
    [ObservableProperty] public partial string StatusBarAssistantSummary { get; set; } = "模型 -- | 0 ms";
    [ObservableProperty] public partial string StatusBarThroughputSummary { get; set; } = "TTFT 0 ms · 0 chars/s";
    [ObservableProperty] public partial string StatusBarUptime { get; set; } = "0s";
    [ObservableProperty] public partial string StatusBarVersion { get; set; } = ResolveDisplayVersion();
    [ObservableProperty] public partial string CommandPalettePlaceholder { get; set; } = "搜索...";
    [ObservableProperty] public partial string EnvironmentLabel { get; set; } = "本地";
    [ObservableProperty] public partial string RuntimeLabel { get; set; } = "运行中";

    // Global progress indicator properties
    [ObservableProperty] public partial bool IsProgressVisible { get; set; }
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial string ProgressStep { get; set; } = "";

    public ShellViewModel()
    {
        GlobalProgressService.ProgressChanged += OnProgressChanged;
    }

    public event EventHandler? AboutDialogOpenRequested;

    public event EventHandler? AboutDialogCloseRequested;

    public event EventHandler? ProjectHomepageOpenRequested;

    public event EventHandler? NavigationRailToggleRequested;

    [RelayCommand]
    private void OpenAboutDialog()
        => AboutDialogOpenRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void CloseAboutDialog()
        => AboutDialogCloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenProjectHomepage()
        => ProjectHomepageOpenRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleNavigationRail()
        => NavigationRailToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnProgressChanged(object? sender, (double Percent, string Step) e)
    {
        if (_dispatcherQueue is not null)
        {
            _dispatcherQueue.TryEnqueue(() => ApplyProgress(e.Percent, e.Step));
        }
        else
        {
            ApplyProgress(e.Percent, e.Step);
        }
    }

    private void ApplyProgress(double percent, string step)
    {
        ProgressPercent = percent;
        ProgressStep = step;
        IsProgressVisible = percent < 100 || !string.IsNullOrEmpty(step);

        // Auto-hide after completion
        if (percent >= 100 && string.IsNullOrEmpty(step))
        {
            IsProgressVisible = false;
        }
    }

    public void StartUptimeTimer()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;
        _dispatcherQueue = queue;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            var elapsed = DateTimeOffset.Now - _startTime;
            StatusBarUptime = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s"
                : elapsed.TotalMinutes >= 1
                    ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                    : $"{elapsed.Seconds}s";
        };
        _timer.Start();
    }

    public void ApplyProxyRuntimeStatus(bool isRunning)
    {
        StatusBarSystemProxyEnabled = isRunning;
        StatusBarSystemProxyText = isRunning ? "本地代理运行中" : "本地代理未启动";
        StatusBarSystemProxyLiveVisibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        StatusBarSystemProxyIdleVisibility = isRunning ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string ResolveDisplayVersion()
    {
        var version = typeof(ShellViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            version = typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3);
        }

        return string.IsNullOrWhiteSpace(version) ? "v0.1.8" : $"v{version}";
    }
}
