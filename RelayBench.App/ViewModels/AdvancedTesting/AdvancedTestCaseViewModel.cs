using RelayBench.App.Infrastructure;
using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.App.ViewModels.AdvancedTesting;

public sealed class AdvancedTestCaseViewModel : ObservableObject
{
    private bool _isSelected;
    private AdvancedTestStatus _status = AdvancedTestStatus.Queued;
    private double _score;
    private string _durationText = "-";
    private string _summary = "等待运行";
    private string _rawRequest = string.Empty;
    private string _rawResponse = string.Empty;
    private string _errorDetail = string.Empty;
    private AdvancedRiskLevel _riskLevel;

    public AdvancedTestCaseViewModel(AdvancedTestCaseDefinition definition)
    {
        Definition = definition;
        _isSelected = definition.IsEnabledByDefault;
    }

    public AdvancedTestCaseDefinition Definition { get; }

    public string TestId => Definition.TestId;

    public string DisplayName => Definition.DisplayName;

    public string Description => Definition.Description;

    public string CategoryText => ToCategoryText(Definition.Category);

    public double Weight => Definition.Weight;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public AdvancedTestStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusForeground));
                OnPropertyChanged(nameof(HasRawExchange));
                OnPropertyChanged(nameof(CanOpenError));
            }
        }
    }

    public string StatusText => ToStatusText(Status);

    public string StatusBrush
        => Status switch
        {
            AdvancedTestStatus.Running => "#DBEAFE",
            AdvancedTestStatus.Passed => "#DCFCE7",
            AdvancedTestStatus.Partial => "#FEF3C7",
            AdvancedTestStatus.Failed => "#FEE2E2",
            AdvancedTestStatus.Stopped => "#F1F5F9",
            AdvancedTestStatus.Skipped => "#F8FAFC",
            _ => "#EEF2FF"
        };

    public string StatusForeground
        => Status switch
        {
            AdvancedTestStatus.Passed => "#047857",
            AdvancedTestStatus.Partial => "#B45309",
            AdvancedTestStatus.Failed => "#B91C1C",
            AdvancedTestStatus.Running => "#1D4ED8",
            _ => "#475569"
        };

    public double Score
    {
        get => _score;
        private set
        {
            if (SetProperty(ref _score, Math.Round(value, 1)))
            {
                OnPropertyChanged(nameof(ScoreText));
            }
        }
    }

    public string ScoreText => Status is AdvancedTestStatus.Queued or AdvancedTestStatus.Running ? "-" : Score.ToString("0.0");

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public AdvancedRiskLevel RiskLevel
    {
        get => _riskLevel;
        private set
        {
            if (SetProperty(ref _riskLevel, value))
            {
                OnPropertyChanged(nameof(RiskText));
                OnPropertyChanged(nameof(RiskBrush));
            }
        }
    }

    public string RiskText
        => RiskLevel switch
        {
            AdvancedRiskLevel.Critical => "严重",
            AdvancedRiskLevel.High => "高",
            AdvancedRiskLevel.Medium => "中",
            _ => "低"
        };

    public string RiskBrush
        => RiskLevel switch
        {
            AdvancedRiskLevel.Critical => "#7F1D1D",
            AdvancedRiskLevel.High => "#DC2626",
            AdvancedRiskLevel.Medium => "#D97706",
            _ => "#059669"
        };

    public string RawRequest
    {
        get => _rawRequest;
        private set
        {
            if (SetProperty(ref _rawRequest, value))
            {
                OnPropertyChanged(nameof(HasRawExchange));
            }
        }
    }

    public string RawResponse
    {
        get => _rawResponse;
        private set
        {
            if (SetProperty(ref _rawResponse, value))
            {
                OnPropertyChanged(nameof(HasRawExchange));
            }
        }
    }

    public string ErrorDetail
    {
        get => _errorDetail;
        private set
        {
            if (SetProperty(ref _errorDetail, value))
            {
                OnPropertyChanged(nameof(CanOpenError));
            }
        }
    }

    public bool HasRawExchange => !string.IsNullOrWhiteSpace(RawRequest) || !string.IsNullOrWhiteSpace(RawResponse);

    public bool CanOpenError => Status is AdvancedTestStatus.Failed or AdvancedTestStatus.Partial && !string.IsNullOrWhiteSpace(ErrorDetail);

    public void MarkRunning()
    {
        Status = AdvancedTestStatus.Running;
        Summary = "正在运行...";
        DurationText = "-";
    }

    public void ResetForRun()
    {
        Status = AdvancedTestStatus.Queued;
        Score = 0;
        DurationText = "-";
        Summary = "排队中";
        RiskLevel = AdvancedRiskLevel.Low;
        RawRequest = string.Empty;
        RawResponse = string.Empty;
        ErrorDetail = string.Empty;
    }

    public void ApplyResult(AdvancedTestCaseResult result)
    {
        Status = result.Status;
        Score = result.Score;
        DurationText = result.Duration.TotalMilliseconds <= 0 ? "-" : $"{result.Duration.TotalMilliseconds:0} ms";
        Summary = result.ResponseSummary;
        RiskLevel = result.RiskLevel;
        RawRequest = result.RawRequest ?? string.Empty;
        RawResponse = result.RawResponse ?? string.Empty;
        ErrorDetail = BuildErrorDetail(result);
    }

    private static string BuildErrorDetail(AdvancedTestCaseResult result)
    {
        List<string> lines =
        [
            $"结论: {ToStatusText(result.Status)}",
            $"探针: {result.DisplayName} ({result.TestId})",
            $"风险: {result.RiskLevel}",
            $"耗时: {(result.Duration.TotalMilliseconds <= 0 ? "-" : $"{result.Duration.TotalMilliseconds:0} ms")}",
            string.Empty,
            "为什么失败或需要复核:"
        ];

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            lines.Add($"- {result.ErrorMessage}");
        }

        foreach (var check in result.Checks.Where(static item => !item.Passed))
        {
            lines.Add($"- {check.Name} 未通过：期望 {check.Expected}，实际 {check.Actual}。{check.Detail}");
        }

        lines.Add(string.Empty);
        lines.Add("建议:");
        foreach (var suggestion in result.Suggestions)
        {
            lines.Add($"- {suggestion}");
        }

        lines.Add(string.Empty);
        lines.Add("自动检查:");
        foreach (var check in result.Checks)
        {
            lines.Add($"- {(check.Passed ? "通过" : "失败")} · {check.Name}");
            lines.Add($"  期望: {check.Expected}");
            lines.Add($"  实际: {check.Actual}");
            lines.Add($"  说明: {check.Detail}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ToCategoryText(AdvancedTestCategory category)
        => category switch
        {
            AdvancedTestCategory.BasicCompatibility => "基础兼容",
            AdvancedTestCategory.AgentCompatibility => "Agent",
            AdvancedTestCategory.StructuredOutput => "JSON",
            AdvancedTestCategory.ReasoningCompatibility => "Reasoning",
            AdvancedTestCategory.LongContext => "长上下文",
            AdvancedTestCategory.Stability => "稳定性",
            AdvancedTestCategory.Concurrency => "并发",
            AdvancedTestCategory.Rag => "RAG",
            AdvancedTestCategory.ModelConsistency => "模型风险",
            AdvancedTestCategory.SecurityRedTeam => "安全红队",
            _ => category.ToString()
        };

    private static string ToStatusText(AdvancedTestStatus status)
        => status switch
        {
            AdvancedTestStatus.Queued => "排队",
            AdvancedTestStatus.Running => "运行中",
            AdvancedTestStatus.Passed => "通过",
            AdvancedTestStatus.Partial => "部分通过",
            AdvancedTestStatus.Failed => "失败",
            AdvancedTestStatus.Skipped => "跳过",
            AdvancedTestStatus.Stopped => "已停止",
            _ => status.ToString()
        };
}
