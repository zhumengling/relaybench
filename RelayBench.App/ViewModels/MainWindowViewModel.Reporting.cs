using RelayBench.App.Infrastructure;
using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string ExportReportBusyMessage = "正在导出结构化诊断报告...";

    private Task ExportCurrentReportAsync()
        => ExecuteBusyActionAsync(ExportReportBusyMessage, ExportCurrentReportCoreAsync);

    private Task ExportCurrentReportCoreAsync()
    {
        var overviewStatus = string.Equals(StatusMessage, ExportReportBusyMessage, StringComparison.Ordinal)
            ? "报告基于当前界面状态导出。"
            : StatusMessage;

        var relayRecommendation = BuildRelayRecommendationSnapshot();
        var trendSnapshot = BuildTrendSummarySnapshot();
        var unlockSnapshot = BuildUnlockSemanticSnapshot();
        var natSnapshot = BuildNatReviewSnapshot();
        var conclusionSummary = BuildConclusionSummary(relayRecommendation, trendSnapshot, unlockSnapshot, natSnapshot);

        var sections = BuildReportSections(overviewStatus, conclusionSummary);
        var textArtifacts = BuildReportTextArtifacts(conclusionSummary);
        var imageArtifacts = BuildReportImageArtifacts();
        var structuredPayload = BuildStructuredReportPayload(
            overviewStatus,
            conclusionSummary,
            relayRecommendation,
            trendSnapshot,
            unlockSnapshot,
            natSnapshot,
            sections,
            textArtifacts,
            imageArtifacts);

        var bundle = _diagnosticReportService.ExportBundleReport(
            "relaybench-结构化诊断报告",
            sections,
            textArtifacts,
            imageArtifacts,
            structuredPayload);

        RefreshReportArchiveView();
        StatusMessage = $"结构化报告已导出到：{bundle.BundlePath}";
        AppendHistory("报告", "导出结构化报告", bundle.BundlePath);
        return Task.CompletedTask;
    }
}
