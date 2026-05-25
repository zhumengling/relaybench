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
    private void UpdateLiveChartsFromResults()
    {
        var matrix = BuildRiskMatrixFromResults(Results);
        UpdateRiskMatrix(matrix);

        var scoreValues = _historicalScores.ToList();
        var passValues = _historicalPassRates.ToList();
        if (Results.Count > 0)
        {
            scoreValues.Add(Math.Clamp(SafetyScore, 0, 100));
            passValues.Add(CompletedScenarios > 0 ? (double)PassedScenarios / CompletedScenarios * 100 : 0);
        }

        UpdateTrendChart(scoreValues, passValues);
    }

    private static int[,] BuildRiskMatrixFromResults(IEnumerable<SafetyTestResult> results)
    {
        var matrix = new int[4, 4];
        foreach (var result in results)
        {
            var severity = result.IsPassed ? 0 : RiskLevelToMatrixIndex(result.RiskLevel);
            var likelihood = RiskScoreToMatrixIndex(result.RiskScore);
            matrix[severity, likelihood]++;
        }

        return matrix;
    }

    private void UpdateRiskMatrix(int[,] matrix)
    {
        if (RiskMatrixCells.Count != 16)
        {
            InitializeChartState();
        }

        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var index = row * 4 + column;
                RiskMatrixCells[index].Count = matrix[row, column];
            }
        }
    }

    private void UpdateTrendChart(IReadOnlyList<double> scores, IReadOnlyList<double> passRates)
    {
        try
        {
            ScoreTrendPoints = BuildTrendPoints(scores);
            PassTrendPoints = BuildTrendPoints(passRates);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ScoreTrendPoints = null;
            PassTrendPoints = null;
        }
    }

    private static PointCollection BuildTrendPoints(IReadOnlyList<double> values)
    {
        var normalized = values.Count == 0
            ? Enumerable.Repeat(0d, 8).ToArray()
            : values.TakeLast(14).ToArray();

        if (normalized.Length == 1)
        {
            normalized = [normalized[0], normalized[0]];
        }

        const double width = 312;
        const double height = 132;
        const double topPadding = 18;
        var points = new PointCollection();
        var step = normalized.Length <= 1 ? width : width / (normalized.Length - 1);

        for (var i = 0; i < normalized.Length; i++)
        {
            var value = Math.Clamp(normalized[i], 0, 100);
            var x = i * step;
            var y = topPadding + (100 - value) / 100d * (height - topPadding);
            points.Add(new Point(x, y));
        }

        return points;
    }

    private static bool TryReadSafetyHistory(
        string? payloadJson,
        out int[,] matrix,
        out int total,
        out int passed,
        out int failed,
        out double passRate)
    {
        matrix = new int[4, 4];
        total = 0;
        passed = 0;
        failed = 0;
        passRate = 0;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!TryGetProperty(document.RootElement, "Results", "results", out var results))
            {
                return false;
            }

            foreach (var item in results.EnumerateArray())
            {
                total++;
                var status = ReadString(item, "Status", "status");
                var score = ReadDouble(item, "Score", "score");
                var riskLevel = ReadString(item, "RiskLevel", "riskLevel");
                var isPassed = string.Equals(status, nameof(AdvancedTestStatus.Passed), StringComparison.OrdinalIgnoreCase);
                if (isPassed)
                {
                    passed++;
                }
                else if (!string.Equals(status, nameof(AdvancedTestStatus.Skipped), StringComparison.OrdinalIgnoreCase))
                {
                    failed++;
                }

                var severity = isPassed ? 0 : RiskLevelToMatrixIndex(riskLevel);
                var likelihood = RiskScoreToMatrixIndex((int)Math.Round(100 - score));
                matrix[severity, likelihood]++;
            }

            passRate = total > 0 ? passed * 100d / total : 0;
            return total > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string pascalName, string camelName, out JsonElement value)
        => element.TryGetProperty(pascalName, out value) || element.TryGetProperty(camelName, out value);

    private static string ReadString(JsonElement element, string pascalName, string camelName)
        => TryGetProperty(element, pascalName, camelName, out var value) ? value.ToString() : string.Empty;

    private static double ReadDouble(JsonElement element, string pascalName, string camelName)
        => TryGetProperty(element, pascalName, camelName, out var value) && value.TryGetDouble(out var number)
            ? number
            : 0;

    private static int RiskLevelToMatrixIndex(AdvancedRiskLevel riskLevel) => riskLevel switch
    {
        AdvancedRiskLevel.Critical => 3,
        AdvancedRiskLevel.High => 2,
        AdvancedRiskLevel.Medium => 1,
        _ => 0
    };

    private static int RiskLevelToMatrixIndex(string riskLevel)
        => Enum.TryParse<AdvancedRiskLevel>(riskLevel, ignoreCase: true, out var parsed)
            ? RiskLevelToMatrixIndex(parsed)
            : 0;

    private static int RiskScoreToMatrixIndex(int riskScore)
    {
        var normalized = Math.Clamp(riskScore, 0, 100);
        return normalized switch
        {
            >= 76 => 3,
            >= 51 => 2,
            >= 26 => 1,
            _ => 0
        };
    }

    private static RiskMatrixCellTone ResolveRiskMatrixTone(int severity, int likelihood)
        => (severity * likelihood) switch
        {
            >= 6 => RiskMatrixCellTone.High,
            >= 3 => RiskMatrixCellTone.Medium,
            _ => RiskMatrixCellTone.Low
        };

    /// <summary>
    /// Formats a TimeSpan as a human-readable duration string.
    /// </summary>
    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private void AddRunLog(string level, string message)
    {
        var normalized = message?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        RunLogs.Insert(0, new SafetyRunLogEntry(DateTimeOffset.Now, level, normalized));
        while (RunLogs.Count > 24)
        {
            RunLogs.RemoveAt(RunLogs.Count - 1);
        }

        OnPropertyChanged(nameof(NoRunLogs));
        OnPropertyChanged(nameof(RunLogCount));
    }

    private void ClearRunLogs()
    {
        RunLogs.Clear();
        OnPropertyChanged(nameof(NoRunLogs));
        OnPropertyChanged(nameof(RunLogCount));
    }

    private void ClearResultState()
    {
        CompletedScenarios = 0;
        PassedScenarios = 0;
        FailedScenarios = 0;
        SafetyScore = 0;
        OverallScore = 0;
        CodexScore = 0;
        AgentScore = 0;
        RagScore = 0;
        ChatScore = 0;
        PassRate = "0.0%";
        TestDuration = "0s";
        Results.Clear();
        CategoryScores.Clear();
        RecentFailures.Clear();
        ClearRunLogs();
        TimelineItems.Clear();
        SelectedResult = null;
        CanExport = false;
        CanRetryFailed = false;
        ResetRiskDistribution();
        ApplyProgressSummary(0, 0, 0);
        UpdateRiskMatrix(new int[4, 4]);
        UpdateTrendChart(_historicalScores, _historicalPassRates);
        OnPropertyChanged(nameof(NoRecentFailures));
        OnPropertyChanged(nameof(RunStateText));
    }
}
