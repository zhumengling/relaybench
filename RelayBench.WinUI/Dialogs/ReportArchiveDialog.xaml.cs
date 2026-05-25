using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RelayBench.Services.Infrastructure;
using RelayBench.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class ReportArchiveDialog : ContentDialog
{
    private readonly string _exportRoot;

    public ReportArchiveDialog(
        IReadOnlyList<HistoryReportArchiveItem> archives,
        string summaryText)
    {
        Items = archives
            .OrderByDescending(static item => item.LastWriteTimeUtc)
            .Select(static item => new ReportArchiveDialogItem(item))
            .ToArray();
        SummaryText = string.IsNullOrWhiteSpace(summaryText)
            ? "暂无报告归档摘要。"
            : summaryText;
        HeaderSummary = Items.Count == 0
            ? "暂无导出的报告归档。"
            : $"最近 {Items.Count} 份报告归档。可打开 bundle、复制路径，或复制不含密钥的归档摘要。";
        EmptyVisibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _exportRoot = ResolveExportRoot(SummaryText);
        InitializeComponent();
    }

    public IReadOnlyList<ReportArchiveDialogItem> Items { get; }

    public string HeaderSummary { get; }

    public string SummaryText { get; }

    public Visibility EmptyVisibility { get; }

    private void OnCopySummaryClick(object sender, RoutedEventArgs e)
        => CopyText(SummaryText);

    private void OnOpenExportRootClick(object sender, RoutedEventArgs e)
        => OpenPath(_exportRoot);

    private void OnOpenArchiveClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string path })
        {
            OpenPath(path);
        }
    }

    private void OnCopyArchivePathClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string path })
        {
            CopyText(path);
        }
    }

    private void OnCopyArchiveSummaryClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string summary })
        {
            CopyText(summary);
        }
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                return;
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("ReportArchiveDialog.OpenPath", ex);
        }
    }

    private static string ResolveExportRoot(string summaryText)
    {
        var firstLine = summaryText
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .FirstOrDefault(static line => line.StartsWith("报告目录：", StringComparison.Ordinal));
        var root = firstLine?["报告目录：".Length..].Trim();
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RelayBench",
                "WinUI",
                "exports")
            : root;
    }

    private void FlyoutButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button button ||
            e.Key is not (VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA or VirtualKey.Application))
        {
            return;
        }

        button.Flyout?.ShowAt(button);
        e.Handled = true;
    }
}

public sealed class ReportArchiveDialogItem
{
    public ReportArchiveDialogItem(HistoryReportArchiveItem source)
    {
        Name = source.Name;
        Kind = source.Kind;
        Size = source.Size;
        LastWriteTimeText = source.LastWriteTimeText;
        Path = source.Path;
        Summary = string.Join(
            Environment.NewLine,
            source.Name,
            $"类型：{source.Kind}",
            $"大小：{source.Size}",
            $"更新时间：{source.LastWriteTimeText}",
            $"路径：{source.Path}");
        KindGlyph = ResolveGlyph(source.Kind);
    }

    public string Name { get; }

    public string Kind { get; }

    public string Size { get; }

    public string LastWriteTimeText { get; }

    public string Path { get; }

    public string Summary { get; }

    public string KindGlyph { get; }

    public Visibility ZipKindVisibility => VisibleWhen(IsKind("Zip"));

    public Visibility BundleKindVisibility => VisibleWhen(IsKind("Bundle"));

    public Visibility ReportKindVisibility => VisibleWhen(IsKind("Report"));

    public Visibility FallbackKindVisibility => VisibleWhen(
        !IsKind("Zip") &&
        !IsKind("Bundle") &&
        !IsKind("Report"));

    private bool IsKind(string kind)
        => string.Equals(Kind, kind, StringComparison.OrdinalIgnoreCase);

    private static Visibility VisibleWhen(bool visible)
        => visible ? Visibility.Visible : Visibility.Collapsed;

    private static string ResolveGlyph(string kind)
    {
        return kind switch
        {
            "Zip" => "\uE7B8",
            "Bundle" => "\uE8A7",
            "Report" => "\uE8A5",
            _ => "\uE838"
        };
    }

}
