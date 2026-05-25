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

public sealed partial class RiskMatrixCell : ObservableObject
{
    [ObservableProperty] public partial int Count { get; set; }

    public RiskMatrixCell(int count, RiskMatrixCellTone tone)
    {
        Count = count;
        Tone = tone;
    }

    public RiskMatrixCellTone Tone { get; }

    public Visibility LowToneVisibility => Tone == RiskMatrixCellTone.Low ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MediumToneVisibility => Tone == RiskMatrixCellTone.Medium ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HighToneVisibility => Tone == RiskMatrixCellTone.High ? Visibility.Visible : Visibility.Collapsed;
}

public enum RiskMatrixCellTone
{
    Low,
    Medium,
    High
}

public sealed record RiskDistributionItem(
    string Label,
    int Count,
    string PercentText,
    double BarWidth,
    RiskDistributionTone Tone)
{
    public Visibility HighToneVisibility => Tone == RiskDistributionTone.High ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MediumToneVisibility => Tone == RiskDistributionTone.Medium ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LowToneVisibility => Tone == RiskDistributionTone.Low ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PassedToneVisibility => Tone == RiskDistributionTone.Passed ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SkippedToneVisibility => Tone == RiskDistributionTone.Skipped ? Visibility.Visible : Visibility.Collapsed;
}

public enum RiskDistributionTone
{
    High,
    Medium,
    Low,
    Passed,
    Skipped
}

public sealed record SafetyRunLogEntry(DateTimeOffset Time, string Level, string Message)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");

    public string LevelText => string.IsNullOrWhiteSpace(Level) ? "INFO" : Level.Trim().ToUpperInvariant();

    public string DisplayMessage => string.IsNullOrWhiteSpace(Message) ? "-" : Message.Trim();

    public Visibility PassLevelVisibility => LevelText == "PASS" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ErrorLevelVisibility => LevelText is "FAIL" or "ERROR" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WarnLevelVisibility => LevelText == "WARN" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InfoLevelVisibility => LevelText is not "PASS" and not "FAIL" and not "ERROR" and not "WARN"
        ? Visibility.Visible
        : Visibility.Collapsed;
}

public sealed record ProtocolPriorityItem(
    string Protocol,
    string RequestPath,
    string RankText,
    string WeightText,
    double BarWidth,
    string StatusText,
    ProtocolPriorityStatusTone StatusTone)
{
    public Visibility DangerStatusVisibility => StatusTone == ProtocolPriorityStatusTone.Danger ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MutedStatusVisibility => StatusTone == ProtocolPriorityStatusTone.Muted ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AccentStatusVisibility => StatusTone == ProtocolPriorityStatusTone.Accent ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HealthyStatusVisibility => StatusTone == ProtocolPriorityStatusTone.Healthy ? Visibility.Visible : Visibility.Collapsed;
}

public enum ProtocolPriorityStatusTone
{
    Danger,
    Muted,
    Accent,
    Healthy
}

/// <summary>
/// Represents a single safety test result row in the ResultsTable.
/// Columns: scenario name, category, result (pass/fail), response excerpt, risk score 0-100.
/// </summary>
public sealed partial class SafetyTestResult : ObservableObject
{
    public SafetyTestResult(
        string scenarioId,
        string scenarioName,
        string category,
        bool isPassed,
        string responseExcerpt,
        int riskScore,
        AdvancedRiskLevel riskLevel,
        AdvancedTestStatus status,
        string requestSummary,
        string? rawRequest,
        string? rawResponse,
        string errorMessage,
        IReadOnlyList<string> suggestions,
        IReadOnlyList<AdvancedCheckResult> checks)
    {
        ScenarioId = scenarioId;
        ScenarioName = scenarioName;
        Category = category;
        IsPassed = isPassed;
        ResponseExcerpt = responseExcerpt.Length > 200
            ? string.Concat(responseExcerpt.AsSpan(0, 200), "...")
            : responseExcerpt;
        RiskScore = Math.Clamp(riskScore, 0, 100);
        RiskLevel = riskLevel;
        Status = status;
        RequestSummary = requestSummary;
        RawRequest = rawRequest ?? string.Empty;
        RawResponse = rawResponse ?? string.Empty;
        ErrorMessage = errorMessage;
        Suggestions = suggestions;
        Checks = checks;
        CompletedAt = DateTime.Now;
    }

    public string ScenarioId { get; }
    public string ScenarioName { get; }
    public string Category { get; }
    public bool IsPassed { get; }
    public string ResponseExcerpt { get; }
    public int RiskScore { get; }
    public AdvancedRiskLevel RiskLevel { get; }
    public AdvancedTestStatus Status { get; }
    public string RequestSummary { get; }
    public string RawRequest { get; }
    public string RawResponse { get; }
    public string ErrorMessage { get; }
    public IReadOnlyList<string> Suggestions { get; }
    public IReadOnlyList<AdvancedCheckResult> Checks { get; }
    public DateTime CompletedAt { get; }
    public string CompletedAtText => CompletedAt.ToString("HH:mm:ss");
    public string PrimarySuggestion
        => Suggestions.Count > 0
            ? Suggestions[0]
            : string.IsNullOrWhiteSpace(ErrorMessage)
                ? "保持观察"
                : ErrorMessage;

    /// <summary>Display text for the result column.</summary>
    public string ResultText => Status switch
    {
        AdvancedTestStatus.Passed => "通过",
        AdvancedTestStatus.Partial => "部分通过",
        AdvancedTestStatus.Skipped => "跳过",
        _ => "失败"
    };

    public string RiskLevelText => RiskLevel switch
    {
        AdvancedRiskLevel.Critical => "严重",
        AdvancedRiskLevel.High => "高",
        AdvancedRiskLevel.Medium => "中",
        AdvancedRiskLevel.Low => "低",
        _ => "低"
    };

    public string ScenarioInfo
        => $"场景 ID        {ScenarioId}{Environment.NewLine}" +
           $"严重性         {RiskLevelText}{Environment.NewLine}" +
           $"状态           {ResultText}{Environment.NewLine}" +
           $"类别           {Category}{Environment.NewLine}" +
           $"风险分         {RiskScore}/100";

    public Visibility HighRiskVisibility => RiskLevel is AdvancedRiskLevel.Critical or AdvancedRiskLevel.High
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MediumRiskVisibility => RiskLevel == AdvancedRiskLevel.Medium
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LowRiskVisibility => RiskLevel is not AdvancedRiskLevel.Critical and not AdvancedRiskLevel.High and not AdvancedRiskLevel.Medium
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PassedResultVisibility => Status == AdvancedTestStatus.Passed ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PartialResultVisibility => Status == AdvancedTestStatus.Partial ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SkippedResultVisibility => Status == AdvancedTestStatus.Skipped ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FailedResultVisibility => Status is not AdvancedTestStatus.Passed and not AdvancedTestStatus.Partial and not AdvancedTestStatus.Skipped
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string CheckEvidenceText
    {
        get
        {
            if (Checks.Count == 0)
            {
                return "没有判定项记录。";
            }

            var builder = new System.Text.StringBuilder();
            foreach (var check in Checks)
            {
                builder.AppendLine($"{(check.Passed ? "通过" : "失败")} · {check.Name}");
                builder.AppendLine($"期望：{check.Expected}");
                builder.AppendLine($"实际：{check.Actual}");
                if (!string.IsNullOrWhiteSpace(check.Detail))
                {
                    builder.AppendLine($"说明：{check.Detail}");
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }
    }

    public string RequestLogText
        => string.IsNullOrWhiteSpace(RawRequest)
            ? "没有原始请求记录。"
            : RawRequest;

    public string SanitizedLogText
    {
        get
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("【模型响应】");
            builder.AppendLine(string.IsNullOrWhiteSpace(RawResponse) ? "没有原始响应记录。" : RawResponse);
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                builder.AppendLine();
                builder.AppendLine("【错误】");
                builder.AppendLine(ErrorMessage);
            }

            if (Suggestions.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("【建议】");
                foreach (var suggestion in Suggestions)
                {
                    builder.AppendLine($"- {suggestion}");
                }
            }

            return builder.ToString();
        }
    }

    public string DetailText
    {
        get
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("【请求摘要】");
            builder.AppendLine(string.IsNullOrWhiteSpace(RequestSummary) ? "无请求摘要" : RequestSummary);
            builder.AppendLine();
            builder.AppendLine("【模型响应摘要】");
            builder.AppendLine(string.IsNullOrWhiteSpace(ResponseExcerpt) ? "无响应摘要" : ResponseExcerpt);

            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                builder.AppendLine();
                builder.AppendLine("【错误详情】");
                builder.AppendLine(ErrorMessage);
            }

            if (Checks.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("【判定依据】");
                foreach (var check in Checks)
                {
                    builder.AppendLine($"- {(check.Passed ? "通过" : "失败")} {check.Name}: 期望 {check.Expected}，实际 {check.Actual}。{check.Detail}");
                }
            }

            if (Suggestions.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("【建议】");
                foreach (var suggestion in Suggestions)
                {
                    builder.AppendLine($"- {suggestion}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("【原始请求（已脱敏）】");
            builder.AppendLine(string.IsNullOrWhiteSpace(RawRequest) ? "无原始请求记录" : RawRequest);
            builder.AppendLine();
            builder.AppendLine("【原始响应（已脱敏）】");
            builder.AppendLine(string.IsNullOrWhiteSpace(RawResponse) ? "无原始响应记录" : RawResponse);
            return builder.ToString();
        }
    }
}

/// <summary>
/// Represents per-category pass/fail breakdown for the safety test results.
/// </summary>
public sealed class CategoryScoreItem
{
    public CategoryScoreItem(string categoryName, int passed, int failed, int partial, int total, double averageScore, int completed)
    {
        CategoryName = categoryName;
        Passed = passed;
        Failed = failed;
        Partial = partial;
        Total = total;
        AverageScore = Math.Round(averageScore, 1);
        Completed = completed;
    }

    public string CategoryName { get; }
    public int Passed { get; }
    public int Failed { get; }
    public int Partial { get; }
    public int Total { get; }
    public double AverageScore { get; }
    public int Completed { get; }

    /// <summary>Display string for pass/fail ratio.</summary>
    public string PassFailText => $"{Passed}/{Total}";
    public string CompletionText => $"{Completed}/{Total}";
    public string AverageScoreText => $"{AverageScore:0.0}";
    public string FailureText => Failed + Partial == 0 ? "无失败" : $"失败 {Failed} · 部分 {Partial}";
    public double ScoreBarWidth => Math.Max(0, Math.Min(100, AverageScore)) * 1.22;

    private CategoryScoreTone ScoreTone
    {
        get
        {
            if (Completed == 0 || Total <= 0)
            {
                return CategoryScoreTone.Muted;
            }

            var rate = (double)Passed / Total;
            if (rate >= 0.8)
            {
                return CategoryScoreTone.Healthy;
            }

            return rate >= 0.5 ? CategoryScoreTone.Warning : CategoryScoreTone.Danger;
        }
    }

    public Visibility ScoreMutedToneVisibility => ScoreTone == CategoryScoreTone.Muted ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScoreDangerToneVisibility => ScoreTone == CategoryScoreTone.Danger ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScoreWarningToneVisibility => ScoreTone == CategoryScoreTone.Warning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScoreHealthyToneVisibility => ScoreTone == CategoryScoreTone.Healthy ? Visibility.Visible : Visibility.Collapsed;
}

public enum CategoryScoreTone
{
    Muted,
    Danger,
    Warning,
    Healthy
}
