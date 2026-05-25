using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a single site entry in the sortable ranking table.
/// </summary>
public sealed partial class SiteRankEntry : ObservableObject
{
    [ObservableProperty] public partial int Rank { get; set; }
    [ObservableProperty] public partial string SiteName { get; set; } = string.Empty;
    [ObservableProperty] public partial double LatencyP50 { get; set; }
    [ObservableProperty] public partial double? TtftP50 { get; set; }
    [ObservableProperty] public partial double Throughput { get; set; }
    [ObservableProperty] public partial double SuccessRate { get; set; }
    [ObservableProperty] public partial double CompositeScore { get; set; }
    [ObservableProperty] public partial string BaseUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string ApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string Model { get; set; } = string.Empty;
    [ObservableProperty] public partial string CapabilityText { get; set; } = "--";
    [ObservableProperty] public partial string CacheStateText { get; set; } = "--";
    [ObservableProperty] public partial string ProtocolSummary { get; set; } = "未探测";
    [ObservableProperty] public partial string LatestResult { get; set; } = "--";
    [ObservableProperty] public partial string VerdictText { get; set; } = "--";
    [ObservableProperty] public partial string SecondaryText { get; set; } = "--";
    [ObservableProperty] public partial int RunCount { get; set; } = 1;

    public SiteRankEntry() { }

    public SiteRankEntry(int rank, string siteName, double latencyP50, double throughput, double successRate, double compositeScore)
    {
        Rank = rank;
        SiteName = siteName;
        LatencyP50 = latencyP50;
        Throughput = throughput;
        SuccessRate = successRate;
        CompositeScore = compositeScore;
    }

    public SiteRankEntry(int rank, string siteName, double latencyP50, double? ttftP50, double throughput, double successRate, double compositeScore)
        : this(rank, siteName, latencyP50, throughput, successRate, compositeScore)
    {
        TtftP50 = ttftP50;
    }

    /// <summary>
    /// Whether this entry is rank 1 (visually distinguished with gold/accent color).
    /// </summary>
    public bool IsTopRank => Rank == 1;

    public Visibility TopRankVisibility => IsTopRank ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NormalRankVisibility => IsTopRank ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HealthyScoreVisibility => CompositeScore >= 90
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility WarningScoreVisibility => CompositeScore >= 90
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility HealthySuccessRateVisibility => SuccessRate >= 99.0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility WarningSuccessRateVisibility => SuccessRate >= 99.0
        ? Visibility.Collapsed
        : Visibility.Visible;

    /// <summary>
    /// Formatted latency string with "ms" suffix.
    /// </summary>
    public string LatencyDisplay => $"{LatencyP50:F0} ms";

    /// <summary>
    /// Formatted TTFT string with "ms" suffix.
    /// </summary>
    public string TtftDisplay => TtftP50.HasValue ? $"{TtftP50.Value:F0} ms" : "--";

    /// <summary>
    /// Formatted throughput string.
    /// </summary>
    public string ThroughputDisplay => $"{Throughput:F1}";

    /// <summary>
    /// Formatted success rate string with "%" suffix.
    /// </summary>
    public string SuccessRateDisplay => $"{SuccessRate:F1}%";

    /// <summary>
    /// Formatted composite score string.
    /// </summary>
    public string ScoreDisplay => $"{CompositeScore:F1}";

    public string ModelDisplay => string.IsNullOrWhiteSpace(Model) ? "--" : Model;

    public string RunCountText => RunCount > 0 ? $"{RunCount} 轮" : "待运行";

    public string RankingTooltip
        => $"{SiteName}\n结论：{VerdictText}\n摘要：{SecondaryText}\n轮次：{RunCountText}\n能力：{CapabilityText}\n缓存：{CacheStateText}\n协议：{ProtocolSummary}\n最近结果：{LatestResult}";

    public bool CanApplyEndpoint
        => !string.IsNullOrWhiteSpace(BaseUrl) &&
           !string.IsNullOrWhiteSpace(ApiKey) &&
           !string.IsNullOrWhiteSpace(Model);

    public string ApplyTooltip
        => CanApplyEndpoint
            ? $"设为当前入口：{BaseUrl}"
            : "该排行项缺少地址、密钥或模型";

    partial void OnRankChanged(int value)
    {
        OnPropertyChanged(nameof(IsTopRank));
        OnPropertyChanged(nameof(TopRankVisibility));
        OnPropertyChanged(nameof(NormalRankVisibility));
    }

    partial void OnCompositeScoreChanged(double value)
    {
        OnPropertyChanged(nameof(HealthyScoreVisibility));
        OnPropertyChanged(nameof(WarningScoreVisibility));
        OnPropertyChanged(nameof(ScoreDisplay));
    }

    partial void OnSuccessRateChanged(double value)
    {
        OnPropertyChanged(nameof(HealthySuccessRateVisibility));
        OnPropertyChanged(nameof(WarningSuccessRateVisibility));
        OnPropertyChanged(nameof(SuccessRateDisplay));
    }

    partial void OnLatencyP50Changed(double value)
    {
        OnPropertyChanged(nameof(LatencyDisplay));
    }

    partial void OnTtftP50Changed(double? value)
    {
        OnPropertyChanged(nameof(TtftDisplay));
    }

    partial void OnThroughputChanged(double value)
    {
        OnPropertyChanged(nameof(ThroughputDisplay));
    }

    partial void OnBaseUrlChanged(string value)
    {
        OnPropertyChanged(nameof(CanApplyEndpoint));
        OnPropertyChanged(nameof(ApplyTooltip));
    }

    partial void OnApiKeyChanged(string value)
    {
        OnPropertyChanged(nameof(CanApplyEndpoint));
        OnPropertyChanged(nameof(ApplyTooltip));
    }

    partial void OnModelChanged(string value)
    {
        OnPropertyChanged(nameof(CanApplyEndpoint));
        OnPropertyChanged(nameof(ApplyTooltip));
        OnPropertyChanged(nameof(ModelDisplay));
    }

    partial void OnCapabilityTextChanged(string value) => OnPropertyChanged(nameof(RankingTooltip));

    partial void OnCacheStateTextChanged(string value) => OnPropertyChanged(nameof(RankingTooltip));

    partial void OnProtocolSummaryChanged(string value) => OnPropertyChanged(nameof(RankingTooltip));

    partial void OnLatestResultChanged(string value) => OnPropertyChanged(nameof(RankingTooltip));

    partial void OnVerdictTextChanged(string value) => OnPropertyChanged(nameof(RankingTooltip));

    partial void OnSecondaryTextChanged(string value) => OnPropertyChanged(nameof(RankingTooltip));

    partial void OnRunCountChanged(int value)
    {
        OnPropertyChanged(nameof(RunCountText));
        OnPropertyChanged(nameof(RankingTooltip));
    }
}
