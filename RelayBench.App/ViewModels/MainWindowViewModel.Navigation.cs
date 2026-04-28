using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string WorkbenchPageInterfaceDiagnostics = "interface-diagnostics";
    private const string WorkbenchPageModelChat = "model-chat";
    private const string WorkbenchPageBatchEvaluation = "batch-evaluation";
    private const string WorkbenchPageApplicationCenter = "application-center";
    private const string WorkbenchPageNetworkReview = "network-review";
    private const string WorkbenchPageHistoryReports = "history-reports";

    private const string WorkbenchPageSingleStationLegacy = "single-station";
    private const string WorkbenchPageBatchComparisonLegacy = "batch-comparison";

    private const string SingleStationModeQuick = "quick";
    private const string SingleStationModeStability = "stability";
    private const string SingleStationModeDeep = "deep";
    private const string SingleStationModeConcurrency = "concurrency";

    private string _selectedWorkbenchPageKey = WorkbenchPageInterfaceDiagnostics;
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
                if (!IsBusy)
                {
                    RefreshSingleStationInlineChartPlaceholder();
                }
                OnPropertyChanged(nameof(CurrentPageSubtitle));
            }
        }
    }

    public bool IsInterfaceDiagnosticsPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageInterfaceDiagnostics, StringComparison.Ordinal);

    public bool IsSingleStationPageActive
        => IsInterfaceDiagnosticsPageActive;

    public bool IsBatchEvaluationPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageBatchEvaluation, StringComparison.Ordinal);

    public bool IsModelChatPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageModelChat, StringComparison.Ordinal);

    public bool IsBatchComparisonPageActive
        => IsBatchEvaluationPageActive;

    public bool IsApplicationCenterPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageApplicationCenter, StringComparison.Ordinal);

    public bool IsNetworkReviewPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageNetworkReview, StringComparison.Ordinal);

    public bool IsHistoryReportsPageActive
        => string.Equals(SelectedWorkbenchPageKey, WorkbenchPageHistoryReports, StringComparison.Ordinal);

    public string CurrentPageTitle
        => SelectedWorkbenchPageKey switch
        {
            WorkbenchPageModelChat => "\u5927\u6a21\u578b\u5bf9\u8bdd",
            WorkbenchPageBatchEvaluation => "\u6279\u91cf\u8bc4\u6d4b",
            WorkbenchPageApplicationCenter => "\u5e94\u7528\u63a5\u5165",
            WorkbenchPageNetworkReview => "\u7f51\u7edc\u590d\u6838",
            WorkbenchPageHistoryReports => "\u5386\u53f2\u62a5\u544a",
            _ => "\u5355\u7ad9\u6d4b\u8bd5"
        };

    public string CurrentPageSubtitle
        => SelectedWorkbenchPageKey switch
        {
            WorkbenchPageModelChat => "\u590d\u7528\u5f53\u524d\u63a5\u53e3\u8fdb\u884c\u771f\u5b9e\u591a\u8f6e\u5bf9\u8bdd\uff0c\u89c2\u5bdf\u6d41\u5f0f\u8f93\u51fa\u3001\u4ee3\u7801\u5757\u3001\u56fe\u7247\u8f93\u5165\u3001\u6587\u672c\u9644\u4ef6\u548c reasoning \u53c2\u6570\u517c\u5bb9\u6027\u3002",
            WorkbenchPageBatchEvaluation => "\u5148\u5bfc\u5165\u6216\u7ef4\u62a4\u63a5\u53e3\u7ec4\uff0c\u518d\u505a\u6279\u91cf\u5feb\u901f\u8bc4\u6d4b\uff1b\u4ece\u6392\u884c\u699c\u91cc\u624b\u52a8\u7b5b\u9009\u4e3b\u7528\u3001\u5907\u7528\u4e0e\u5019\u9009\u63a5\u53e3\u3002",
            WorkbenchPageApplicationCenter => "\u626b\u63cf Codex / VSCode / Antigravity / Claude \u7b49\u672c\u673a\u5e94\u7528\u7684\u5b89\u88c5\u4e0e\u63a5\u5165\u72b6\u6001\uff0c\u5e76\u5728\u6b64\u76f4\u63a5\u5b8c\u6210 Codex \u7cfb\u5217\u7684\u63a5\u53e3\u5199\u5165\u3001\u8fd8\u539f\u4e0e Trace \u590d\u6838\u3002",
            WorkbenchPageNetworkReview => BuildNetworkReviewSubtitle(),
            WorkbenchPageHistoryReports => "\u96c6\u4e2d\u56de\u770b\u6700\u8fd1\u7684\u63a5\u53e3\u6d4b\u8bd5\u3001\u80fd\u529b\u7ed3\u679c\u4e0e\u7ed3\u6784\u5316\u62a5\u544a\u5f52\u6863\u3002",
            _ => BuildSingleStationSubtitle()
        };

    private void LoadWorkbenchState(AppStateSnapshot snapshot)
    {
        _selectedWorkbenchPageKey = WorkbenchPageInterfaceDiagnostics;
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
            WorkbenchPageModelChat => WorkbenchPageModelChat,
            WorkbenchPageBatchEvaluation or WorkbenchPageBatchComparisonLegacy => WorkbenchPageBatchEvaluation,
            WorkbenchPageApplicationCenter => WorkbenchPageApplicationCenter,
            WorkbenchPageNetworkReview => WorkbenchPageNetworkReview,
            WorkbenchPageHistoryReports => WorkbenchPageHistoryReports,
            WorkbenchPageInterfaceDiagnostics or WorkbenchPageSingleStationLegacy => WorkbenchPageInterfaceDiagnostics,
            _ => WorkbenchPageInterfaceDiagnostics
        };

    private static string NormalizeSingleStationModeKey(string? value)
        => value switch
        {
            SingleStationModeStability => SingleStationModeStability,
            SingleStationModeDeep => SingleStationModeDeep,
            SingleStationModeConcurrency => SingleStationModeConcurrency,
            _ => SingleStationModeQuick
        };

    private string BuildSingleStationSubtitle()
        => SelectedSingleStationModeKey switch
        {
            SingleStationModeStability => "\u5f53\u524d\u6a21\u5f0f\uff1a\u7a33\u5b9a\u6027\u6d4b\u8bd5\u3002\u9002\u5408\u591a\u8f6e\u89c2\u5bdf\u6210\u529f\u7387\u3001\u6ce2\u52a8\u548c\u8fde\u7eed\u5931\u8d25\u60c5\u51b5\u3002",
            SingleStationModeDeep => "\u5f53\u524d\u6a21\u5f0f\uff1a\u6df1\u5ea6\u6d4b\u8bd5\u3002\u9002\u5408\u9a8c\u8bc1\u534f\u8bae\u517c\u5bb9\u3001\u6d41\u5f0f\u5b8c\u6574\u6027\u3001\u591a\u6a21\u6001\u4e0e\u7f13\u5b58\u9694\u79bb\u7b49\u9ad8\u7ea7\u80fd\u529b\u3002",
            SingleStationModeConcurrency => "\u5f53\u524d\u6a21\u5f0f\uff1a\u5e76\u53d1\u538b\u6d4b\u3002\u9002\u5408\u89c2\u5bdf 1 / 2 / 4 / 8 / 16 \u6863\u5e76\u53d1\u4e0b\u7684\u6210\u529f\u7387\u3001\u9650\u6d41\u8d77\u70b9\u3001TTFT \u4e0e tok/s \u53d8\u5316\u3002",
            _ => "\u5f53\u524d\u6a21\u5f0f\uff1a\u5feb\u901f\u6d4b\u8bd5\u3002\u53ea\u9002\u5408\u804a\u5929\u6a21\u578b\uff0c\u9ed8\u8ba4\u7528\u4e8e\u5224\u65ad\u63a5\u53e3\u662f\u5426\u53ef\u7528\uff0c\u4ee5\u53ca\u9996\u5b57\u54cd\u5e94\u4e0e\u57fa\u7840\u534f\u8bae\u94fe\u8def\u662f\u5426\u6b63\u5e38\u3002"
        };

    private void NotifyWorkbenchPageStateChanged()
    {
        OnPropertyChanged(nameof(IsInterfaceDiagnosticsPageActive));
        OnPropertyChanged(nameof(IsSingleStationPageActive));
        OnPropertyChanged(nameof(IsModelChatPageActive));
        OnPropertyChanged(nameof(IsBatchEvaluationPageActive));
        OnPropertyChanged(nameof(IsBatchComparisonPageActive));
        OnPropertyChanged(nameof(IsApplicationCenterPageActive));
        OnPropertyChanged(nameof(IsNetworkReviewPageActive));
        OnPropertyChanged(nameof(IsHistoryReportsPageActive));
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(CurrentPageSubtitle));
    }
}
