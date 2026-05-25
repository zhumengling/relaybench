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
    private async Task RecordRunHistoryAsync(AdvancedTestRunResult result, TimeSpan elapsed, bool cancelled)
    {
        try
        {
            await RunHistoryRecorder.RecordAsync(
                "数据安全",
                BaseUrl.Trim(),
                $"{(cancelled ? "安全测试已Cancel" : "安全测试完成")}: {result.Scores.Overall:F1}/100，{PassedScenarios}/{TotalScenarios} 通过",
                result.Scores.Overall,
                (int)elapsed.TotalMilliseconds,
                _reportExporter.BuildJson(result));
        }
        catch
        {
            // Do not fail the safety workflow if history persistence fails.
        }
    }

    [RelayCommand]
    private async Task ExportSafetyReportAsync()
    {
        if (_lastRunResult is null)
        {
            StatusText = "没有可导出的结果";
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            // Get the window handle for WinUI 3
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.SuggestedFileName = $"DataSafety-Report-{DateTime.Now:yyyyMMdd-HHmmss}";
            picker.FileTypeChoices.Add("Markdown", new[] { ".md" });
            picker.FileTypeChoices.Add("JSON", new[] { ".json" });

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            string content;
            if (file.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                content = _reportExporter.BuildJson(_lastRunResult);
            }
            else
            {
                content = _reportExporter.BuildMarkdown(_lastRunResult);
            }

            await Windows.Storage.FileIO.WriteTextAsync(file, content);
            StatusText = $"报告已导出: {file.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles progress reports from the AdvancedTestRunner.
    /// Updates timeline items and results as each test case completes.
    /// </summary>
    private void OnTestProgress(AdvancedTestProgress progress)
    {
        if (progress.Result is { } caseResult)
        {
            // A test case completed
            var index = CompletedScenarios;
            CompletedScenarios++;

            var passed = caseResult.Status == AdvancedTestStatus.Passed;
            var failed = caseResult.Status == AdvancedTestStatus.Failed;

            if (passed) PassedScenarios++;
            if (failed) FailedScenarios++;

            // Update timeline
            if (index < TimelineItems.Count)
            {
                TimelineItems[index].Status = passed
                    ? ScenarioStatus.Passed
                    : failed
                        ? ScenarioStatus.Failed
                        : ScenarioStatus.Failed; // Partial/Stopped treated as fail for timeline
            }

            // Map category to display name
            var categoryName = MapCategoryToDisplayName(caseResult.Category);

            // Risk score: use the test case score (0-100, higher = better)
            // Invert for risk display: 100 - score = risk
            var riskScore = (int)Math.Round(100 - caseResult.Score);

            var resultItem = new SafetyTestResult(
                caseResult.TestId,
                caseResult.DisplayName,
                categoryName,
                passed,
                caseResult.ResponseSummary.Length > 200
                    ? string.Concat(caseResult.ResponseSummary.AsSpan(0, 200), "...")
                    : caseResult.ResponseSummary,
                riskScore,
                caseResult.RiskLevel,
                caseResult.Status,
                caseResult.RequestSummary,
                caseResult.RawRequest,
                caseResult.RawResponse,
                caseResult.ErrorMessage,
                caseResult.Suggestions,
                caseResult.Checks);
            Results.Add(resultItem);
            SelectedResult ??= resultItem;
            UpdateRiskDistribution(resultItem);
            UpdateCategoryScoresFromResults();
            AddRunLog(
                passed ? "PASS" : failed ? "FAIL" : "WARN",
                $"{resultItem.ScenarioName}: {resultItem.ResultText} · {resultItem.Category}");

            // Track recent failures (keep last 3)
            if (failed)
            {
                if (RecentFailures.Count >= 3)
                    RecentFailures.RemoveAt(0);
                RecentFailures.Add(resultItem);
                SelectedResult = resultItem;
                OnPropertyChanged(nameof(NoRecentFailures));
            }

            PassRate = CompletedScenarios > 0
                ? $"{(double)PassedScenarios / CompletedScenarios * 100:F1}%"
                : "0.0%";
            SafetyScore = Results.Count > 0
                ? Math.Round(Results.Average(static item => 100 - item.RiskScore), 1)
                : 0;
            ApplyProgressSummary(TotalScenarios, PassedScenarios, FailedScenarios);
            UpdateLiveChartsFromResults();
            StatusText = $"正在测试... {CompletedScenarios}/{TotalScenarios}";
        }
        else if (progress.Status == AdvancedTestStatus.Running)
        {
            // Mark next item as in-progress
            var nextIndex = CompletedScenarios;
            if (nextIndex < TimelineItems.Count)
            {
                TimelineItems[nextIndex].Status = ScenarioStatus.InProgress;
            }
            StatusText = progress.Message;
            AddRunLog("INFO", StatusText);
        }
    }

    /// <summary>
    /// Maps enabled UI test suite names to real AdvancedTestRunner test IDs.
    /// The UI suites map to the security-red-team suite in the AdvancedTestCatalog.
    /// </summary>
    private List<string> MapEnabledSuitesToTestIds(List<TestSuiteItem> enabledSuites)
    {
        var testIds = new List<string>();

        foreach (var suite in enabledSuites)
        {
            if (suite.Cases.Count > 0)
            {
                testIds.AddRange(suite.Cases
                    .Where(static testCase => testCase.IsEnabled)
                    .Select(static testCase => testCase.TestId));
                continue;
            }

            switch (suite.Name)
            {
                case "Prompt Injection":
                    testIds.Add("redteam_prompt_injection");
                    break;
                case "Jailbreak":
                    testIds.Add("redteam_jailbreak_boundary");
                    break;
                case "PII Leak":
                    testIds.Add("redteam_privacy_echo");
                    break;
                case "Content Compliance":
                    testIds.Add("redteam_system_prompt_leak");
                    break;
                case "Role Play":
                    testIds.Add("redteam_tool_overreach");
                    break;
                case "Multi-Language Bypass":
                    testIds.Add("redteam_rag_poisoning");
                    break;
                case "Encoding Obfuscation":
                    testIds.Add("redteam_malicious_url_command");
                    break;
            }
        }

        return testIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void InitializeCategoryScores(IReadOnlyList<string> selectedTestIds)
    {
        _plannedCategoryTotals = ResolveTestDefinitions(selectedTestIds)
            .GroupBy(static item => item.Category)
            .ToDictionary(
                group => MapCategoryToDisplayName(group.Key),
                group => group.Count(),
                StringComparer.Ordinal);

        UpdateCategoryScoresFromResults();
    }

    private IEnumerable<AdvancedTestCaseDefinition> ResolveTestDefinitions(IReadOnlyList<string> selectedTestIds)
    {
        var selected = selectedTestIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _testRunner.Suites
            .SelectMany(static suite => suite.Cases)
            .Where(testCase => selected.Contains(testCase.TestId));
    }

    private void UpdateCategoryScoresFromResults()
    {
        var resultGroups = Results
            .GroupBy(static item => item.Category)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var categories = _plannedCategoryTotals.Keys
            .Concat(resultGroups.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CategoryScores.Clear();
        foreach (var categoryName in categories)
        {
            resultGroups.TryGetValue(categoryName, out var groupResults);
            groupResults ??= [];
            var total = _plannedCategoryTotals.TryGetValue(categoryName, out var plannedTotal)
                ? Math.Max(plannedTotal, groupResults.Length)
                : groupResults.Length;
            var passed = groupResults.Count(static item => item.Status == AdvancedTestStatus.Passed);
            var failed = groupResults.Count(static item => item.Status == AdvancedTestStatus.Failed);
            var partial = groupResults.Count(static item => item.Status == AdvancedTestStatus.Partial);
            var avgScore = groupResults.Length == 0
                ? 0
                : groupResults.Average(static item => 100 - item.RiskScore);

            CategoryScores.Add(new CategoryScoreItem(
                categoryName,
                passed,
                failed,
                partial,
                total,
                avgScore,
                groupResults.Length));
        }
    }

    /// <summary>
    /// Builds per-category pass/fail breakdown from the run result.
    /// </summary>
    private void BuildCategoryScores(AdvancedTestRunResult result)
    {
        CategoryScores.Clear();

        var grouped = result.Results
            .GroupBy(r => r.Category)
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            var categoryName = MapCategoryToDisplayName(group.Key);
            var passed = group.Count(r => r.Status == AdvancedTestStatus.Passed);
            var total = group.Count();
            var failed = group.Count(r => r.Status == AdvancedTestStatus.Failed);
            var partial = group.Count(r => r.Status == AdvancedTestStatus.Partial);
            var avgScore = group.Average(r => r.Score);

            CategoryScores.Add(new CategoryScoreItem(
                categoryName,
                passed,
                failed,
                partial,
                total,
                avgScore,
                total));
        }
    }

    private void ResetRiskDistribution()
    {
        HighRiskCount = 0;
        MediumRiskCount = 0;
        LowRiskCount = 0;
        PassedResultCount = 0;
        SkippedResultCount = 0;
        RefreshRiskDistributionItems();
        OnPropertyChanged(nameof(RiskDistributionSummary));
    }

    private void UpdateRiskDistribution(SafetyTestResult result)
    {
        if (result.Status == AdvancedTestStatus.Skipped)
        {
            SkippedResultCount++;
        }
        else if (result.IsPassed)
        {
            PassedResultCount++;
        }
        else
        {
            switch (result.RiskLevel)
            {
                case AdvancedRiskLevel.Critical:
                case AdvancedRiskLevel.High:
                    HighRiskCount++;
                    break;
                case AdvancedRiskLevel.Medium:
                    MediumRiskCount++;
                    break;
                default:
                    LowRiskCount++;
                    break;
            }
        }

        OnPropertyChanged(nameof(RiskDistributionSummary));
        RefreshRiskDistributionItems();
    }

    private void ApplyRiskDistributionCounts(int high, int medium, int low, int passed, int skipped)
    {
        HighRiskCount = Math.Max(0, high);
        MediumRiskCount = Math.Max(0, medium);
        LowRiskCount = Math.Max(0, low);
        PassedResultCount = Math.Max(0, passed);
        SkippedResultCount = Math.Max(0, skipped);
        RefreshRiskDistributionItems();
        OnPropertyChanged(nameof(RiskDistributionSummary));
    }

    private void ApplyHistoricalRiskDistribution(int[,] matrix, int passed, int failed)
    {
        var high = matrix[2, 0] + matrix[2, 1] + matrix[2, 2] + matrix[2, 3] +
                   matrix[3, 0] + matrix[3, 1] + matrix[3, 2] + matrix[3, 3];
        var medium = matrix[1, 0] + matrix[1, 1] + matrix[1, 2] + matrix[1, 3];
        var low = Math.Max(0, failed - high - medium);
        ApplyRiskDistributionCounts(high, medium, low, passed, 0);
    }

    private void RefreshRiskDistributionItems()
    {
        var total = HighRiskCount + MediumRiskCount + LowRiskCount + PassedResultCount + SkippedResultCount;
        RiskDistributionItems.Clear();
        RiskDistributionItems.Add(BuildRiskDistributionItem("高风险", HighRiskCount, total, RiskDistributionTone.High));
        RiskDistributionItems.Add(BuildRiskDistributionItem("中风险", MediumRiskCount, total, RiskDistributionTone.Medium));
        RiskDistributionItems.Add(BuildRiskDistributionItem("低风险", LowRiskCount, total, RiskDistributionTone.Low));
        RiskDistributionItems.Add(BuildRiskDistributionItem("通过", PassedResultCount, total, RiskDistributionTone.Passed));
        RiskDistributionItems.Add(BuildRiskDistributionItem("跳过", SkippedResultCount, total, RiskDistributionTone.Skipped));
    }

    private static RiskDistributionItem BuildRiskDistributionItem(string label, int count, int total, RiskDistributionTone tone)
    {
        var percent = total > 0 ? count * 100d / total : 0;
        return new RiskDistributionItem(
            label,
            count,
            $"{percent:0.0}%",
            Math.Max(count > 0 ? 14 : 0, percent * 1.36),
            tone);
    }

    private string BuildSelectedEvidenceOutput()
    {
        if (SelectedResult is not { } result)
        {
            return "选择一条测试结果后，会在这里显示脱敏后的原始请求、响应、判定依据和建议。";
        }

        return SelectedEvidenceTabIndex switch
        {
            1 => result.CheckEvidenceText,
            2 => result.RequestLogText,
            3 => result.SanitizedLogText,
            _ => result.DetailText
        };
    }

    private static string BuildDiagnosticSummary(AdvancedTestRunResult result)
    {
        var failed = result.Results
            .Where(static item => item.Status is AdvancedTestStatus.Failed or AdvancedTestStatus.Partial)
            .ToArray();
        var lines = new List<string>
        {
            "RelayBench 数据安全诊断摘要",
            $"接口：{result.Endpoint.BaseUrl}",
            $"模型：{result.Endpoint.Model}",
            $"总分：{result.Scores.Overall:F1}/100",
            $"通过/失败/部分/跳过：{result.PassedCount}/{result.FailedCount}/{result.PartialCount}/{result.SkippedCount}",
            $"开始：{result.StartedAt:yyyy-MM-dd HH:mm:ss}",
            $"完成：{result.CompletedAt:yyyy-MM-dd HH:mm:ss}"
        };

        if (failed.Length > 0)
        {
            lines.Add("");
            lines.Add("需要关注的失败项：");
            foreach (var item in failed.Take(8))
            {
                lines.Add($"- {item.DisplayName}：{item.ErrorMessage}；建议：{string.Join("；", item.Suggestions.Take(2))}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Maps AdvancedTestCategory enum to a user-friendly display name.
    /// </summary>
    private static string MapCategoryToDisplayName(AdvancedTestCategory category) => category switch
    {
        AdvancedTestCategory.BasicCompatibility => "基础兼容",
        AdvancedTestCategory.AgentCompatibility => "Agent 兼容",
        AdvancedTestCategory.StructuredOutput => "结构化输出",
        AdvancedTestCategory.ReasoningCompatibility => "推理兼容",
        AdvancedTestCategory.LongContext => "长上下文",
        AdvancedTestCategory.Stability => "稳定性",
        AdvancedTestCategory.Concurrency => "并发",
        AdvancedTestCategory.Rag => "RAG",
        AdvancedTestCategory.ModelConsistency => "模型一致性",
        AdvancedTestCategory.SecurityRedTeam => "安全红队",
        _ => category.ToString()
    };

    /// <summary>
    /// Keeps the legacy individual toggle properties in sync with the TestSuites collection.
    /// </summary>
    private void SyncTogglePropertiesFromSuites()
    {
        var enabledIds = MapEnabledSuitesToTestIds(TestSuites.Where(static suite => suite.IsEnabled).ToList())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        PromptInjectionEnabled = enabledIds.Contains("redteam_prompt_injection");
        JailbreakEnabled = enabledIds.Contains("redteam_jailbreak_boundary");
        PiiLeakEnabled = enabledIds.Contains("redteam_privacy_echo");
        ContentComplianceEnabled = enabledIds.Contains("redteam_system_prompt_leak");
        RolePlayEnabled = enabledIds.Contains("redteam_tool_overreach");
        MultiLangBypassEnabled = enabledIds.Contains("redteam_rag_poisoning");
        EncodingObfuscationEnabled = enabledIds.Contains("redteam_malicious_url_command");
    }

    private string NormalizeBaseUrl(string baseUrl)
    {
        var normalized = baseUrl.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = ProtocolPrefix + normalized;
        }

        return normalized;
    }

    private string? GetPreferredWireApi() => PreferredWireApiIndex switch
    {
        1 => ProxyWireApiProbeService.ChatCompletionsWireApi,
        2 => ProxyWireApiProbeService.ResponsesWireApi,
        3 => ProxyWireApiProbeService.AnthropicMessagesWireApi,
        _ => null
    };

    private void ApplyProgressSummary(int total, int passed, int failed)
    {
        DisplayTotalScenarios = Math.Max(0, total);
        DisplayPassedScenarios = Math.Max(0, passed);
        DisplayFailedScenarios = Math.Max(0, failed);
    }

}
