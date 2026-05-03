using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using RelayBench.App.Infrastructure;
using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.AdvancedTesting.Reporting;
using RelayBench.Core.AdvancedTesting.Runners;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels.AdvancedTesting;

public sealed class AdvancedTestLabViewModel : ObservableObject
{
    private readonly Func<AdvancedEndpoint> _singleStationEndpointProvider;
    private readonly IAdvancedTestRunner _runner;
    private readonly ProxyDiagnosticsService _modelCatalogService = new();
    private readonly AdvancedReportExporter _reportExporter = new();
    private CancellationTokenSource? _runCancellationSource;
    private AdvancedTestRunResult? _lastResult;
    private AdvancedTestSuiteViewModel? _selectedSuite;
    private AdvancedTestCaseViewModel? _selectedCase;
    private string _advancedBaseUrl = string.Empty;
    private string _advancedApiKey = string.Empty;
    private string _advancedModel = string.Empty;
    private bool _isAdvancedModelMenuOpen;
    private bool _advancedIgnoreTlsErrors;
    private int _advancedTimeoutSeconds = 20;
    private bool _isFetchingModels;
    private bool _isRunning;
    private bool _isStopping;
    private bool _isStopConfirmationOpen;
    private double _overallProgress;
    private string _currentStatusText = "选择测试套件后即可开始。";
    private string _endpointSummary = "尚未读取当前接口。";
    private bool _isDetailDialogOpen;
    private string _detailDialogTitle = string.Empty;
    private string _detailDialogContent = string.Empty;

    public AdvancedTestLabViewModel(Func<AdvancedEndpoint> endpointProvider)
        : this(endpointProvider, new AdvancedTestRunner())
    {
    }

    public AdvancedTestLabViewModel(Func<AdvancedEndpoint> endpointProvider, IAdvancedTestRunner runner)
    {
        _singleStationEndpointProvider = endpointProvider;
        _runner = runner;
        var seedEndpoint = _singleStationEndpointProvider();
        _advancedBaseUrl = seedEndpoint.BaseUrl;
        _advancedApiKey = seedEndpoint.ApiKey;
        _advancedModel = seedEndpoint.Model;
        _advancedIgnoreTlsErrors = seedEndpoint.IgnoreTlsErrors;
        _advancedTimeoutSeconds = Math.Clamp(seedEndpoint.TimeoutSeconds, 5, 300);

        var suites = runner.Suites.Select(static suite => new AdvancedTestSuiteViewModel(suite)).ToArray();
        Suites = new ObservableCollection<AdvancedTestSuiteViewModel>(suites);

        var caseDefinitions = runner.Suites
            .SelectMany(static suite => suite.Cases)
            .GroupBy(static item => item.TestId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(static definition => new AdvancedTestCaseViewModel(definition))
            .ToArray();
        TestCases = new ObservableCollection<AdvancedTestCaseViewModel>(caseDefinitions);
        VisibleTestCases = new ObservableCollection<AdvancedTestCaseViewModel>();
        AdvancedModelOptions = [];
        Logs = [];
        ScenarioScores =
        [
            new ScenarioScoreViewModel("总分", "协议、稳定性、性能、能力、成本和网络风险综合"),
            new ScenarioScoreViewModel("Codex", "Tool Calling、流式、Reasoning、长上下文和错误透传"),
            new ScenarioScoreViewModel("Agent", "工具调用、多轮上下文、JSON、稳定性和限流"),
            new ScenarioScoreViewModel("RAG", "Embeddings、长文本、JSON 和稳定性"),
            new ScenarioScoreViewModel("聊天", "TTFT、输出速度、多轮记忆、稳定性和成本透明")
        ];

        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        FetchAdvancedModelsCommand = new AsyncRelayCommand(FetchAdvancedModelsAsync, () => CanFetchAdvancedModels);
        StopCommand = new AsyncRelayCommand(StopAsync, () => CanStop);
        ConfirmStopCommand = new AsyncRelayCommand(ConfirmStopAsync, () => IsStopConfirmationOpen && IsRunning);
        CancelStopCommand = new AsyncRelayCommand(CancelStopAsync, () => IsStopConfirmationOpen);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync, () => CanRetryFailed);
        ExportReportCommand = new AsyncRelayCommand(ExportReportAsync, () => CanExportReport);
        CopyDiagnosticSummaryCommand = new AsyncRelayCommand(CopyDiagnosticSummaryAsync, () => HasAnyResult);
        OpenRawRequestCommand = new AsyncRelayCommand<AdvancedTestCaseViewModel?>(OpenRawRequestAsync, item => item?.HasRawExchange == true);
        OpenRawResponseCommand = new AsyncRelayCommand<AdvancedTestCaseViewModel?>(OpenRawResponseAsync, item => item?.HasRawExchange == true);
        OpenErrorDetailCommand = new AsyncRelayCommand<AdvancedTestCaseViewModel?>(OpenErrorDetailAsync, item => item?.CanOpenError == true);
        CloseDetailDialogCommand = new AsyncRelayCommand(CloseDetailDialogAsync);

        SelectedSuite = Suites.FirstOrDefault();
        RefreshEndpointSummary();
        AddLog("INFO", "数据安全测试已就绪。");
    }

    public ObservableCollection<AdvancedTestSuiteViewModel> Suites { get; }

    public ObservableCollection<AdvancedTestCaseViewModel> TestCases { get; }

    public ObservableCollection<AdvancedTestCaseViewModel> VisibleTestCases { get; }

    public ObservableCollection<string> AdvancedModelOptions { get; }

    public ObservableCollection<AdvancedLogEntryViewModel> Logs { get; }

    public ObservableCollection<ScenarioScoreViewModel> ScenarioScores { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand FetchAdvancedModelsCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand ConfirmStopCommand { get; }

    public AsyncRelayCommand CancelStopCommand { get; }

    public AsyncRelayCommand RetryFailedCommand { get; }

    public AsyncRelayCommand ExportReportCommand { get; }

    public AsyncRelayCommand CopyDiagnosticSummaryCommand { get; }

    public AsyncRelayCommand<AdvancedTestCaseViewModel?> OpenRawRequestCommand { get; }

    public AsyncRelayCommand<AdvancedTestCaseViewModel?> OpenRawResponseCommand { get; }

    public AsyncRelayCommand<AdvancedTestCaseViewModel?> OpenErrorDetailCommand { get; }

    public AsyncRelayCommand CloseDetailDialogCommand { get; }

    public AdvancedTestSuiteViewModel? SelectedSuite
    {
        get => _selectedSuite;
        set
        {
            if (SetProperty(ref _selectedSuite, value))
            {
                foreach (var suite in Suites)
                {
                    suite.IsActive = ReferenceEquals(suite, value);
                }

                RefreshVisibleCases();
                OnPropertyChanged(nameof(IsSecuritySuiteActive));
            }
        }
    }

    public AdvancedTestCaseViewModel? SelectedCase
    {
        get => _selectedCase;
        set => SetProperty(ref _selectedCase, value);
    }

    public string AdvancedBaseUrl
    {
        get => _advancedBaseUrl;
        set
        {
            if (SetProperty(ref _advancedBaseUrl, value))
            {
                OnEndpointConfigChanged();
            }
        }
    }

    public string AdvancedApiKey
    {
        get => _advancedApiKey;
        set
        {
            if (SetProperty(ref _advancedApiKey, value))
            {
                OnEndpointConfigChanged();
            }
        }
    }

    public string AdvancedModel
    {
        get => _advancedModel;
        set
        {
            if (SetProperty(ref _advancedModel, value))
            {
                OnPropertyChanged(nameof(AdvancedModelOptionSelection));
                OnEndpointConfigChanged();
            }
        }
    }

    public string? AdvancedModelOptionSelection
    {
        get => AdvancedModelOptions.FirstOrDefault(model => string.Equals(model, AdvancedModel, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                AdvancedModel = value;
                IsAdvancedModelMenuOpen = false;
            }
        }
    }

    public bool IsAdvancedModelMenuOpen
    {
        get => _isAdvancedModelMenuOpen;
        set => SetProperty(ref _isAdvancedModelMenuOpen, value);
    }

    public bool IsFetchingModels
    {
        get => _isFetchingModels;
        private set
        {
            if (SetProperty(ref _isFetchingModels, value))
            {
                OnPropertyChanged(nameof(CanFetchAdvancedModels));
                OnPropertyChanged(nameof(FetchAdvancedModelsButtonText));
                RefreshCommands();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanFetchAdvancedModels));
                OnPropertyChanged(nameof(CanRetryFailed));
                OnPropertyChanged(nameof(StartButtonText));
                OnPropertyChanged(nameof(StopButtonText));
                RefreshCommands();
            }
        }
    }

    public bool IsStopping
    {
        get => _isStopping;
        private set
        {
            if (SetProperty(ref _isStopping, value))
            {
                OnPropertyChanged(nameof(StopButtonText));
                RefreshCommands();
            }
        }
    }

    public double OverallProgress
    {
        get => _overallProgress;
        private set => SetProperty(ref _overallProgress, Math.Clamp(value, 0, 100));
    }

    public string CurrentStatusText
    {
        get => _currentStatusText;
        private set => SetProperty(ref _currentStatusText, value);
    }

    public string EndpointSummary
    {
        get => _endpointSummary;
        private set => SetProperty(ref _endpointSummary, value);
    }

    public bool IsDetailDialogOpen
    {
        get => _isDetailDialogOpen;
        private set => SetProperty(ref _isDetailDialogOpen, value);
    }

    public bool IsStopConfirmationOpen
    {
        get => _isStopConfirmationOpen;
        private set
        {
            if (SetProperty(ref _isStopConfirmationOpen, value))
            {
                ConfirmStopCommand.RaiseCanExecuteChanged();
                CancelStopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DetailDialogTitle
    {
        get => _detailDialogTitle;
        private set => SetProperty(ref _detailDialogTitle, value);
    }

    public string DetailDialogContent
    {
        get => _detailDialogContent;
        private set => SetProperty(ref _detailDialogContent, value);
    }

    public bool HasAnyResult => TestCases.Any(static item => item.Status is AdvancedTestStatus.Passed or AdvancedTestStatus.Partial or AdvancedTestStatus.Failed);

    public bool IsSecuritySuiteActive
        => string.Equals(SelectedSuite?.SuiteId, "security-red-team", StringComparison.OrdinalIgnoreCase);

    public bool HasRedTeamResult
        => TestCases.Any(static item =>
            item.Definition.Category == AdvancedTestCategory.SecurityRedTeam &&
            item.Status is AdvancedTestStatus.Passed or AdvancedTestStatus.Partial or AdvancedTestStatus.Failed);

    public string RedTeamRiskText
    {
        get
        {
            var results = GetRedTeamResultItems();
            if (results.Length == 0)
            {
                return "未运行";
            }

            if (results.Any(static item => item.Status == AdvancedTestStatus.Failed && item.RiskLevel == AdvancedRiskLevel.Critical))
            {
                return "严重";
            }

            if (results.Any(static item => item.Status == AdvancedTestStatus.Failed))
            {
                return "高";
            }

            return results.Any(static item => item.Status == AdvancedTestStatus.Partial)
                ? "中"
                : "低";
        }
    }

    public string RedTeamRiskBrush
        => RedTeamRiskText switch
        {
            "严重" => "#7F1D1D",
            "高" => "#DC2626",
            "中" => "#D97706",
            "低" => "#059669",
            _ => "#64748B"
        };

    public string RedTeamRiskDetail
    {
        get
        {
            var results = GetRedTeamResultItems();
            if (results.Length == 0)
            {
                return "数据安全风险未运行。";
            }

            var passed = results.Count(static item => item.Status == AdvancedTestStatus.Passed);
            var partial = results.Count(static item => item.Status == AdvancedTestStatus.Partial);
            var failed = results.Count(static item => item.Status == AdvancedTestStatus.Failed);
            return $"数据安全风险：通过 {passed}，复核 {partial}，失败 {failed}。";
        }
    }

    public bool CanStart => !IsRunning && BuildConfiguredEndpoint().IsComplete;

    public bool CanFetchAdvancedModels
        => !IsRunning &&
           !IsFetchingModels &&
           !string.IsNullOrWhiteSpace(AdvancedBaseUrl) &&
           !string.IsNullOrWhiteSpace(AdvancedApiKey);

    public bool CanStop => IsRunning && !IsStopping;

    public bool CanRetryFailed => !IsRunning && TestCases.Any(static item => item.Status is AdvancedTestStatus.Failed or AdvancedTestStatus.Partial);

    public bool CanExportReport => !IsRunning && _lastResult is not null;

    public string StartButtonText => IsRunning ? "测试中..." : "开始测试";

    public string StopButtonText => IsStopping ? "正在停止..." : "停止测试";

    public string FetchAdvancedModelsButtonText => IsFetchingModels ? "正在拉取..." : "拉取数据安全模型";

    public int SelectedSuiteCount => Suites.Count(static item => item.IsSelected);

    public int SelectedCaseCount => BuildSelectedTestIds().Count;

    public void RefreshEndpointSummary()
    {
        var endpoint = BuildConfiguredEndpoint();
        EndpointSummary = endpoint.IsComplete
            ? $"{endpoint.BaseUrl.Trim()} · {endpoint.Model.Trim()} · 超时 {endpoint.TimeoutSeconds}s"
            : "请先在单站测试中填写接口地址、API Key 和模型。";
    }

    private async Task StartAsync()
    {
        RefreshEndpointSummary();
        var endpoint = BuildConfiguredEndpoint();
        if (!endpoint.IsComplete)
        {
            CurrentStatusText = "请先填写接口地址、API Key 和模型。";
            AddLog("WARN", CurrentStatusText);
            return;
        }

        var selectedIds = BuildSelectedTestIds();
        if (selectedIds.Count == 0)
        {
            CurrentStatusText = "请至少选择一个测试项。";
            AddLog("WARN", CurrentStatusText);
            return;
        }

        ResetForRun(selectedIds);
        IsRunning = true;
        IsStopping = false;
        _runCancellationSource = new CancellationTokenSource();
        AddLog("INFO", $"开始数据安全测试：{selectedIds.Count} 项。");

        try
        {
            var progress = new Progress<AdvancedTestProgress>(ApplyProgress);
            var plan = new AdvancedTestPlan(endpoint, selectedIds.ToArray(), new AdvancedTestRunOptions());
            _lastResult = await _runner.RunAsync(plan, progress, _runCancellationSource.Token);
            ApplyScores(_lastResult.Scores);
            OverallProgress = 100;
            CurrentStatusText = $"测试完成：通过 {_lastResult.PassedCount}，部分 {_lastResult.PartialCount}，失败 {_lastResult.FailedCount}。";
            AddLog("INFO", CurrentStatusText);
        }
        catch (OperationCanceledException)
        {
            CurrentStatusText = "测试已停止。";
            AddLog("WARN", CurrentStatusText);
        }
        finally
        {
            IsRunning = false;
            IsStopping = false;
            IsStopConfirmationOpen = false;
            _runCancellationSource?.Dispose();
            _runCancellationSource = null;
            OnResultStateChanged();
        }
    }

    private async Task FetchAdvancedModelsAsync()
    {
        if (!CanFetchAdvancedModels)
        {
            CurrentStatusText = "请先填写数据安全测试的接口地址和 API Key。";
            AddLog("WARN", CurrentStatusText);
            return;
        }

        IsFetchingModels = true;
        CurrentStatusText = "正在拉取数据安全测试的独立模型列表...";
        AddLog("INFO", CurrentStatusText);

        try
        {
            var settings = new ProxyEndpointSettings(
                AdvancedBaseUrl,
                AdvancedApiKey,
                AdvancedModel,
                _advancedIgnoreTlsErrors,
                _advancedTimeoutSeconds);
            var result = await _modelCatalogService.FetchModelsAsync(settings);
            AdvancedModelOptions.Clear();
            foreach (var model in BuildModelOptionList(result))
            {
                AdvancedModelOptions.Add(model);
            }
            OnPropertyChanged(nameof(AdvancedModelOptionSelection));

            if (result.Success && AdvancedModelOptions.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(AdvancedModel) ||
                    !AdvancedModelOptions.Contains(AdvancedModel, StringComparer.OrdinalIgnoreCase))
                {
                    AdvancedModel = AdvancedModelOptions[0];
                }

                CurrentStatusText = $"数据安全测试模型拉取完成，共 {AdvancedModelOptions.Count} 个；该列表不与单站测试共用。";
                AddLog("INFO", CurrentStatusText);
            }
            else
            {
                CurrentStatusText = string.IsNullOrWhiteSpace(result.Error)
                    ? result.Summary
                    : $"{result.Summary} {result.Error}";
                AddLog("WARN", CurrentStatusText);
            }
        }
        finally
        {
            IsFetchingModels = false;
            RefreshEndpointSummary();
        }
    }

    private Task StopAsync()
    {
        if (!CanStop)
        {
            return Task.CompletedTask;
        }

        IsStopConfirmationOpen = true;
        return Task.CompletedTask;
    }

    private Task ConfirmStopAsync()
    {
        if (!IsRunning)
        {
            IsStopConfirmationOpen = false;
            return Task.CompletedTask;
        }

        IsStopConfirmationOpen = false;
        IsStopping = true;
        CurrentStatusText = "正在停止测试...";
        AddLog("WARN", CurrentStatusText);
        _runCancellationSource?.Cancel();
        return Task.CompletedTask;
    }

    private Task CancelStopAsync()
    {
        IsStopConfirmationOpen = false;
        return Task.CompletedTask;
    }

    private async Task RetryFailedAsync()
    {
        foreach (var item in TestCases)
        {
            item.IsSelected = item.Status is AdvancedTestStatus.Failed or AdvancedTestStatus.Partial;
        }

        foreach (var suite in Suites)
        {
            suite.IsSelected = suite.Definition.Cases.Any(definition =>
                TestCases.Any(testCase => testCase.TestId == definition.TestId && testCase.IsSelected));
        }

        await StartAsync();
    }

    private Task ExportReportAsync()
    {
        if (_lastResult is null)
        {
            return Task.CompletedTask;
        }

        var exportDirectory = Path.Combine(RelayBenchPaths.ExportsDirectory, "advanced-test-lab");
        Directory.CreateDirectory(exportDirectory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var markdownPath = Path.Combine(exportDirectory, $"advanced-test-{stamp}.md");
        var jsonPath = Path.Combine(exportDirectory, $"advanced-test-{stamp}.json");
        File.WriteAllText(markdownPath, _reportExporter.BuildMarkdown(_lastResult), Encoding.UTF8);
        File.WriteAllText(jsonPath, _reportExporter.BuildJson(_lastResult), Encoding.UTF8);
        CurrentStatusText = $"数据安全测试报告已导出：{markdownPath}";
        AddLog("INFO", CurrentStatusText);
        return Task.CompletedTask;
    }

    private Task CopyDiagnosticSummaryAsync()
    {
        var summary = BuildDiagnosticSummary();
        Clipboard.SetText(summary);
        CurrentStatusText = "诊断摘要已复制。";
        AddLog("INFO", CurrentStatusText);
        return Task.CompletedTask;
    }

    private Task OpenRawRequestAsync(AdvancedTestCaseViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        DetailDialogTitle = $"{item.DisplayName} · 原始请求";
        DetailDialogContent = item.RawRequest;
        IsDetailDialogOpen = true;
        return Task.CompletedTask;
    }

    private Task OpenRawResponseAsync(AdvancedTestCaseViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        DetailDialogTitle = $"{item.DisplayName} · 原始响应";
        DetailDialogContent = item.RawResponse;
        IsDetailDialogOpen = true;
        return Task.CompletedTask;
    }

    private Task OpenErrorDetailAsync(AdvancedTestCaseViewModel? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        DetailDialogTitle = $"{item.DisplayName} · 判定解读";
        DetailDialogContent = item.ErrorDetail;
        IsDetailDialogOpen = true;
        return Task.CompletedTask;
    }

    private Task CloseDetailDialogAsync()
    {
        IsDetailDialogOpen = false;
        return Task.CompletedTask;
    }

    private void ApplyProgress(AdvancedTestProgress progress)
    {
        OverallProgress = progress.Percent;
        CurrentStatusText = progress.Message;
        var item = TestCases.FirstOrDefault(testCase => string.Equals(testCase.TestId, progress.TestId, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            if (progress.Status == AdvancedTestStatus.Running)
            {
                item.MarkRunning();
                SelectedCase = item;
            }

            if (progress.Result is not null)
            {
                item.ApplyResult(progress.Result);
                AddLog(progress.Result.Status == AdvancedTestStatus.Passed ? "PASS" : "INFO", progress.Message);
                OnResultStateChanged();
            }
        }
    }

    private void ResetForRun(IReadOnlySet<string> selectedIds)
    {
        OverallProgress = 0;
        _lastResult = null;
        foreach (var item in TestCases)
        {
            if (selectedIds.Contains(item.TestId))
            {
                item.ResetForRun();
            }
        }

        ApplyScores(new AdvancedScenarioScores(0, 0, 0, 0, 0));
        OnResultStateChanged();
    }

    private HashSet<string> BuildSelectedTestIds()
    {
        var selectedSuites = Suites.Where(static item => item.IsSelected).ToArray();
        var enabledCases = TestCases.Where(static item => item.IsSelected).ToDictionary(static item => item.TestId, StringComparer.OrdinalIgnoreCase);
        var ids = selectedSuites
            .SelectMany(static suite => suite.Definition.Cases)
            .Where(definition => enabledCases.ContainsKey(definition.TestId))
            .Select(static definition => definition.TestId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ids.Count == 0)
        {
            foreach (var item in TestCases.Where(static item => item.IsSelected))
            {
                ids.Add(item.TestId);
            }
        }

        OnPropertyChanged(nameof(SelectedSuiteCount));
        OnPropertyChanged(nameof(SelectedCaseCount));
        return ids;
    }

    private void RefreshVisibleCases()
    {
        VisibleTestCases.Clear();
        var suite = SelectedSuite;
        var ids = suite?.Definition.Cases.Select(static item => item.TestId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                  ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in TestCases.Where(item => ids.Count == 0 || ids.Contains(item.TestId)))
        {
            VisibleTestCases.Add(item);
        }
    }

    private void ApplyScores(AdvancedScenarioScores scores)
    {
        ScenarioScores[0].Score = scores.Overall;
        ScenarioScores[0].Detail = "综合健康度";
        ScenarioScores[1].Score = scores.CodexFit;
        ScenarioScores[1].Detail = "Codex 适配";
        ScenarioScores[2].Score = scores.AgentFit;
        ScenarioScores[2].Detail = "Agent 适配";
        ScenarioScores[3].Score = scores.RagFit;
        ScenarioScores[3].Detail = "RAG 适配";
        ScenarioScores[4].Score = scores.ChatExperience;
        ScenarioScores[4].Detail = "聊天体验";
    }

    private string BuildDiagnosticSummary()
    {
        StringBuilder builder = new();
        builder.AppendLine("RelayBench 数据安全测试诊断摘要");
        builder.AppendLine(EndpointSummary);
        if (_lastResult is not null)
        {
            builder.AppendLine($"总分: {_lastResult.Scores.Overall:0.0}");
            builder.AppendLine($"Codex: {_lastResult.Scores.CodexFit:0.0}, Agent: {_lastResult.Scores.AgentFit:0.0}, RAG: {_lastResult.Scores.RagFit:0.0}, 聊天: {_lastResult.Scores.ChatExperience:0.0}");
            builder.AppendLine($"数据安全风险: {RedTeamRiskText}，{RedTeamRiskDetail}");
        }

        foreach (var item in TestCases.Where(static item => item.Status is AdvancedTestStatus.Passed or AdvancedTestStatus.Partial or AdvancedTestStatus.Failed))
        {
            builder.AppendLine($"- {item.DisplayName}: {item.StatusText}, {item.ScoreText} 分, {item.Summary}");
        }

        return builder.ToString().TrimEnd();
    }

    private AdvancedEndpoint BuildConfiguredEndpoint()
        => new(
            AdvancedBaseUrl,
            AdvancedApiKey,
            AdvancedModel,
            _advancedIgnoreTlsErrors,
            _advancedTimeoutSeconds);

    private void OnEndpointConfigChanged()
    {
        RefreshEndpointSummary();
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanFetchAdvancedModels));
        RefreshCommands();
    }

    private static IReadOnlyList<string> BuildModelOptionList(ProxyModelCatalogResult result)
    {
        var models = result.ModelItems is { Count: > 0 }
            ? result.ModelItems.Select(static item => item.Id)
            : result.Models;

        return models
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void AddLog(string level, string message)
        => Logs.Insert(0, new AdvancedLogEntryViewModel(DateTimeOffset.Now, level, message));

    private void OnResultStateChanged()
    {
        OnPropertyChanged(nameof(HasAnyResult));
        OnPropertyChanged(nameof(HasRedTeamResult));
        OnPropertyChanged(nameof(RedTeamRiskText));
        OnPropertyChanged(nameof(RedTeamRiskBrush));
        OnPropertyChanged(nameof(RedTeamRiskDetail));
        OnPropertyChanged(nameof(CanRetryFailed));
        OnPropertyChanged(nameof(CanExportReport));
        OnPropertyChanged(nameof(SelectedCaseCount));
        RefreshCommands();
    }

    private AdvancedTestCaseViewModel[] GetRedTeamResultItems()
        => TestCases
            .Where(static item =>
                item.Definition.Category == AdvancedTestCategory.SecurityRedTeam &&
                item.Status is AdvancedTestStatus.Passed or AdvancedTestStatus.Partial or AdvancedTestStatus.Failed)
            .ToArray();

    private void RefreshCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        FetchAdvancedModelsCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ConfirmStopCommand.RaiseCanExecuteChanged();
        CancelStopCommand.RaiseCanExecuteChanged();
        RetryFailedCommand.RaiseCanExecuteChanged();
        ExportReportCommand.RaiseCanExecuteChanged();
        CopyDiagnosticSummaryCommand.RaiseCanExecuteChanged();
        OpenRawRequestCommand.RaiseCanExecuteChanged();
        OpenRawResponseCommand.RaiseCanExecuteChanged();
        OpenErrorDetailCommand.RaiseCanExecuteChanged();
    }
}
