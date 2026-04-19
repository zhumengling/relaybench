using System.IO;
using System.Text;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _reportArchiveSummary = "导出结构化报告后，这里会显示最近的报告归档。";

    public string ReportArchiveSummary
    {
        get => _reportArchiveSummary;
        private set => SetProperty(ref _reportArchiveSummary, value);
    }

    private void RefreshReportArchiveView()
    {
        var reportsDirectory = _diagnosticReportService.ReportsDirectory;
        Directory.CreateDirectory(reportsDirectory);

        var zipFiles = new DirectoryInfo(reportsDirectory)
            .EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(12)
            .ToArray();

        if (zipFiles.Length == 0)
        {
            ReportArchiveSummary =
                $"报告目录：{reportsDirectory}\n" +
                "当前还没有导出的结构化报告归档。";
            return;
        }

        StringBuilder builder = new();
        builder.AppendLine($"报告目录：{reportsDirectory}");
        builder.AppendLine($"归档数量：{zipFiles.Length}（显示最近 12 份）");
        builder.AppendLine($"最近导出：{zipFiles[0].LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();

        foreach (var file in zipFiles)
        {
            builder.AppendLine($"[{file.LastWriteTime:yyyy-MM-dd HH:mm:ss}] {file.Name}");
            builder.AppendLine($"大小：{FormatFileSize(file.Length)}");
            builder.AppendLine($"路径：{file.FullName}");
            builder.AppendLine();
        }

        ReportArchiveSummary = builder.ToString().TrimEnd();
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:F1} {units[unitIndex]}";
    }
}
