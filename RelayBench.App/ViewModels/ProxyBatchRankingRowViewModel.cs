using System.Text.RegularExpressions;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class ProxyBatchRankingRowViewModel : ObservableObject
{
    private bool _isSelected;
    private int _rank;
    private string _entryName = string.Empty;
    private string _baseUrl = string.Empty;
    private string _model = string.Empty;
    private string _quickVerdict = "待对比";
    private string _quickMetrics = "--";
    private string _capabilitySummary = "--";
    private string _deepStatus = "未开始";
    private string _deepSummary = string.Empty;
    private string _deepCheckedAt = "--";
    private double _compositeScore;
    private double _stabilityRatio;
    private double? _ttftMs;
    private double? _chatLatencyMs;
    private double? _tokensPerSecond;
    private string _verdict = string.Empty;
    private string _secondaryText = string.Empty;
    private int _runCount;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(CandidateHint));
                OnPropertyChanged(nameof(DeepSelectionText));
            }
        }
    }

    public int Rank
    {
        get => _rank;
        set
        {
            if (SetProperty(ref _rank, value))
            {
                OnPropertyChanged(nameof(RankText));
                OnPropertyChanged(nameof(CandidateHint));
            }
        }
    }

    public string EntryName
    {
        get => _entryName;
        set => SetProperty(ref _entryName, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
            {
                OnPropertyChanged(nameof(ModelDisplay));
                OnPropertyChanged(nameof(CandidateHint));
            }
        }
    }

    public string QuickVerdict
    {
        get => _quickVerdict;
        set => SetProperty(ref _quickVerdict, value);
    }

    public string QuickMetrics
    {
        get => _quickMetrics;
        set => SetProperty(ref _quickMetrics, value);
    }

    public string CapabilitySummary
    {
        get => _capabilitySummary;
        set => SetProperty(ref _capabilitySummary, value);
    }

    public string DeepStatus
    {
        get => _deepStatus;
        set
        {
            if (SetProperty(ref _deepStatus, value))
            {
                NotifyDeepResultProperties();
            }
        }
    }

    public string DeepSummary
    {
        get => _deepSummary;
        set
        {
            if (SetProperty(ref _deepSummary, value))
            {
                NotifyDeepResultProperties();
            }
        }
    }

    public string DeepCheckedAt
    {
        get => _deepCheckedAt;
        set
        {
            if (SetProperty(ref _deepCheckedAt, value))
            {
                OnPropertyChanged(nameof(DeepCheckedAtText));
            }
        }
    }

    public double CompositeScore
    {
        get => _compositeScore;
        set
        {
            if (SetProperty(ref _compositeScore, value))
            {
                OnPropertyChanged(nameof(CompositeScoreText));
                OnPropertyChanged(nameof(CompositeProgressValue));
            }
        }
    }

    public double StabilityRatio
    {
        get => _stabilityRatio;
        set => SetProperty(ref _stabilityRatio, value);
    }

    public double? TtftMs
    {
        get => _ttftMs;
        set
        {
            if (SetProperty(ref _ttftMs, value))
            {
                OnPropertyChanged(nameof(TtftText));
                OnPropertyChanged(nameof(TtftProgressValue));
            }
        }
    }

    public double? ChatLatencyMs
    {
        get => _chatLatencyMs;
        set
        {
            if (SetProperty(ref _chatLatencyMs, value))
            {
                OnPropertyChanged(nameof(ChatLatencyText));
                OnPropertyChanged(nameof(ChatLatencyProgressValue));
            }
        }
    }

    public double? TokensPerSecond
    {
        get => _tokensPerSecond;
        set
        {
            if (SetProperty(ref _tokensPerSecond, value))
            {
                OnPropertyChanged(nameof(TokensPerSecondText));
                OnPropertyChanged(nameof(TokensPerSecondProgressValue));
            }
        }
    }

    public string Verdict
    {
        get => _verdict;
        set => SetProperty(ref _verdict, value);
    }

    public string SecondaryText
    {
        get => _secondaryText;
        set => SetProperty(ref _secondaryText, value);
    }

    public int RunCount
    {
        get => _runCount;
        set
        {
            if (SetProperty(ref _runCount, value))
            {
                OnPropertyChanged(nameof(RunCountText));
            }
        }
    }

    public string RankText
        => Rank > 0 ? $"#{Rank}" : "--";

    public string ModelDisplay
        => string.IsNullOrWhiteSpace(Model) ? "模型：--" : $"模型：{Model}";

    public string CandidateHint
    {
        get
        {
            var prefix = Rank == 1 ? "当前推荐" : IsSelected ? "已加入候选" : "可加入候选";
            return $"{ModelDisplay} · {prefix}";
        }
    }

    public string RunCountText
        => RunCount > 0 ? $"{RunCount} 轮" : "待运行";

    public string CompositeScoreText
        => CompositeScore > 0d ? $"{CompositeScore:F1} 分" : "--";

    public string ChatLatencyText
        => FormatMilliseconds(ChatLatencyMs);

    public string TtftText
        => FormatMilliseconds(TtftMs);

    public string TokensPerSecondText
        => TokensPerSecond.HasValue ? $"{TokensPerSecond.Value:F1} tok/s" : "--";

    public double CompositeProgressValue
        => ClampProgress(CompositeScore);

    public double ChatLatencyProgressValue
        => BuildLowerIsBetterProgress(ChatLatencyMs, 6000d);

    public double TtftProgressValue
        => BuildLowerIsBetterProgress(TtftMs, 6000d);

    public double TokensPerSecondProgressValue
        => TokensPerSecond.HasValue ? ClampProgress(TokensPerSecond.Value / 72d * 100d) : 0d;

    public string DeepSelectionText
        => IsSelected ? "已纳入深测" : "未选择";

    public string DeepCheckedAtText
        => string.IsNullOrWhiteSpace(DeepCheckedAt) || DeepCheckedAt == "--"
            ? "尚未完成"
            : DeepCheckedAt;

    public string DeepPassText
    {
        get
        {
            var (done, total) = ResolveDeepProgress();
            if (done >= 0 && total > 0)
            {
                return $"{done}/{total} 项";
            }

            return IsSelected ? "等待开始" : "未选择";
        }
    }

    public string DeepHeadline
    {
        get
        {
            if (!IsSelected)
            {
                return "未加入本轮深测";
            }

            if (string.IsNullOrWhiteSpace(DeepSummary))
            {
                return "已排入深度测试队列";
            }

            if (DeepSummary.Contains("失败", StringComparison.Ordinal) ||
                DeepStatus.Contains("失败", StringComparison.Ordinal))
            {
                return "深测执行失败";
            }

            if (DeepSummary.Contains("进度", StringComparison.Ordinal) ||
                DeepStatus.Contains("进行", StringComparison.Ordinal))
            {
                return "正在执行深度探针";
            }

            return "深测结果已汇总";
        }
    }

    public string DeepCompactSummary
    {
        get
        {
            if (!IsSelected)
            {
                return "勾选后会显示深测通过概况。";
            }

            if (string.IsNullOrWhiteSpace(DeepSummary))
            {
                return "等待开始深测，将展示通过项数量和关键阶段。";
            }

            var summary = DeepSummary
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
            var issueIndex = summary.IndexOf("问题", StringComparison.Ordinal);
            if (issueIndex >= 0)
            {
                summary = summary[..issueIndex].Trim().Trim('|').Trim();
            }

            return summary.Length > 82 ? summary[..81] + "…" : summary;
        }
    }

    public double DeepProgressValue
    {
        get
        {
            var (done, total) = ResolveDeepProgress();
            if (done < 0 || total <= 0)
            {
                return 0d;
            }

            return ClampProgress(done * 100d / total);
        }
    }

    internal string ApiKey { get; set; } = string.Empty;

    private static string FormatMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:0} ms" : "--";

    private static double BuildLowerIsBetterProgress(double? value, double softMax)
    {
        if (value is not > 0 || softMax <= 0)
        {
            return 0d;
        }

        var ratio = 1d - Math.Min(value.Value, softMax) / softMax;
        return ClampProgress(Math.Max(0.08d, ratio) * 100d);
    }

    private static double ClampProgress(double value)
        => Math.Clamp(value, 0d, 100d);

    private void NotifyDeepResultProperties()
    {
        OnPropertyChanged(nameof(DeepPassText));
        OnPropertyChanged(nameof(DeepHeadline));
        OnPropertyChanged(nameof(DeepCompactSummary));
        OnPropertyChanged(nameof(DeepProgressValue));
    }

    private (int Done, int Total) ResolveDeepProgress()
    {
        if (string.IsNullOrWhiteSpace(DeepSummary))
        {
            return (-1, 0);
        }

        var progressMatch = Regex.Match(DeepSummary, @"进度\s*(\d+)\s*/\s*(\d+)");
        if (progressMatch.Success &&
            int.TryParse(progressMatch.Groups[1].Value, out var progressDone) &&
            int.TryParse(progressMatch.Groups[2].Value, out var progressTotal))
        {
            return (progressDone, progressTotal);
        }

        var baselineMatch = Regex.Match(DeepSummary, @"B5\s+(\d+)\s*/\s*5");
        var passCount = 0;
        var totalCount = 0;
        if (baselineMatch.Success && int.TryParse(baselineMatch.Groups[1].Value, out var baselinePass))
        {
            passCount += baselinePass;
            totalCount += 5;
        }

        foreach (Match match in Regex.Matches(DeepSummary, @"\b(Sys|Fn|Err|Str|Ref|MM|Cch|Iso)\s+(OK|RV|CFG|SK|NO|ER)\b"))
        {
            totalCount++;
            if (string.Equals(match.Groups[2].Value, "OK", StringComparison.Ordinal))
            {
                passCount++;
            }
        }

        return totalCount > 0 ? (passCount, totalCount) : (-1, 0);
    }
}
