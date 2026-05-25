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
    private void PopulateTraceAttachments(JsonElement root)
    {
        if (TryGetProperty(root, "trace", out var trace) && trace.ValueKind == JsonValueKind.Object)
        {
            if (!string.IsNullOrWhiteSpace(TryGetString(trace, "requestId")))
            {
                Attachments.Add(new HistoryAttachmentItem("request-id", "recorded"));
            }

            if (!string.IsNullOrWhiteSpace(TryGetString(trace, "traceId")))
            {
                Attachments.Add(new HistoryAttachmentItem("trace-id", "recorded"));
            }

            if (!string.IsNullOrWhiteSpace(TryGetString(trace, "headers")))
            {
                Attachments.Add(new HistoryAttachmentItem("response-headers", "recorded"));
            }
        }

        foreach (var artifact in ExtractTextArtifactSummaries(root, "payload").Take(32))
        {
            Attachments.Add(new HistoryAttachmentItem(
                $"{SanitizePathSegment(artifact.Path)}.txt",
                FormatByteSize(Encoding.UTF8.GetByteCount(artifact.Value))));
        }

        foreach (var artifact in ExtractFileArtifactSummaries(root, "payload").Take(16))
        {
            Attachments.Add(new HistoryAttachmentItem(
                $"{SanitizePathSegment(artifact.Path)}{Path.GetExtension(artifact.SourcePath)}",
                FormatByteSize(artifact.Size)));
        }

        foreach (var artifact in ExtractLegacyArtifactReferences(root).Take(32))
        {
            Attachments.Add(new HistoryAttachmentItem(
                SanitizePathSegment(artifact.Path),
                string.IsNullOrWhiteSpace(artifact.Description) ? "legacy artifact" : artifact.Description));
        }
    }

    private static string BuildReportMarkdown(HistoryReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# RelayBench Report {report.RunId}");
        sb.AppendLine();
        sb.AppendLine($"- 类型: {report.TestType}");
        sb.AppendLine($"- 创建时间: {report.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 入口: {report.Endpoint}");
        sb.AppendLine($"- 评分: {(report.Score.HasValue ? report.Score.Value.ToString("F1") : "--")}");
        sb.AppendLine($"- 耗时: {(report.DurationMs.HasValue ? FormatDuration(report.DurationMs.Value) : "--")}");
        sb.AppendLine();
        sb.AppendLine("## 摘要");
        sb.AppendLine();
        sb.AppendLine(report.Summary);
        sb.AppendLine();
        sb.AppendLine("## Payload");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(report.PayloadJson);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string BuildComparisonMarkdown(HistoryReport current, HistoryReport baseline)
    {
        StringBuilder sb = new();
        sb.AppendLine("# RelayBench 报告对比");
        sb.AppendLine();
        sb.AppendLine($"- 当前: {current.RunId}");
        sb.AppendLine($"- 基线: {baseline.RunId}");
        sb.AppendLine($"- 类型: {current.TestType}");
        sb.AppendLine($"- 入口: {current.Endpoint}");
        sb.AppendLine();
        sb.AppendLine("| 字段 | 当前 | 基线 | 变化 |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        AppendComparisonRow(sb, "评分", current.Score, baseline.Score, "pts", value => value?.ToString("F1") ?? "--");
        AppendComparisonRow(sb, "耗时", current.DurationMs, baseline.DurationMs, "ms", value => value?.ToString() ?? "--");
        sb.AppendLine();
        sb.AppendLine("## 当前摘要");
        sb.AppendLine();
        sb.AppendLine(current.Summary);
        sb.AppendLine();
        sb.AppendLine("## 基线摘要");
        sb.AppendLine();
        sb.AppendLine(baseline.Summary);
        return sb.ToString();
    }

    private static void AppendComparisonRow<T>(
        StringBuilder sb,
        string label,
        T? current,
        T? baseline,
        string unit,
        Func<T?, string> formatter)
        where T : struct, IComparable<T>, IFormattable
    {
        var currentText = formatter(current);
        var baselineText = formatter(baseline);

        string deltaText = "--";
        if (current.HasValue && baseline.HasValue)
        {
            if (typeof(T) == typeof(double))
            {
                var delta = Convert.ToDouble(current.Value) - Convert.ToDouble(baseline.Value);
                deltaText = $"{delta:+0.0;-0.0;0.0} {unit}";
            }
            else if (typeof(T) == typeof(int))
            {
                var delta = Convert.ToInt32(current.Value) - Convert.ToInt32(baseline.Value);
                deltaText = $"{delta:+0;-0;0} {unit}";
            }
        }

        sb.AppendLine($"| {label} | {currentText} {unit} | {baselineText} {unit} | {deltaText} |");
    }

    private static string FormatDuration(int ms)
    {
        if (ms < 1000) return $"{ms}ms";
        if (ms < 60_000) return $"{ms / 1000.0:F1}s";
        return $"{ms / 60_000}m {(ms % 60_000) / 1000}s";
    }

}
