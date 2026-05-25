using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class HistoryReportsViewModel
{
    private async Task LoadSelectedReportDetailsAsync(HistoryReportItem? item)
    {
        MetricTiles.Clear();
        ProtocolResults.Clear();
        ChartRows.Clear();
        Attachments.Clear();
        CapabilityRows.Clear();
        OnPropertyChanged(nameof(HasHistoryChartRows));
        ResetSelectedReportMetadata();

        if (item is null)
        {
            SelectedReportStatusText = "--";
            SelectedReportSuccessVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            SelectedReportReviewVisibility = Microsoft.UI.Xaml.Visibility.Visible;
            return;
        }

        SelectedReportStatusText = item.StatusText;
        SelectedReportSuccessVisibility = item.SuccessVisibility;
        SelectedReportReviewVisibility = item.ReviewVisibility;

        var report = await _repository.GetAsync(item.Id);
        if (report is null)
        {
            StatusText = "所选报告已不存在";
            return;
        }

        MetricTiles.Add(new HistoryMetricTile("综合评分", report.Score.HasValue ? $"{report.Score.Value:F1} / 100" : "--", report.TestType, HistoryTones.Accent));
        MetricTiles.Add(new HistoryMetricTile("耗时", report.DurationMs.HasValue ? FormatDuration(report.DurationMs.Value) : "--", "已用时间", HistoryTones.Healthy));
        MetricTiles.Add(new HistoryMetricTile("入口", report.Endpoint, "目标", HistoryTones.Warning));
        MetricTiles.Add(new HistoryMetricTile("创建时间", report.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "本地时间", HistoryTones.Accent));

        using var payload = TryParsePayload(report.PayloadJson);
        if (payload is not null)
        {
            PopulateSelectedReportMetadata(report, payload.RootElement);
            PopulateMetricTilesFromPayload(payload.RootElement);
            PopulateSelectedRouteMapEvidence(payload.RootElement);
            PopulateSelectedReportSummary(report, payload.RootElement);
            PopulateChartRowsFromPayload(payload.RootElement);
        }
        else
        {
            PopulateSelectedReportMetadata(report, null);
            PopulateSelectedReportSummary(report, null);
        }

        var hasProtocolRows = payload is not null && PopulateProtocolRowsFromPayload(payload.RootElement);
        if (!hasProtocolRows)
        {
            ProtocolResults.Add(new HistoryProtocolResult(
                TranslateType(report.TestType),
                report.DurationMs.HasValue ? FormatDuration(report.DurationMs.Value) : "--",
                "--",
                "--",
                report.Score is null or >= 60 ? HistoryStates.Passed : HistoryStates.Review,
                report.Score is null or >= 60 ? HistoryTones.Healthy : HistoryTones.Warning));
        }

        Attachments.Add(new HistoryAttachmentItem("report.md", "generated"));
        Attachments.Add(new HistoryAttachmentItem("payload.json", $"{Encoding.UTF8.GetByteCount(report.PayloadJson) / 1024.0:F1} KB"));
        if (payload is not null)
        {
            PopulateTraceAttachments(payload.RootElement);
        }

        SelectedReportAttachmentTitle = $"附件 ({Attachments.Count})";

        var hasCapabilityRows = payload is not null && PopulateCapabilityRowsFromPayload(payload.RootElement);
        if (!hasCapabilityRows)
        {
            CapabilityRows.Add(new HistoryCapabilityRow(
                TranslateType(report.TestType),
                report.Score is null or >= 60,
                report.DurationMs.HasValue,
                HasMeaningfulPayload(report.PayloadJson)));
        }

        OnPropertyChanged(nameof(HasHistoryChartRows));
    }

    [RelayCommand]
    private void CopyReportLink()
    {
        if (SelectedReport is null)
        {
            StatusText = "请先选择报告";
            return;
        }

        try
        {
            var link = $"relaybench://history/{Uri.EscapeDataString(SelectedReport.Id)}";
            var package = new DataPackage();
            package.SetText(link);
            Clipboard.SetContent(package);
            StatusText = $"已复制报告链接：{SelectedReport.Id}";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyRouteMapPath()
    {
        if (string.IsNullOrWhiteSpace(SelectedReportRouteMapImagePath))
        {
            StatusText = "没有记录路线图图片";
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(SelectedReportRouteMapImagePath);
            Clipboard.SetContent(package);
            StatusText = "已复制路线图路径";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CompareSelectedReportAsync()
    {
        if (SelectedReport is null)
        {
            StatusText = "请先选择报告";
            return;
        }

        var current = await _repository.GetAsync(SelectedReport.Id);
        if (current is null)
        {
            StatusText = "所选报告已不存在";
            return;
        }

        var sameTypeReports = (await _repository.QueryAsync(new HistoryQuery(Limit: 1000)))
            .Where(summary => string.Equals(summary.TestType, current.TestType, StringComparison.OrdinalIgnoreCase))
            .Where(summary => !string.Equals(summary.RunId, current.RunId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(summary => summary.CreatedAtUtc)
            .ToArray();

        if (sameTypeReports.Length == 0)
        {
            StatusText = "未找到可对比的报告";
            return;
        }

        var baselineSummary = sameTypeReports
            .Where(summary => summary.CreatedAtUtc <= current.CreatedAtUtc)
            .OrderByDescending(summary => summary.CreatedAtUtc)
            .FirstOrDefault() ?? sameTypeReports[0];

        var baseline = await _repository.GetAsync(baselineSummary.RunId);
        if (baseline is null)
        {
            StatusText = "Baseline report no longer exists";
            return;
        }

        var comparisonDir = Path.Combine(_exportRoot, "comparisons");
        Directory.CreateDirectory(comparisonDir);

        var comparisonPath = Path.Combine(
            comparisonDir,
            $"compare-{SanitizePathSegment(current.RunId)}-{SanitizePathSegment(baseline.RunId)}.md");

        await File.WriteAllTextAsync(comparisonPath, BuildComparisonMarkdown(current, baseline), Encoding.UTF8);
        StatusText = $"Comparison saved: {comparisonPath}";
    }

    [RelayCommand]
    private async Task PrepareRerunAsync()
    {
        if (SelectedReport is null)
        {
            StatusText = "Select a report first";
            return;
        }

        var report = await _repository.GetAsync(SelectedReport.Id);
        if (report is null)
        {
            StatusText = "Selected report no longer exists";
            return;
        }

        var rerunDir = Path.Combine(_exportRoot, "reruns", SanitizePathSegment(report.RunId));
        Directory.CreateDirectory(rerunDir);

        var rerunPath = Path.Combine(rerunDir, "rerun-seed.json");
        var payload = new
        {
            report.RunId,
            report.CreatedAtUtc,
            report.TestType,
            report.Endpoint,
            report.Summary,
            report.Score,
            report.DurationMs,
            report.PayloadJson
        };

        await File.WriteAllTextAsync(
            rerunPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);

        StatusText = $"Rerun seed saved: {rerunPath}";
    }

}
