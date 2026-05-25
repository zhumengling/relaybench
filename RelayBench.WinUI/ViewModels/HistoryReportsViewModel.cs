using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class HistoryReportsViewModel : ObservableObject
{
    private readonly IHistoryRepository _repository;
    private readonly DiagnosticReportService _diagnosticReportService;
    private readonly string _exportRoot;
    private bool _suppressSelectedReportDetails;
    private bool _hasLoadedOnce;
    private CancellationTokenSource? _filterReloadCts;

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial int SelectedTypeIndex { get; set; }
    [ObservableProperty] public partial string StartDate { get; set; } = "";
    [ObservableProperty] public partial string EndDate { get; set; } = "";
    [ObservableProperty] public partial int TotalReports { get; set; }
    [ObservableProperty] public partial HistoryReportItem? SelectedReport { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial string HistoryTotalRequestsSummary { get; set; } = "\u603B\u8BF7\u6C42 0";
    [ObservableProperty] public partial string HistorySuccessRequestsSummary { get; set; } = "\u6210\u529F\u8BF7\u6C42 0";
    [ObservableProperty] public partial string HistoryErrorRateSummary { get; set; } = "\u9519\u8BEF\u7387 0.00%";
    [ObservableProperty] public partial string HistoryP50Summary { get; set; } = "P50 0 ms";
    [ObservableProperty] public partial string HistoryP95Summary { get; set; } = "P95 0 ms";
    [ObservableProperty] public partial string HistoryP99Summary { get; set; } = "P99 0 ms";
    [ObservableProperty] public partial string HistoryInputThroughputSummary { get; set; } = "Input 0 tokens/s";
    [ObservableProperty] public partial string HistoryOutputThroughputSummary { get; set; } = "Output 0 tokens/s";
    [ObservableProperty] public partial string HistoryTotalInputSummary { get; set; } = "\u603B\u8F93\u5165 0";
    [ObservableProperty] public partial string HistoryTotalOutputSummary { get; set; } = "\u603B\u8F93\u51FA 0";
    [ObservableProperty] public partial string HistoryTimeoutSummary { get; set; } = "Timeouts 0";
    [ObservableProperty] public partial string HistoryRateLimitSummary { get; set; } = "429 count 0";
    [ObservableProperty] public partial string HistoryServerErrorSummary { get; set; } = "5xx count 0";
    [ObservableProperty] public partial string SelectedReportEndpointSummary { get; set; } = "--";
    [ObservableProperty] public partial string SelectedReportProtocolSummary { get; set; } = "--";
    [ObservableProperty] public partial string SelectedReportExitSummary { get; set; } = "0";
    [ObservableProperty] public partial string SelectedReportProxyModeSummary { get; set; } = "--";
    [ObservableProperty] public partial string SelectedReportModelSummary { get; set; } = "--";
    [ObservableProperty] public partial string SelectedReportInputTokensSummary { get; set; } = "0";
    [ObservableProperty] public partial string SelectedReportOutputTokensSummary { get; set; } = "0";
    [ObservableProperty] public partial string SelectedReportPromptCacheTokensSummary { get; set; } = "0";
    [ObservableProperty] public partial string SelectedReportCacheHitRateSummary { get; set; } = "0.0%";
    [ObservableProperty] public partial string SelectedReportOutputTokenSourceSummary { get; set; } = "--";
    [ObservableProperty] public partial string SelectedReportProtocolSupportSummary { get; set; } = "--";
    [ObservableProperty] public partial string SelectedReportAttachmentTitle { get; set; } = "Attachments";
    [ObservableProperty] public partial string SelectedReportStatusText { get; set; } = "--";
    [ObservableProperty] public partial bool HasSelectedReportRouteMapEvidence { get; set; }
    [ObservableProperty] public partial bool HasSelectedReportRouteMapImage { get; set; }
    [ObservableProperty] public partial string SelectedReportRouteMapImagePath { get; set; } = "";
    [ObservableProperty] public partial string SelectedReportRouteMapSummary { get; set; } = "No route map recorded";
    [ObservableProperty] public partial string SelectedReportRouteMapGeoSummary { get; set; } = "";
    [ObservableProperty] public partial string ReportArchiveSummary { get; set; } = "No report archives";
    [ObservableProperty] public partial Microsoft.UI.Xaml.Visibility SelectedReportSuccessVisibility { get; set; } = Microsoft.UI.Xaml.Visibility.Collapsed;
    [ObservableProperty] public partial Microsoft.UI.Xaml.Visibility SelectedReportReviewVisibility { get; set; } = Microsoft.UI.Xaml.Visibility.Visible;

    // InfoBar state for diagnostic export feedback
    [ObservableProperty] public partial bool IsInfoBarOpen { get; set; }
    [ObservableProperty] public partial bool IsInfoBarError { get; set; }
    [ObservableProperty] public partial string InfoBarMessage { get; set; } = "";

    public ObservableCollection<HistoryReportItem> Reports { get; } = new();
    public ObservableCollection<HistoryMetricTile> MetricTiles { get; } = new();
    public ObservableCollection<HistoryProtocolResult> ProtocolResults { get; } = new();
    public ObservableCollection<HistoryChartRow> ChartRows { get; } = new();
    public ObservableCollection<HistoryAttachmentItem> Attachments { get; } = new();
    public ObservableCollection<HistoryCapabilityRow> CapabilityRows { get; } = new();
    public ObservableCollection<HistoryReportArchiveItem> ReportArchives { get; } = new();

    public bool ShowSelectedReportRouteMapPlaceholder => HasSelectedReportRouteMapEvidence && !HasSelectedReportRouteMapImage;
    public bool HasHistoryChartRows => ChartRows.Count > 0;
    public bool HasReportArchives => ReportArchives.Count > 0;

    private static readonly HistoryTypeFilter[] TypeFilters =
    [
        new("", []),
        new("Single station", ["Single station", "Single Station", "\u5355\u7AD9\u6D4B\u8BD5", "Quick", "Deep", "Stability", "Concurrency"]),
        new("Batch comparison", ["Batch comparison", "Batch", "Batch Comparison", "\u6279\u91CF\u8BC4\u6D4B", "\u6279\u91CF\u5BF9\u6BD4"]),
        new("Network review", ["Network review", "Network", "Network Review", "\u7F51\u7EDC\u590D\u6838"]),
        new("Data safety", ["Data safety", "Data Safety", "\u6570\u636E\u5B89\u5168", "Security"]),
        new("Transparent proxy", ["Transparent proxy", "Proxy", "Transparent Proxy", "\u900F\u660E\u4EE3\u7406"]),
        new("Model chat", ["Model chat", "Model Chat", "\u5927\u6A21\u578B\u5BF9\u8BDD", "Chat"]),
        new("\u5E94\u7528\u63A5\u5165", ["\u5E94\u7528\u63A5\u5165", "Application Access", "App Access", "Client Access"])
    ];

    public HistoryReportsViewModel() : this(new HistoryRepository())
    {
    }

    public HistoryReportsViewModel(IHistoryRepository repository, bool autoLoad = true, string? exportRoot = null)
    {
        _repository = repository;
        _diagnosticReportService = new DiagnosticReportService(_repository);
        _exportRoot = string.IsNullOrWhiteSpace(exportRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RelayBench",
                "WinUI",
                "exports")
            : exportRoot;
        if (autoLoad)
        {
            _ = LoadReportsAsync();
        }

        RefreshReportArchiveView();
    }

    public async Task LoadReportsAsync()
    {
        IsLoading = true;
        try
        {
            var selectedId = SelectedReport?.Id;
            var query = BuildQuery();
            var results = (await _repository.QueryAsync(query))
                .Where(MatchesSelectedTypeFilter)
                .Where(MatchesSearchText)
                .ToList();

            Reports.Clear();
            foreach (var r in results)
            {
                Reports.Add(new HistoryReportItem(
                    Id: r.RunId,
                    Time: r.CreatedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                    Type: r.TestType,
                    Detail: r.Summary,
                    Duration: r.DurationMs.HasValue ? FormatDuration(r.DurationMs.Value) : "--",
                    Score: r.Score ?? 0,
                    Success: r.Score is null or >= 60,
                    Date: r.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd")));
            }

            TotalReports = Reports.Count;
            var nextSelection = Reports.FirstOrDefault(report =>
                    string.Equals(report.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ??
                Reports.FirstOrDefault();

            _suppressSelectedReportDetails = true;
            try
            {
                SelectedReport = nextSelection;
            }
            finally
            {
                _suppressSelectedReportDetails = false;
            }

            var hasMixedReportTypes = results
                .Select(static report => report.TestType.Trim())
                .Where(static type => !string.IsNullOrWhiteSpace(type))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();

            await PopulateHistoryAggregateSummaryAsync(results);
            await LoadSelectedReportDetailsAsync(SelectedReport);
            if (hasMixedReportTypes)
            {
                await PopulateHistoryAggregateSummaryAsync(results);
            }

            StatusText = $"Loaded {TotalReports} reports";
            _hasLoadedOnce = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private HistoryQuery BuildQuery()
    {
        DateTime? from = null;
        DateTime? to = null;

        if (DateTime.TryParse(StartDate, out var fromDate))
            from = fromDate.ToUniversalTime();
        if (DateTime.TryParse(EndDate, out var toDate))
            to = toDate.AddDays(1).ToUniversalTime(); // include the full end day

        return new HistoryQuery(
            FromUtc: from,
            ToUtc: to,
            TestType: null,
            EndpointContains: null,
            Limit: 1000);
    }

    private bool MatchesSelectedTypeFilter(HistoryReportSummary summary)
    {
        if (SelectedTypeIndex <= 0 || SelectedTypeIndex >= TypeFilters.Length)
        {
            return true;
        }

        var aliases = TypeFilters[SelectedTypeIndex].Aliases;
        return aliases.Any(alias => string.Equals(summary.TestType, alias, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesSearchText(HistoryReportSummary summary)
    {
        var text = SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return Contains(summary.RunId, text) ||
               Contains(summary.TestType, text) ||
               Contains(summary.Endpoint, text) ||
               Contains(summary.Summary, text) ||
               Contains(summary.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), text);
    }

    [RelayCommand]
    private void SelectReport(HistoryReportItem? item)
    {
        SelectedReport = item;
    }

    partial void OnSelectedReportChanged(HistoryReportItem? value)
    {
        if (_suppressSelectedReportDetails)
        {
            return;
        }

        _ = LoadSelectedReportDetailsAsync(value);
    }

    partial void OnSearchTextChanged(string value) => ScheduleFilterReload();

    partial void OnSelectedTypeIndexChanged(int value) => ScheduleFilterReload();

    partial void OnStartDateChanged(string value) => ScheduleFilterReload();

    partial void OnEndDateChanged(string value) => ScheduleFilterReload();

    partial void OnHasSelectedReportRouteMapEvidenceChanged(bool value)
        => OnPropertyChanged(nameof(ShowSelectedReportRouteMapPlaceholder));

    partial void OnHasSelectedReportRouteMapImageChanged(bool value)
        => OnPropertyChanged(nameof(ShowSelectedReportRouteMapPlaceholder));

    private void ScheduleFilterReload()
    {
        if (!_hasLoadedOnce)
        {
            return;
        }

        _filterReloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterReloadCts = cts;
        _ = ReloadAfterFilterDelayAsync(cts);
    }

    private async Task ReloadAfterFilterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(250, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                await LoadReportsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer filter change superseded this reload.
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadReportsAsync();
        RefreshReportArchiveView();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SelectedReport is null)
        {
            StatusText = "Select a report before exporting";
            return;
        }

        try
        {
            var report = await _repository.GetAsync(SelectedReport.Id);
            if (report is null)
            {
                StatusText = "Selected report no longer exists";
                return;
            }

            var exportRoot = Path.Combine(_exportRoot, SanitizePathSegment(report.RunId));
            Directory.CreateDirectory(exportRoot);

            var markdownPath = Path.Combine(exportRoot, "report.md");
            var jsonPath = Path.Combine(exportRoot, "payload.json");
            await File.WriteAllTextAsync(markdownPath, BuildReportMarkdown(report), Encoding.UTF8);
            await File.WriteAllTextAsync(jsonPath, report.PayloadJson, Encoding.UTF8);
            StatusText = $"Exported bundle: {exportRoot}";
            RefreshReportArchiveView();
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportCurrentReportAsync()
        => await ExportAsync();

    [RelayCommand]
    private async Task DeleteReportAsync()
    {
        var selected = SelectedReport;
        if (selected is not null)
        {
            await _repository.DeleteAsync([selected.Id]);
            await LoadReportsAsync();
            StatusText = $"Deleted: {selected.Id}";
        }
    }

    [RelayCommand]
    private async Task ExportReportsAsync()
    {
        IsInfoBarOpen = false;

        var reportIds = SelectedReport is not null
            ? new[] { SelectedReport.Id }
            : Reports.Select(r => r.Id).ToArray();

        if (reportIds.Length == 0)
        {
            IsInfoBarError = true;
            InfoBarMessage = "没有可导出的报告。";
            IsInfoBarOpen = true;
            return;
        }

        try
        {
            var filePath = await _diagnosticReportService.ExportBundleAsync(
                reportIds,
                _exportRoot);

            IsInfoBarError = false;
            InfoBarMessage = $"报告包已导出到：{filePath}";
            IsInfoBarOpen = true;
            RefreshReportArchiveView();
        }
        catch (IOException ex)
        {
            IsInfoBarError = true;
            InfoBarMessage = $"导出失败：{ex.Message}";
            IsInfoBarOpen = true;
        }
    }

}
