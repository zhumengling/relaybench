using NetTest.App.Infrastructure;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string WorkbenchPageSingleStation = "single-station";
    private const string WorkbenchPageBatchComparison = "batch-comparison";
    private const string WorkbenchPageNetworkReview = "network-review";
    private const string WorkbenchPageHistoryReports = "history-reports";

    private const string SingleStationModeQuick = "quick";
    private const string SingleStationModeStability = "stability";
    private const string SingleStationModeDeep = "deep";

    private string _selectedWorkbenchPageKey = WorkbenchPageSingleStation;
    private string _selectedSingleStationModeKey = SingleStationModeQuick;

    public string SelectedWorkbenchPageKey
    {
        get => _selectedWorkbenchPageKey;
        set
        {
            var normalized = NormalizeWorkbenchPageKey(value);
            if (SetProperty(ref _selectedWorkbenchPageKey, normalized))
            {
                NotifyWorkbenchPageStateChanged();
            }
        }
    }

    public string SelectedSingleStationModeKey
    {
        get => _selectedSingleStationModeKey;
        set
        {
            var normalized = NormalizeSingleStationModeKey(value);
            if (SetProperty(ref _selectedSingleStationModeKey, normalized))
            {
                NotifySingleStationModeStateChanged();
                OnPropertyChanged(nameof(CurrentPageSubtitle));
            }
        }
    }

    public bool IsSingleStationPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageSingleStation, StringComparison.Ordinal);

    public bool IsBatchComparisonPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageBatchComparison, StringComparison.Ordinal);

    public bool IsNetworkReviewPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageNetworkReview, StringComparison.Ordinal);

    public bool IsHistoryReportsPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageHistoryReports, StringComparison.Ordinal);

    public string CurrentPageTitle
        => SelectedWorkbenchPageKey switch
        {
            WorkbenchPageBatchComparison => "批量对比",
            WorkbenchPageNetworkReview => "网络复核",
            WorkbenchPageHistoryReports => "历史报告",
            _ => "单站测试"
        };

    public string CurrentPageSubtitle
        => SelectedWorkbenchPageKey switch
        {
            WorkbenchPageBatchComparison => "先选择或导入入口组，再运行一次快速对比；完成后在排行榜列表里手动勾选候选站点。",
            WorkbenchPageNetworkReview => BuildNetworkReviewSubtitle(),
            WorkbenchPageHistoryReports => "集中回看最近运行记录与结构化报告归档，不承担当前测试入口职责。",
            _ => BuildSingleStationSubtitle()
        };

    private void LoadWorkbenchState(AppStateSnapshot snapshot)
    {
        _selectedWorkbenchPageKey = WorkbenchPageSingleStation;
        _selectedSingleStationModeKey = NormalizeSingleStationModeKey(snapshot.SingleStationModeKey);
        NotifyWorkbenchPageStateChanged();
        OnPropertyChanged(nameof(SelectedWorkbenchPageKey));
        OnPropertyChanged(nameof(SelectedSingleStationModeKey));
        NotifySingleStationModeStateChanged();
    }

    private void ApplyWorkbenchStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.SingleStationModeKey = NormalizeSingleStationModeKey(SelectedSingleStationModeKey);
    }

    private static string NormalizeWorkbenchPageKey(string? value)
        => value switch
        {
            WorkbenchPageBatchComparison => WorkbenchPageBatchComparison,
            WorkbenchPageNetworkReview => WorkbenchPageNetworkReview,
            WorkbenchPageHistoryReports => WorkbenchPageHistoryReports,
            _ => WorkbenchPageSingleStation
        };

    private static string NormalizeSingleStationModeKey(string? value)
        => value switch
        {
            SingleStationModeStability => SingleStationModeStability,
            SingleStationModeDeep => SingleStationModeDeep,
            _ => SingleStationModeQuick
        };

    private string BuildSingleStationSubtitle()
        => SelectedSingleStationModeKey switch
        {
            SingleStationModeStability => "当前模式：稳定性测试。适合多轮观察成功率、波动与连续失败情况。",
            SingleStationModeDeep => "当前模式：深度测试。适合验证协议兼容、流式完整性、多模态与缓存隔离等高级能力。",
            _ => "当前模式：快速测试。默认用于判断站点是否可用、首字响应是否稳定。"
        };

    private void NotifyWorkbenchPageStateChanged()
    {
        OnPropertyChanged(nameof(IsSingleStationPageActive));
        OnPropertyChanged(nameof(IsBatchComparisonPageActive));
        OnPropertyChanged(nameof(IsNetworkReviewPageActive));
        OnPropertyChanged(nameof(IsHistoryReportsPageActive));
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(CurrentPageSubtitle));
    }
}
