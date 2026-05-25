using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RelayBench.Core.Models;
using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.AdvancedTesting.Reporting;
using RelayBench.Core.AdvancedTesting.Runners;
using RelayBench.Core.Services;
using RelayBench.WinUI.Storage;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Data safety red-team testing VM. Uses the real AdvancedTestRunner from
/// RelayBench.Core.AdvancedTesting.Runners to execute security test suites
/// and compute per-category and aggregate safety scores.
/// </summary>
public sealed partial class DataSafetyViewModel : ObservableObject
{
    private readonly AdvancedTestRunner _testRunner = new();
    private readonly AdvancedReportExporter _reportExporter = new();
    private readonly EndpointHistoryStore _historyStore = new();
    private readonly IHistoryRepository _historyRepository = new HistoryRepository();
    private readonly ProxyEndpointModelCacheService _modelCacheService = new();
    private CancellationTokenSource? _cts;
    private AdvancedTestRunResult? _lastRunResult;
    private DateTime _testStartTime;
    private IReadOnlyList<double> _historicalScores = Array.Empty<double>();
    private IReadOnlyList<double> _historicalPassRates = Array.Empty<double>();
    private Dictionary<string, int> _plannedCategoryTotals = new(StringComparer.Ordinal);
    private int _historicalTotalScenarios;
    private int _historicalPassedScenarios;
    private int _historicalFailedScenarios;

    [ObservableProperty] public partial string BaseUrl { get; set; } = "";
    [ObservableProperty] public partial string ApiKey { get; set; } = "";
    [ObservableProperty] public partial string Model { get; set; } = "";
    [ObservableProperty] public partial int ProtocolIndex { get; set; } = 1;
    [ObservableProperty] public partial bool IsTesting { get; set; }
    [ObservableProperty] public partial bool IsStopping { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial int TotalScenarios { get; set; }
    [ObservableProperty] public partial int CompletedScenarios { get; set; }
    [ObservableProperty] public partial int PassedScenarios { get; set; }
    [ObservableProperty] public partial int FailedScenarios { get; set; }
    [ObservableProperty] public partial double SafetyScore { get; set; }
    [ObservableProperty] public partial string PassRate { get; set; } = "0.0%";
    [ObservableProperty] public partial int Concurrency { get; set; } = 10;
    [ObservableProperty] public partial int Timeout { get; set; } = 60;
    [ObservableProperty] public partial string ValidationError { get; set; } = "";
    [ObservableProperty] public partial bool IgnoreTlsErrors { get; set; }
    [ObservableProperty] public partial bool IncludeRawExchange { get; set; } = true;
    [ObservableProperty] public partial bool AllowParallelSuites { get; set; }
    [ObservableProperty] public partial int PreferredWireApiIndex { get; set; }
    [ObservableProperty] public partial int SelectedEvidenceTabIndex { get; set; }
    [ObservableProperty] public partial string ProtocolCacheSummary { get; set; } = "填写接口后显示协议缓存";
    [ObservableProperty] public partial bool IsTransparentProxyEndpoint { get; set; }
    [ObservableProperty] public partial string EndpointSourceText { get; set; } = "直接接口";
    [ObservableProperty] public partial string TransparentProxyContextText { get; set; } = "未接入透明代理模型池";
    [ObservableProperty] public partial int DisplayTotalScenarios { get; set; }
    [ObservableProperty] public partial int DisplayPassedScenarios { get; set; }
    [ObservableProperty] public partial int DisplayFailedScenarios { get; set; }
    [ObservableProperty] public partial PointCollection? ScoreTrendPoints { get; set; }
    [ObservableProperty] public partial PointCollection? PassTrendPoints { get; set; }

    // Dimension scores
    [ObservableProperty] public partial double OverallScore { get; set; }
    [ObservableProperty] public partial double CodexScore { get; set; }
    [ObservableProperty] public partial double AgentScore { get; set; }
    [ObservableProperty] public partial double RagScore { get; set; }
    [ObservableProperty] public partial double ChatScore { get; set; }

    // Export availability
    [ObservableProperty] public partial bool CanExport { get; set; }
    [ObservableProperty] public partial bool CanRetryFailed { get; set; }

    // Right panel properties
    [ObservableProperty] public partial string TestDuration { get; set; } = "0s";
    [ObservableProperty] public partial SafetyTestResult? SelectedResult { get; set; }
    [ObservableProperty] public partial int HighRiskCount { get; set; }
    [ObservableProperty] public partial int MediumRiskCount { get; set; }
    [ObservableProperty] public partial int LowRiskCount { get; set; }
    [ObservableProperty] public partial int PassedResultCount { get; set; }
    [ObservableProperty] public partial int SkippedResultCount { get; set; }

    public bool HasSelectedResult => SelectedResult is not null;
    public string SelectedScenarioInfo => SelectedResult?.ScenarioInfo ?? "0";
    public string SelectedRawOutput => BuildSelectedEvidenceOutput();
    public string RiskDistributionSummary => (HighRiskCount + MediumRiskCount + LowRiskCount + PassedResultCount + SkippedResultCount) == 0
        ? "高 0 / 中 0 / 低 0 / 通过 0 / 跳过 0"
        : $"高 {HighRiskCount} / 中 {MediumRiskCount} / 低 {LowRiskCount} / 通过 {PassedResultCount} / 跳过 {SkippedResultCount}";
    public string SafetyScoreText => $"{SafetyScore:0}";
    public string CurrentScoreText => $"{SafetyScore:0.0} / 100";
    public string RunProgressText => $"{CompletedScenarios}/{Math.Max(TotalScenarios, DisplayTotalScenarios)} 场景";
    public string RunHeaderModelText => string.IsNullOrWhiteSpace(Model) ? "未选择模型" : Model.Trim();
    public string RunHeaderEndpointText => string.IsNullOrWhiteSpace(BaseUrl) ? "未填写接口" : BaseUrl.Trim();
    public string RunStateText => IsStopping ? "停止中" : IsTesting ? "测试中" : Results.Count > 0 ? "已完成" : "待开始";
    public bool CanStopTest => IsTesting && _cts is not null && !IsStopping;
    public string EvidenceTabTitle => SelectedEvidenceTabIndex switch
    {
        1 => "判定依据",
        2 => "请求记录",
        3 => "脱敏日志",
        _ => "原始输出"
    };

    /// <summary>Available models fetched from the endpoint.</summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    public string ProtocolPrefix => ProtocolIndex == 0 ? "https://" : "http://";

    public string SuiteEnablementSummary
    {
        get
        {
            var suiteCount = TestSuites.Count;
            var enabledSuites = TestSuites.Count(static suite => suite.IsEnabled);
            var enabledCases = TestSuites.Where(static suite => suite.IsEnabled).Sum(static suite => suite.EnabledCaseCount);
            var totalCases = TestSuites.Sum(static suite => suite.TotalCaseCount == 0 ? 1 : suite.TotalCaseCount);
            return $"{enabledSuites} / {suiteCount} 套件，{enabledCases} / {totalCases} 测试项已启用";
        }
    }

    public string ProgressTotalText => $"全部 {DisplayTotalScenarios}";
    public string ProgressPassedText => $"通过 {DisplayPassedScenarios}";
    public string ProgressFailedText => $"失败 {DisplayFailedScenarios}";

    public ObservableCollection<RiskMatrixCell> RiskMatrixCells { get; } = new();
    public ObservableCollection<RiskDistributionItem> RiskDistributionItems { get; } = new();
    public ObservableCollection<ProtocolPriorityItem> ProtocolPriorityItems { get; } = new();

    /// <summary>Endpoint history entries for the history flyout.</summary>
    public ObservableCollection<EndpointHistoryItem> EndpointHistory { get; } = new();

    /// <summary>
    /// Last 3 failed scenarios for the right panel evidence card.
    /// </summary>
    public ObservableCollection<SafetyTestResult> RecentFailures { get; } = new();

    /// <summary>
    /// Recent run log entries, newest first. Mirrors the useful WPF Advanced Test Lab log feed.
    /// </summary>
    public ObservableCollection<SafetyRunLogEntry> RunLogs { get; } = new();

    /// <summary>
    /// Indicates whether there are no recent failures (for empty state display).
    /// </summary>
    public bool NoRecentFailures => RecentFailures.Count == 0;

    public bool NoRunLogs => RunLogs.Count == 0;

    public int RunLogCount => RunLogs.Count;

    /// <summary>
    /// Indicates whether there is a validation error to display.
    /// </summary>
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    partial void OnValidationErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidationError));
    }

    partial void OnProtocolIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ProtocolPrefix));
        OnPropertyChanged(nameof(RunHeaderEndpointText));
    }

    partial void OnDisplayTotalScenariosChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressTotalText));
        OnPropertyChanged(nameof(RunProgressText));
    }

    partial void OnDisplayPassedScenariosChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressPassedText));
    }

    partial void OnDisplayFailedScenariosChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressFailedText));
    }

    partial void OnSelectedResultChanged(SafetyTestResult? value)
    {
        OnPropertyChanged(nameof(HasSelectedResult));
        OnPropertyChanged(nameof(SelectedScenarioInfo));
        OnPropertyChanged(nameof(SelectedRawOutput));
    }

    partial void OnSelectedEvidenceTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedRawOutput));
        OnPropertyChanged(nameof(EvidenceTabTitle));
    }

    partial void OnSafetyScoreChanged(double value)
    {
        OnPropertyChanged(nameof(SafetyScoreText));
        OnPropertyChanged(nameof(CurrentScoreText));
    }

    partial void OnBaseUrlChanged(string value)
    {
        OnPropertyChanged(nameof(RunHeaderEndpointText));
        if (IsTransparentProxyEndpoint && !IsLocalTransparentProxyBaseUrl(value))
        {
            MarkDirectEndpoint();
        }

        RefreshProtocolPriorityFromSelection();
    }

    partial void OnApiKeyChanged(string value)
    {
        RefreshProtocolPriorityFromSelection();
    }

    partial void OnModelChanged(string value)
    {
        OnPropertyChanged(nameof(RunHeaderModelText));
        RefreshProtocolPriorityFromSelection();
    }

    partial void OnPreferredWireApiIndexChanged(int value)
    {
        RefreshProtocolPriorityFromSelection();
    }

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(RunStateText));
        OnPropertyChanged(nameof(CanStopTest));
    }

    partial void OnIsStoppingChanged(bool value)
    {
        OnPropertyChanged(nameof(RunStateText));
        OnPropertyChanged(nameof(CanStopTest));
    }

    partial void OnTotalScenariosChanged(int value)
    {
        OnPropertyChanged(nameof(RunProgressText));
    }

    partial void OnCompletedScenariosChanged(int value)
    {
        OnPropertyChanged(nameof(RunProgressText));
        OnPropertyChanged(nameof(RunStateText));
    }

    [ObservableProperty] public partial bool PromptInjectionEnabled { get; set; } = true;
    [ObservableProperty] public partial bool JailbreakEnabled { get; set; } = true;
    [ObservableProperty] public partial bool PiiLeakEnabled { get; set; } = true;
    [ObservableProperty] public partial bool ContentComplianceEnabled { get; set; } = true;
    [ObservableProperty] public partial bool RolePlayEnabled { get; set; }
    [ObservableProperty] public partial bool MultiLangBypassEnabled { get; set; }
    [ObservableProperty] public partial bool EncodingObfuscationEnabled { get; set; }

    /// <summary>
    /// Prioritized list of test suites. Users can reorder via drag-and-drop
    /// and toggle each suite on/off. Execution order follows list order.
    /// </summary>
    public ObservableCollection<TestSuiteItem> TestSuites { get; } = new();

    /// <summary>
    /// Timeline items representing each scenario's execution status.
    /// Populated when a test starts and updated as scenarios complete.
    /// </summary>
    public ObservableCollection<ScenarioTimelineItem> TimelineItems { get; } = new();

    public ObservableCollection<SafetyTestResult> Results { get; } = new();

    /// <summary>
    /// Per-category breakdown of pass/fail counts.
    /// </summary>
    public ObservableCollection<CategoryScoreItem> CategoryScores { get; } = new();

    public DataSafetyViewModel()
    {
        InitializeTestSuites();
        InitializeChartState();
        LoadPersistedEndpoint();
        LoadHistoricalChartState();
        RefreshProtocolPriorityFromSelection();
        _ = RefreshProtocolPriorityAsync();
    }

    private void InitializeTestSuites()
    {
        TestSuites.Clear();
        for (var i = 0; i < _testRunner.Suites.Count; i++)
        {
            var definition = _testRunner.Suites[i];
            var suite = new TestSuiteItem(definition, isEnabled: true, i)
            {
                IsExpanded = i == 0
            };
            suite.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(TestSuiteItem.IsEnabled) or nameof(TestSuiteItem.CaseSummary))
                {
                    OnPropertyChanged(nameof(SuiteEnablementSummary));
                }
            };
            foreach (var testCase in suite.Cases)
            {
                testCase.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TestCaseItem.IsEnabled))
                    {
                        OnPropertyChanged(nameof(SuiteEnablementSummary));
                    }
                };
            }

            TestSuites.Add(suite);
        }

        OnPropertyChanged(nameof(SuiteEnablementSummary));
    }

    private void InitializeChartState()
    {
        RiskMatrixCells.Clear();
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                RiskMatrixCells.Add(new RiskMatrixCell(0, ResolveRiskMatrixTone(row, column)));
            }
        }

        UpdateTrendChart(Array.Empty<double>(), Array.Empty<double>());
        ApplyProgressSummary(0, 0, 0);
        ApplyRiskDistributionCounts(0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Loads persisted endpoint values from the shared endpoint store (real API key).
    /// Falls back to endpoint history for BaseUrl/Model if shared store is empty.
    /// </summary>
    private void LoadPersistedEndpoint()
    {
        try
        {
            var shared = SharedEndpointStore.Load();
            if (shared is not null && !string.IsNullOrWhiteSpace(shared.BaseUrl))
            {
                BaseUrl = shared.BaseUrl;
                ApiKey = shared.ApiKey;
                Model = shared.Model;
                return;
            }

            var store = new EndpointHistoryStore();
            var items = store.LoadAsync().GetAwaiter().GetResult();
            if (items is { Count: > 0 })
            {
                var latest = items[0];
                BaseUrl = latest.BaseUrl ?? "";
                Model = latest.Model ?? "";
            }
        }
        catch
        {
            // Best-effort
        }
    }

    private void LoadHistoricalChartState()
    {
        try
        {
            var summaries = _historyRepository
                .QueryAsync(new HistoryQuery(TestType: "数据安全", Limit: 14))
                .GetAwaiter()
                .GetResult()
                .OrderBy(static item => item.CreatedAtUtc)
                .ToArray();

            if (summaries.Length == 0)
            {
                ApplyHistoricalFallback(null);
                return;
            }

            _historicalScores = summaries
                .Select(static item => Math.Clamp(item.Score ?? 0, 0, 100))
                .ToArray();

            var passRates = new List<double>(summaries.Length);
            HistoryReport? latest = null;
            foreach (var summary in summaries)
            {
                var report = _historyRepository
                    .GetAsync(summary.RunId)
                    .GetAwaiter()
                    .GetResult();
                latest = report ?? latest;
                passRates.Add(report is not null &&
                              TryReadSafetyHistory(report.PayloadJson, out _, out _, out _, out _, out var passRate)
                    ? passRate
                    : 0);
            }

            _historicalPassRates = passRates;

            ApplyHistoricalFallback(latest);
        }
        catch
        {
            ApplyHistoricalFallback(null);
        }
    }

    private void ApplyHistoricalFallback(HistoryReport? latest)
    {
        if (latest is null || !TryReadSafetyHistory(latest.PayloadJson, out var matrix, out var total, out var passed, out var failed, out var passRate))
        {
            _historicalPassRates = _historicalScores.Count == 0
                ? Array.Empty<double>()
                : _historicalScores.Select(static _ => 0d).ToArray();
            SafetyScore = _historicalScores.Count == 0 ? 0 : _historicalScores[^1];
            OverallScore = SafetyScore;
            CodexScore = 0;
            AgentScore = 0;
            RagScore = 0;
            ChatScore = 0;
            PassRate = "0.0%";
            StatusText = "暂无History数据安全测试，当前显示 0";
            UpdateRiskMatrix(new int[4, 4]);
            UpdateTrendChart(_historicalScores, _historicalPassRates);
            ApplyProgressSummary(0, 0, 0);
            ApplyRiskDistributionCounts(0, 0, 0, 0, 0);
            return;
        }

        SafetyScore = Math.Clamp(latest.Score ?? 0, 0, 100);
        PassRate = $"{Math.Clamp(passRate, 0, 100):F1}%";
        ApplyHistoricalDimensionScores(latest.PayloadJson, SafetyScore);
        StatusText = $"已加载History数据安全测试：{latest.CreatedAtUtc.ToLocalTime():MM-dd HH:mm}";
        _historicalTotalScenarios = total;
        _historicalPassedScenarios = passed;
        _historicalFailedScenarios = failed;
        if (_historicalPassRates.Count != _historicalScores.Count)
        {
            _historicalPassRates = MergeLatestPassRate(_historicalScores.Count, passRate);
        }

        UpdateRiskMatrix(matrix);
        UpdateTrendChart(_historicalScores, _historicalPassRates);
        ApplyProgressSummary(_historicalTotalScenarios, _historicalPassedScenarios, _historicalFailedScenarios);
        ApplyHistoricalRiskDistribution(matrix, _historicalPassedScenarios, _historicalFailedScenarios);
    }

    private void ApplyHistoricalDimensionScores(string payloadJson, double fallbackScore)
    {
        OverallScore = fallbackScore;
        CodexScore = fallbackScore;
        AgentScore = fallbackScore;
        RagScore = fallbackScore;
        ChatScore = fallbackScore;

        if (!HistoryPayloadReader.TryParse(payloadJson, out var document))
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            OverallScore = HistoryPayloadReader.FirstDouble(root, ["Scores", "Overall"], ["scores", "overall"]) ?? OverallScore;
            CodexScore = HistoryPayloadReader.FirstDouble(root, ["Scores", "CodexFit"], ["scores", "codexFit"]) ?? CodexScore;
            AgentScore = HistoryPayloadReader.FirstDouble(root, ["Scores", "AgentFit"], ["scores", "agentFit"]) ?? AgentScore;
            RagScore = HistoryPayloadReader.FirstDouble(root, ["Scores", "RagFit"], ["scores", "ragFit"]) ?? RagScore;
            ChatScore = HistoryPayloadReader.FirstDouble(root, ["Scores", "ChatExperience"], ["scores", "chatExperience"]) ?? ChatScore;
        }
    }

    private static IReadOnlyList<double> MergeLatestPassRate(int scoreCount, double latestPassRate)
    {
        if (scoreCount <= 0)
        {
            return Array.Empty<double>();
        }

        var values = Enumerable.Repeat(0d, scoreCount).ToArray();
        values[^1] = latestPassRate;
        return values;
    }

}
