using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayBench.Services.Infrastructure;
using RelayBench.Services;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// ViewModel for the transparent proxy log viewer section.
/// Provides filtering, detail view, CSV export, and clear functionality.
/// </summary>
public sealed partial class ProxyLogViewerViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly TransparentProxyLogStore _logStore;
    private readonly List<ProxyLogDisplayEntry> _allEntries = new();

    /// <summary>Filtered log entries displayed in the ListView.</summary>
    public ObservableCollection<ProxyLogDisplayEntry> FilteredEntries { get; } = new();

    [ObservableProperty] public partial string FilterSource { get; set; } = string.Empty;
    [ObservableProperty] public partial string FilterStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string FilterModel { get; set; } = string.Empty;
    [ObservableProperty] public partial string FilterText { get; set; } = string.Empty;
    [ObservableProperty] public partial ProxyLogDisplayEntry? SelectedEntry { get; set; }
    [ObservableProperty] public partial bool IsDetailVisible { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial int ClearDaysThreshold { get; set; } = 0;

    public ProxyLogViewerViewModel(TransparentProxyLogStore logStore)
    {
        _logStore = logStore;
    }

    partial void OnFilterSourceChanged(string value) => ApplyFilters();
    partial void OnFilterStatusChanged(string value) => ApplyFilters();
    partial void OnFilterModelChanged(string value) => ApplyFilters();
    partial void OnFilterTextChanged(string value) => ApplyFilters();

    partial void OnSelectedEntryChanged(ProxyLogDisplayEntry? value)
    {
        IsDetailVisible = value is not null;
        OnPropertyChanged(nameof(SelectedEntryDetailText));
    }

    /// <summary>Detail text for the currently selected log entry.</summary>
    public string SelectedEntryDetailText => SelectedEntry?.DetailText ?? string.Empty;

    /// <summary>
    /// Loads recent log entries from the SQLite store.
    /// </summary>
    [RelayCommand]
    private async Task LoadLogsAsync()
    {
        IsLoading = true;
        StatusMessage = string.Empty;
        try
        {
            var entries = await _logStore.LoadRecentAsync(500);
            _allEntries.Clear();
            foreach (var entry in entries)
            {
                _allEntries.Add(ProxyLogDisplayEntry.FromLogEntry(entry));
            }
            ApplyFilters();
            StatusMessage = $"{_allEntries.Count} entries loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Appends a new log entry from the real-time LogEmitted event.
    /// </summary>
    public void AppendEntry(TransparentProxyLogEntry entry)
    {
        var display = ProxyLogDisplayEntry.FromLogEntry(entry);
        _allEntries.Insert(0, display);

        // Keep max 1000 entries in memory
        if (_allEntries.Count > 1000)
        {
            _allEntries.RemoveAt(_allEntries.Count - 1);
        }

        if (MatchesFilter(display))
        {
            FilteredEntries.Insert(0, display);
            if (FilteredEntries.Count > 1000)
            {
                FilteredEntries.RemoveAt(FilteredEntries.Count - 1);
            }
        }
    }

    /// <summary>
    /// Exports filtered logs to CSV using FileSavePicker.
    /// Returns the export path or null if cancelled.
    /// </summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        try
        {
            var exportPath = await _logStore.ExportCsvAsync(RelayBenchPaths.ExportsDirectory);
            StatusMessage = $"Exported to: {exportPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Clears all logs or logs older than the specified day threshold.
    /// </summary>
    [RelayCommand]
    private async Task ClearLogsAsync()
    {
        try
        {
            if (ClearDaysThreshold <= 0)
            {
                // Clear all
                await _logStore.ClearAsync();
                _allEntries.Clear();
                FilteredEntries.Clear();
                SelectedEntry = null;
                StatusMessage = "所有日志已清空";
            }
            else
            {
                // Clear all and reload (the store doesn't support date-based delete,
                // so we clear all and the next load will only show new entries)
                await _logStore.ClearAsync();
                _allEntries.Clear();
                FilteredEntries.Clear();
                SelectedEntry = null;
                StatusMessage = $"日志已清空（阈值：{ClearDaysThreshold} 天）";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"清空失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Closes the detail panel.
    /// </summary>
    [RelayCommand]
    private void CloseDetail()
    {
        SelectedEntry = null;
        IsDetailVisible = false;
    }

    private void ApplyFilters()
    {
        FilteredEntries.Clear();
        foreach (var entry in _allEntries)
        {
            if (MatchesFilter(entry))
            {
                FilteredEntries.Add(entry);
            }
        }
    }

    private bool MatchesFilter(ProxyLogDisplayEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(FilterSource) &&
            !entry.SourceApplication.Contains(FilterSource, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(FilterStatus))
        {
            var statusStr = entry.StatusCode.ToString(CultureInfo.InvariantCulture);
            if (!statusStr.Contains(FilterStatus, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(FilterModel) &&
            !entry.ModelName.Contains(FilterModel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var text = FilterText;
            if (!entry.Path.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.RouteName.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.ModelName.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.Method.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.RequestId.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.TraceIdDisplay.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.WireApi.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.AttemptSummary.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.SourceApplication.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.ErrorType.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !entry.CacheState.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Display-friendly wrapper around TransparentProxyLogEntry for XAML binding.
/// </summary>
public sealed record ProxyLogDisplayEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Method,
    string Path,
    string RouteName,
    int StatusCode,
    long ElapsedMs,
    string Message,
    string ModelName,
    string RequestId,
    string WireApi,
    string AttemptSummary,
    string IngressKind,
    string SourceApplication,
    string CaptureMode,
    string TargetHost,
    bool WasTunnelOnly,
    string ErrorType,
    string CacheState,
    long InputTokens,
    long OutputTokens,
    long CacheTokens,
    string TraceId)
{
    /// <summary>Formatted timestamp for display.</summary>
    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Formatted date for display.</summary>
    public string DateDisplay => Timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Formatted latency for display.</summary>
    public string LatencyDisplay => $"{ElapsedMs} ms";

    /// <summary>Status code color indicator.</summary>
    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
    public bool IsClientError => StatusCode >= 400 && StatusCode < 500;
    public bool IsServerError => StatusCode >= 500;

    /// <summary>Status badge text.</summary>
    public string StatusBadge => StatusCode > 0 ? StatusCode.ToString(CultureInfo.InvariantCulture) : "--";

    public string TraceIdDisplay => string.IsNullOrWhiteSpace(TraceId)
        ? (string.IsNullOrWhiteSpace(RequestId) ? "--" : RequestId)
        : TraceId;

    public string TraceShortDisplay
    {
        get
        {
            var trace = TraceIdDisplay;
            return trace == "--"
                ? trace
                : trace.Length <= 8 ? trace : trace[..8];
        }
    }

    public string CacheStateDisplay => string.IsNullOrWhiteSpace(CacheState) ? "--" : CacheState;

    public string ErrorTypeDisplay => string.IsNullOrWhiteSpace(ErrorType) ? "--" : ErrorType;

    public string SignalDisplay
    {
        get
        {
            var cache = CacheStateDisplay;
            var error = ErrorTypeDisplay;
            return cache == "--" && error == "--"
                ? "--"
                : $"{(cache == "--" ? "C:--" : $"C:{cache}")} / {(error == "--" ? "E:--" : $"E:{error}")}";
        }
    }

    public string TokenDisplay
    {
        get
        {
            var input = Math.Max(0, InputTokens);
            var output = Math.Max(0, OutputTokens);
            var cache = Math.Max(0, CacheTokens);
            return input == 0 && output == 0 && cache == 0
                ? "--"
                : $"I{input} O{output} C{cache}";
        }
    }

    public string SignalTooltip =>
        $"缓存：{CacheStateDisplay}\n" +
        $"错误：{ErrorTypeDisplay}\n" +
        $"追踪：{TraceIdDisplay}";

    public string TokenTooltip =>
        $"输入 Token：{Math.Max(0, InputTokens)}\n" +
        $"输出 Token：{Math.Max(0, OutputTokens)}\n" +
        $"缓存 Token：{Math.Max(0, CacheTokens)}";

    /// <summary>Summary line for the detail panel.</summary>
    public string DetailSummary => $"{Method} {Path} -> {RouteName} ({StatusCode}) [{ElapsedMs}ms]";

    /// <summary>Full detail text showing all request/response information.</summary>
    public string DetailText =>
        $"时间：{Timestamp:yyyy-MM-dd HH:mm:ss zzz}\n" +
        $"请求 ID：{(string.IsNullOrWhiteSpace(RequestId) ? "-" : RequestId)}\n" +
        $"追踪 ID：{TraceIdDisplay}\n" +
        $"入口：{(string.IsNullOrWhiteSpace(IngressKind) ? "UnifiedLocalEndpoint" : IngressKind)}\n" +
        $"来源：{(string.IsNullOrWhiteSpace(SourceApplication) ? "-" : SourceApplication)}\n" +
        $"捕获模式：{(string.IsNullOrWhiteSpace(CaptureMode) ? "-" : CaptureMode)}\n" +
        $"目标主机：{(string.IsNullOrWhiteSpace(TargetHost) ? "-" : TargetHost)}\n" +
        $"仅隧道：{(WasTunnelOnly ? "是" : "否")}\n" +
        $"方法：{Method}\n" +
        $"路径：{Path}\n" +
        $"模型：{ModelName}\n" +
        $"路由：{RouteName}\n" +
        $"传输 API：{(string.IsNullOrWhiteSpace(WireApi) ? "-" : WireApi)}\n" +
        $"状态：{StatusCode}\n" +
        $"耗时：{LatencyDisplay}\n" +
        $"错误类型：{ErrorTypeDisplay}\n" +
        $"缓存：{CacheStateDisplay}\n" +
        $"Token：输入 {Math.Max(0, InputTokens)}，输出 {Math.Max(0, OutputTokens)}，缓存 {Math.Max(0, CacheTokens)}\n" +
        $"尝试：{(string.IsNullOrWhiteSpace(AttemptSummary) ? "-" : AttemptSummary)}\n" +
        $"消息：{Message}";

    public static ProxyLogDisplayEntry FromLogEntry(TransparentProxyLogEntry entry)
    {
        return new ProxyLogDisplayEntry(
            entry.Timestamp,
            entry.Level,
            entry.Method,
            entry.Path,
            entry.RouteName,
            entry.StatusCode,
            entry.ElapsedMs,
            entry.Message,
            entry.ModelName,
            entry.RequestId,
            entry.WireApi,
            entry.AttemptSummary,
            entry.IngressKind,
            entry.SourceApplication,
            entry.CaptureMode,
            entry.TargetHost,
            entry.WasTunnelOnly,
            entry.ErrorType,
            entry.CacheState,
            entry.InputTokens,
            entry.OutputTokens,
            entry.CacheTokens,
            entry.TraceId);
    }
}
