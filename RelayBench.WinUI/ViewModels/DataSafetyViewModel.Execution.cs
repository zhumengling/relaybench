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

public sealed partial class DataSafetyViewModel : ObservableObject
{
    [RelayCommand]
    private async Task StartTestAsync()
    {
        // Clear any previous validation error
        ValidationError = "";

        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusText = "请填写接口地址和接口密钥";
            return;
        }

        // Validate that at least one test suite is enabled (Requirement 6.5)
        var enabledSuites = TestSuites.Where(s => s.IsEnabled).ToList();
        if (enabledSuites.Count == 0)
        {
            ClearResultState();
            ValidationError = "至少需要启用一个测试套件。";
            StatusText = "无法开始：未启用测试套件";
            return;
        }

        // Sync legacy toggle properties from TestSuites collection
        SyncTogglePropertiesFromSuites();

        // Map enabled UI suites to real AdvancedTestRunner test IDs
        var selectedTestIds = MapEnabledSuitesToTestIds(enabledSuites);
        await RunTestIdsAsync(selectedTestIds, "正在运行安全测试...");
    }

    private async Task RunTestIdsAsync(IReadOnlyList<string> selectedTestIds, string startingStatus)
    {
        if (selectedTestIds.Count == 0)
        {
            ClearResultState();
            ValidationError = "至少需要一个可执行测试项。";
            StatusText = "无法开始：没有可执行测试项";
            AddRunLog("WARN", StatusText);
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsStopping = false;
        IsTesting = true;
        CanExport = false;
        CanRetryFailed = false;
        StatusText = startingStatus;
        CompletedScenarios = 0;
        PassedScenarios = 0;
        FailedScenarios = 0;
        SafetyScore = 0;
        OverallScore = 0;
        CodexScore = 0;
        AgentScore = 0;
        RagScore = 0;
        ChatScore = 0;
        TestDuration = "0s";
        Results.Clear();
        CategoryScores.Clear();
        RecentFailures.Clear();
        ClearRunLogs();
        SelectedResult = null;
        ResetRiskDistribution();
        OnPropertyChanged(nameof(NoRecentFailures));
        _lastRunResult = null;
        _testStartTime = DateTime.Now;
        AddRunLog("INFO", $"{startingStatus} 共 {selectedTestIds.Count} 个场景，模型 {RunHeaderModelText}");

        TotalScenarios = selectedTestIds.Count;
        ApplyProgressSummary(TotalScenarios, 0, 0);
        UpdateRiskMatrix(new int[4, 4]);
        UpdateTrendChart(_historicalScores, _historicalPassRates);
        InitializeCategoryScores(selectedTestIds);

        // Initialize timeline items (all pending)
        TimelineItems.Clear();
        for (int t = 0; t < TotalScenarios; t++)
        {
            TimelineItems.Add(new ScenarioTimelineItem(t, ScenarioStatus.Pending));
        }

        var endpoint = new AdvancedEndpoint(
            NormalizeBaseUrl(BaseUrl),
            ApiKey.Trim(),
            Model.Trim(),
            IgnoreTlsErrors: IgnoreTlsErrors,
            TimeoutSeconds: Math.Clamp(Timeout, 5, 300),
            PreferredWireApi: GetPreferredWireApi());

        // Save endpoint to shared store for cross-page auto-fill
        _ = SharedEndpointStore.SaveAsync(endpoint.BaseUrl, ApiKey, Model);

        var plan = new AdvancedTestPlan(
            endpoint,
            selectedTestIds,
            new AdvancedTestRunOptions(
                AllowParallelSuites: AllowParallelSuites,
                MaxParallelism: Math.Clamp(Concurrency, 1, 32),
                IncludeRawExchange: IncludeRawExchange));

        var progress = new Progress<AdvancedTestProgress>(OnTestProgress);

        try
        {
            var result = await _testRunner.RunAsync(plan, progress, _cts.Token);
            _lastRunResult = result;

            // Update dimension scores from the real score calculator
            OverallScore = result.Scores.Overall;
            CodexScore = result.Scores.CodexFit;
            AgentScore = result.Scores.AgentFit;
            RagScore = result.Scores.RagFit;
            ChatScore = result.Scores.ChatExperience;

            // Build per-category breakdown
            BuildCategoryScores(result);

            SafetyScore = OverallScore;
            PassRate = TotalScenarios > 0
                ? $"{(double)PassedScenarios / TotalScenarios * 100:F1}%"
                : "0.0%";
            ApplyProgressSummary(TotalScenarios, PassedScenarios, FailedScenarios);
            UpdateLiveChartsFromResults();
            StatusText = "安全测试已完成";
            AddRunLog("INFO", $"{StatusText}: {PassedScenarios}/{TotalScenarios} 通过，Score {SafetyScore:0.0}/100");
            TestDuration = FormatDuration(DateTime.Now - _testStartTime);
            CanExport = true;
            CanRetryFailed = result.Results.Any(static item =>
                item.Status is AdvancedTestStatus.Failed or AdvancedTestStatus.Partial);
            await RecordRunHistoryAsync(result, DateTime.Now - _testStartTime, cancelled: false);
            await RefreshProtocolPriorityAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "安全测试已Cancel";
            AddRunLog("WARN", $"{StatusText}: 已完成 {CompletedScenarios}/{TotalScenarios}");
            // Still allow export of partial results
            if (Results.Count > 0)
                CanExport = true;
            CanRetryFailed = _lastRunResult?.Results.Any(static item =>
                item.Status is AdvancedTestStatus.Failed or AdvancedTestStatus.Partial) == true;
            if (_lastRunResult is not null)
            {
                await RecordRunHistoryAsync(_lastRunResult, DateTime.Now - _testStartTime, cancelled: true);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
            AddRunLog("ERROR", StatusText);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsStopping = false;
            IsTesting = false;
            OnPropertyChanged(nameof(CanStopTest));
        }
    }

    [RelayCommand]
    private void StopTest()
    {
        if (!IsTesting || IsStopping)
        {
            return;
        }

        if (_cts is null)
        {
            StatusText = "当前没有可停止的安全测试";
            AddRunLog("WARN", StatusText);
            return;
        }

        IsStopping = true;
        StatusText = "正在停止安全测试...";
        AddRunLog("WARN", StatusText);
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        if (IsTesting)
        {
            return;
        }

        var failedIds = _lastRunResult?.Results
            .Where(static item => item.Status is AdvancedTestStatus.Failed or AdvancedTestStatus.Partial)
            .Select(static item => item.TestId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (failedIds.Length == 0)
        {
            StatusText = "没有需要重试的失败项";
            AddRunLog("INFO", StatusText);
            return;
        }

        ValidationError = "";
        await RunTestIdsAsync(failedIds, $"正在重试 {failedIds.Length} 个失败项...");
    }

    [RelayCommand]
    private void EnableAllSuites()
    {
        SetAllSuitesEnabled(true);
        StatusText = "已启用全部测试项";
    }

    [RelayCommand]
    private void DisableAllSuites()
    {
        SetAllSuitesEnabled(false);
        StatusText = "已清空测试项选择";
    }

    [RelayCommand]
    private void ExpandAllSuites()
    {
        foreach (var suite in TestSuites)
        {
            suite.IsExpanded = true;
        }
    }

    [RelayCommand]
    private void CollapseAllSuites()
    {
        foreach (var suite in TestSuites)
        {
            suite.IsExpanded = false;
        }
    }

    private void SetAllSuitesEnabled(bool isEnabled)
    {
        foreach (var suite in TestSuites)
        {
            suite.IsEnabled = isEnabled;
            foreach (var testCase in suite.Cases)
            {
                testCase.IsEnabled = isEnabled;
            }
        }

        OnPropertyChanged(nameof(SuiteEnablementSummary));
    }

    [RelayCommand]
    private void CopyDiagnosticSummary()
    {
        if (_lastRunResult is null)
        {
            StatusText = "没有可复制的诊断摘要";
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(BuildDiagnosticSummary(_lastRunResult));
        Clipboard.SetContent(dataPackage);
            StatusText = "诊断摘要已复制到剪贴板";
    }

    // ========== 接口History ==========
}
