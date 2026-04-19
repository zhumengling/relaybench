using NetTest.App.Infrastructure;

namespace NetTest.App.ViewModels;

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

    internal string ApiKey { get; set; } = string.Empty;
}
