using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private bool _suppressPersistence;

    [ObservableProperty] public partial int SelectedThemeIndex { get; set; }
    [ObservableProperty] public partial string ListenAddress { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int ListenPort { get; set; } = 8080;
    [ObservableProperty] public partial bool AutoStart { get; set; } = true;
    [ObservableProperty] public partial bool SystemProxy { get; set; } = true;
    [ObservableProperty] public partial int MaxConcurrency { get; set; } = 32;
    [ObservableProperty] public partial int RequestTimeout { get; set; } = 30;
    [ObservableProperty] public partial int CacheTtl { get; set; } = 600;
    [ObservableProperty] public partial string DataDirectory { get; set; } = "";
    [ObservableProperty] public partial bool AutoBackup { get; set; } = true;
    [ObservableProperty] public partial int RetentionDays { get; set; } = 30;
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsErrorVisible { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";

    public SettingsViewModel()
    {
        _settings = App.Settings;
        LoadFromStore();
    }

    public string ThemeName => SelectedThemeIndex switch
    {
        0 => "浅色",
        1 => "深色",
        _ => "跟随系统"
    };

    public string ProxyEndpointText => $"{ListenAddress}:{ListenPort}";

    public string AutoStartText => AutoStart ? "已启用" : "已禁用";

    public string SystemProxyText => SystemProxy ? "已启用" : "已禁用";

    public string BackupText => AutoBackup ? $"{RetentionDays} 天" : "已禁用";

    public string PerformanceSummaryText => $"{MaxConcurrency} 并发 / {RequestTimeout}s 超时 / {CacheTtl}s TTL";

    /// <summary>
    /// Populates all bound fields from the current persisted settings.
    /// </summary>
    private void LoadFromStore()
    {
        _suppressPersistence = true;
        try
        {
            var s = _settings.Current;

            SelectedThemeIndex = s.Theme switch
            {
                "Light" => 0,
                "Dark" => 1,
                _ => 2 // "System" or any unknown value
            };

            ListenAddress = s.ProxyListenAddress;
            ListenPort = s.ProxyListenPort;
            AutoStart = s.AutoStartProxy;
            SystemProxy = s.RegisterSystemProxy;
            MaxConcurrency = s.MaxConcurrency;
            RequestTimeout = s.RequestTimeoutSeconds;
            CacheTtl = s.CacheTtlSeconds;
            AutoBackup = s.AutoBackup;
            RetentionDays = s.RetentionDays;

            DataDirectory = StoragePaths.Root;
        }
        finally
        {
            _suppressPersistence = false;
        }
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ThemeName));
        var theme = value switch
        {
            0 => ElementTheme.Light,
            1 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ThemeService.SetTheme(theme);

        var themeStr = value switch
        {
            0 => "Light",
            1 => "Dark",
            _ => "System"
        };
        PersistAsync(s => s with { Theme = themeStr });
    }

    partial void OnListenAddressChanged(string value)
    {
        OnPropertyChanged(nameof(ProxyEndpointText));
        PersistAsync(s => s with { ProxyListenAddress = value });
    }

    partial void OnListenPortChanged(int value)
    {
        OnPropertyChanged(nameof(ProxyEndpointText));
        PersistAsync(s => s with { ProxyListenPort = value });
    }

    partial void OnAutoStartChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoStartText));
        PersistAsync(s => s with { AutoStartProxy = value });
    }

    partial void OnSystemProxyChanged(bool value)
    {
        OnPropertyChanged(nameof(SystemProxyText));
        PersistAsync(s => s with { RegisterSystemProxy = value });
    }

    partial void OnMaxConcurrencyChanged(int value)
    {
        OnPropertyChanged(nameof(PerformanceSummaryText));
        PersistAsync(s => s with { MaxConcurrency = value });
    }

    partial void OnRequestTimeoutChanged(int value)
    {
        OnPropertyChanged(nameof(PerformanceSummaryText));
        PersistAsync(s => s with { RequestTimeoutSeconds = value });
    }

    partial void OnCacheTtlChanged(int value)
    {
        OnPropertyChanged(nameof(PerformanceSummaryText));
        PersistAsync(s => s with { CacheTtlSeconds = value });
    }

    partial void OnAutoBackupChanged(bool value)
    {
        OnPropertyChanged(nameof(BackupText));
        PersistAsync(s => s with { AutoBackup = value });
    }

    partial void OnRetentionDaysChanged(int value)
    {
        OnPropertyChanged(nameof(BackupText));
        PersistAsync(s => s with { RetentionDays = value });
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        // Force an immediate persist of the current in-memory state
        try
        {
            await _settings.UpdateAsync(s => s with
            {
                Theme = SelectedThemeIndex switch { 0 => "Light", 1 => "Dark", _ => "System" },
                ProxyListenAddress = ListenAddress,
                ProxyListenPort = ListenPort,
                AutoStartProxy = AutoStart,
                RegisterSystemProxy = SystemProxy,
                MaxConcurrency = MaxConcurrency,
                RequestTimeoutSeconds = RequestTimeout,
                CacheTtlSeconds = CacheTtl,
                AutoBackup = AutoBackup,
                RetentionDays = RetentionDays
            });

            IsErrorVisible = false;
            StatusText = "设置已保存";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        try
        {
            await _settings.UpdateAsync(_ => AppSettings.Defaults);
            LoadFromStore();
            IsErrorVisible = false;
            StatusText = "设置已恢复默认值";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// Persists a single field change to the settings store.
    /// On failure, shows an error and keeps the in-memory UI state unchanged
    /// (the store itself also keeps its previous state on write failure).
    /// </summary>
    private async void PersistAsync(Func<AppSettings, AppSettings> mutate)
    {
        if (_suppressPersistence) return;

        try
        {
            await _settings.UpdateAsync(mutate);
            IsErrorVisible = false;
            ErrorMessage = "";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        IsErrorVisible = true;
        StatusText = "";
    }
}
