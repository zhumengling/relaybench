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
    [RelayCommand]
    private void RefreshReportArchive()
        => RefreshReportArchiveView();

    private void RefreshReportArchiveView()
    {
        ReportArchives.Clear();
        try
        {
            Directory.CreateDirectory(_exportRoot);
            var archives = EnumerateReportArchives(_exportRoot)
                .OrderByDescending(static item => item.LastWriteTimeUtc)
                .Take(12)
                .ToArray();

            foreach (var archive in archives)
            {
                ReportArchives.Add(archive);
            }

            ReportArchiveSummary = archives.Length == 0
                ? $"Archive root: {_exportRoot}\nNo report archives found."
                : BuildReportArchiveSummary(_exportRoot, archives);
        }
        catch (Exception ex)
        {
            ReportArchiveSummary = $"Archive root: {_exportRoot}\nFailed: {ex.Message}";
        }

        OnPropertyChanged(nameof(HasReportArchives));
    }

    private static IReadOnlyList<HistoryReportArchiveItem> EnumerateReportArchives(string exportRoot)
    {
        DirectoryInfo root = new(exportRoot);
        if (!root.Exists)
        {
            return [];
        }

        List<HistoryReportArchiveItem> archives = [];
        foreach (var file in SafeEnumerateFiles(root, "*.zip"))
        {
            archives.Add(new HistoryReportArchiveItem(
                file.Name,
                "Zip",
                FormatArchiveSize(file.Length),
                file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                file.FullName,
                file.LastWriteTimeUtc));
        }

        foreach (var directory in SafeEnumerateDirectories(root))
        {
            if (!IsReportArchiveDirectory(directory))
            {
                continue;
            }

            archives.Add(new HistoryReportArchiveItem(
                directory.Name,
                ResolveArchiveKind(directory),
                FormatArchiveSize(CalculateDirectorySize(directory)),
                directory.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                directory.FullName,
                directory.LastWriteTimeUtc));
        }

        return archives;
    }

    private static string BuildReportArchiveSummary(
        string exportRoot,
        IReadOnlyList<HistoryReportArchiveItem> archives)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Archive root: {exportRoot}");
        builder.AppendLine($"Showing {archives.Count} of latest 12 archives");
        builder.AppendLine($"Latest: {archives[0].LastWriteTimeText}");
        builder.AppendLine();

        foreach (var archive in archives)
        {
            builder.AppendLine($"[{archive.LastWriteTimeText}] {archive.Name}");
            builder.AppendLine($"Kind: {archive.Kind}");
            builder.AppendLine($"Size: {archive.Size}");
            builder.AppendLine($"Path: {archive.Path}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static bool IsReportArchiveDirectory(DirectoryInfo directory)
        => File.Exists(Path.Combine(directory.FullName, "index.md")) ||
           File.Exists(Path.Combine(directory.FullName, "index.json")) ||
           File.Exists(Path.Combine(directory.FullName, "report.md")) ||
           File.Exists(Path.Combine(directory.FullName, "payload.json"));

    private static string ResolveArchiveKind(DirectoryInfo directory)
    {
        if (File.Exists(Path.Combine(directory.FullName, "index.md")) ||
            File.Exists(Path.Combine(directory.FullName, "index.json")))
        {
            return "Bundle";
        }

        if (File.Exists(Path.Combine(directory.FullName, "report.md")) ||
            File.Exists(Path.Combine(directory.FullName, "payload.json")))
        {
            return "Report";
        }

        return "Folder";
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(DirectoryInfo directory, string pattern)
    {
        try
        {
            return directory.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static long CalculateDirectorySize(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(static file => file.Length);
        }
        catch
        {
            return 0;
        }
    }

}
