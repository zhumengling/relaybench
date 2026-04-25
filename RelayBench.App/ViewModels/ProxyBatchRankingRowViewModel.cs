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
        set => SetProperty(ref _isSelected, value);
    }

    public int Rank
    {
        get => _rank;
        set => SetProperty(ref _rank, value);
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
        set => SetProperty(ref _model, value);
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
        set => SetProperty(ref _deepStatus, value);
    }

    public string DeepSummary
    {
        get => _deepSummary;
        set => SetProperty(ref _deepSummary, value);
    }

    public string DeepCheckedAt
    {
        get => _deepCheckedAt;
        set => SetProperty(ref _deepCheckedAt, value);
    }

    public double CompositeScore
    {
        get => _compositeScore;
        set => SetProperty(ref _compositeScore, value);
    }

    public double StabilityRatio
    {
        get => _stabilityRatio;
        set => SetProperty(ref _stabilityRatio, value);
    }

    public double? TtftMs
    {
        get => _ttftMs;
        set => SetProperty(ref _ttftMs, value);
    }

    public double? ChatLatencyMs
    {
        get => _chatLatencyMs;
        set => SetProperty(ref _chatLatencyMs, value);
    }

    public double? TokensPerSecond
    {
        get => _tokensPerSecond;
        set => SetProperty(ref _tokensPerSecond, value);
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
        set => SetProperty(ref _runCount, value);
    }

    internal string ApiKey { get; set; } = string.Empty;
}
