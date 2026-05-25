using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;



namespace RelayBench.WinUI.ViewModels;

public sealed partial class ModelChatViewModel : ObservableObject
{
    [RelayCommand]
    private async Task AddAttachmentAsync()
        => await AddFilesWithPickerAsync(ImageExtensions.Concat(TextExtensions), "附件");

    [RelayCommand]
    private async Task AddChatImageAttachmentAsync()
        => await AddFilesWithPickerAsync(ImageExtensions, "图片附件");

    [RelayCommand]
    private async Task AddChatTextFileAttachmentAsync()
        => await AddFilesWithPickerAsync(TextExtensions, "文本附件");

    [RelayCommand]
    private void AddChatAttachmentFiles(string[]? filePaths)
    {
        if (filePaths is null || filePaths.Length == 0)
        {
            AttachmentError = "没有可导入的附件";
            return;
        }

        var imported = 0;
        foreach (var filePath in filePaths)
        {
            if (ImportAttachment(filePath))
            {
                imported++;
            }
        }

        StatusText = imported > 0
            ? $"已添加 {imported} 个附件"
            : AttachmentError;
    }

    private async Task AddFilesWithPickerAsync(IEnumerable<string> extensions, string label)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var extension in extensions.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                picker.FileTypeFilter.Add(extension);
            }

            // WinUI 3 requires window handle initialization
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;

            var imported = 0;
            foreach (var file in files)
            {
                if (ImportAttachment(file.Path))
                {
                    imported++;
                }
            }

            if (imported > 1)
            {
                StatusText = $"已添加 {imported} 个附件";
            }
            else if (imported == 0)
            {
                StatusText = $"未选择{label}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"附件错误：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Model))
        {
            StatusText = "请先填写接口地址、API 密钥和模型";
            return;
        }

        StatusText = "正在拉取模型列表...";
        try
        {
            var settings = new ProxyEndpointSettings(
                BaseUrl.Trim(),
                ApiKey.Trim(),
                Model.Trim(),
                IgnoreTlsErrors: false,
                TimeoutSeconds: 20);
            var result = await _diagnosticsService.FetchModelsAsync(settings);
            AvailableModels.Clear();
            foreach (var model in result.Models.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                AddAvailableModel(model);
            }

            if (result.Success && AvailableModels.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(Model) ||
                    !AvailableModels.Contains(Model, StringComparer.OrdinalIgnoreCase))
                {
                    Model = AvailableModels[0];
                }

                await CacheModelCatalogResultAsync(settings, result);
                await GlobalEndpointProtocolProbeCoordinator.Instance.RecordEndpointAsync(BaseUrl, ApiKey, Model, AvailableModels);
                GlobalEndpointProtocolProbeCoordinator.Instance.EnqueueEndpointProbe(
                    BaseUrl,
                    ApiKey,
                    Model,
                    AvailableModels,
                    force: true);
                await LoadCachedModelsAsync();
                StatusText = $"已拉取 {AvailableModels.Count} 个模型";
            }
            else
            {
                StatusText = result.Summary;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"拉取模型失败：{ex.Message}";
        }
    }

    private async Task CacheModelCatalogResultAsync(
        ProxyEndpointSettings settings,
        ProxyModelCatalogResult result)
    {
        try
        {
            await _modelCacheService.SaveCatalogAsync(settings, result);
        }
        catch
        {
            // Best-effort cache warm-up only; the fetched model list remains usable.
        }
    }

    [RelayCommand]
    private async Task RefreshCurrentEndpointAsync()
    {
        if (!LoadPersistedEndpoint())
        {
            StatusText = "没有可同步的当前入口";
            return;
        }

        await LoadCachedModelsAsync();
        StatusText = "已同步当前入口";
    }

    [RelayCommand]
    private void RemoveAttachment(ChatAttachmentItem? item)
    {
        if (item is null) return;
        Attachments.Remove(item);
        PendingAttachments.Remove(item);
    }

    [RelayCommand]
    private void RemoveChatAttachment(ChatAttachmentItem? item)
        => RemoveAttachment(item);

    [RelayCommand]
    private void AttachFile(string filePath)
    {
        AttachmentError = "";
        if (!ImportAttachment(filePath) && string.IsNullOrWhiteSpace(AttachmentError))
        {
            AttachmentError = "附件导入失败";
        }
    }

    private bool ImportAttachment(string filePath)
    {
        var (item, error) = _attachmentService.Import(filePath);
        if (item is not null)
        {
            AddPendingAttachment(item);
            StatusText = $"已添加附件：{item.FileName}";
            AttachmentError = "";
            return true;
        }

        if (error is not null)
        {
            AttachmentError = error;
            StatusText = $"附件错误：{error}";
        }

        return false;
    }

    private void AddPendingAttachment(ChatAttachmentItem item)
    {
        Attachments.Add(item);
        PendingAttachments.Add(item);
    }

    private void ClearPendingAttachments()
    {
        Attachments.Clear();
        PendingAttachments.Clear();
        AttachmentError = "";
    }

    // ─── Phase 11: Export & Edit Commands ─────────────────────────────────

    [RelayCommand]
    private async Task ExportChatSessionMarkdownAsync()
        => await ExportMarkdownAsync();

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        if (Messages.Count == 0)
        {
            StatusText = "没有可导出的消息";
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("Markdown", [".md"]);
            picker.SuggestedFileName = $"chat-export-{DateTime.Now:yyyyMMdd-HHmmss}";

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var content = BuildMarkdownExport();
            await Windows.Storage.FileIO.WriteTextAsync(file, content);
            StatusText = $"已导出到：{file.Path}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportChatSessionTextAsync()
        => await ExportTextAsync();

    [RelayCommand]
    private async Task ExportTextAsync()
    {
        if (Messages.Count == 0)
        {
            StatusText = "没有可导出的消息";
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("Text", [".txt"]);
            picker.SuggestedFileName = $"chat-export-{DateTime.Now:yyyyMMdd-HHmmss}";

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var content = BuildTextExport();
            await Windows.Storage.FileIO.WriteTextAsync(file, content);
            StatusText = $"已导出到：{file.Path}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    private string BuildMarkdownExport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 对话导出");
        sb.AppendLine();
        sb.AppendLine($"> 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var msg in Messages)
        {
            var role = msg.IsUser ? "用户" : "助手";
            sb.AppendLine($"## {role}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(msg.Metrics))
            {
                sb.AppendLine($"- 指标：{msg.Metrics}");
                sb.AppendLine();
            }

            sb.AppendLine(BuildMarkdownExportContent(msg));
            AppendAttachmentExportMarkdown(sb, msg.Attachments);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string BuildTextExport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("对话导出");
        sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('=', 48));
        sb.AppendLine();

        foreach (var msg in Messages)
        {
            var role = msg.IsUser ? "用户" : "助手";
            sb.AppendLine($"[{role}]");
            if (!string.IsNullOrWhiteSpace(msg.Metrics))
            {
                sb.AppendLine($"指标：{msg.Metrics}");
            }

            sb.AppendLine(BuildTextExportContent(msg));
            AppendAttachmentExportText(sb, msg.Attachments);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildMarkdownExportContent(ChatMessageItem message)
    {
        var content = message.MarkdownContent;
        return string.IsNullOrWhiteSpace(content) ? "（空）" : content.TrimEnd();
    }

    private static string BuildTextExportContent(ChatMessageItem message)
    {
        if (!string.IsNullOrWhiteSpace(message.CodeBlock))
        {
            var content = string.IsNullOrWhiteSpace(message.Content)
                ? string.Empty
                : message.Content.TrimEnd() + Environment.NewLine;
            return content + message.CodeBlock.TrimEnd();
        }

        return string.IsNullOrWhiteSpace(message.Content) ? "（空）" : message.Content.TrimEnd();
    }

    private static void AppendAttachmentExportMarkdown(StringBuilder sb, IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("附件：");
        foreach (var attachment in attachments)
        {
            sb.AppendLine($"- {attachment.FileName}（{FormatAttachmentKind(attachment.Kind)}，{FormatAttachmentSize(attachment.SizeBytes)}）");
        }
    }

    private static void AppendAttachmentExportText(StringBuilder sb, IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        sb.AppendLine("附件：");
        foreach (var attachment in attachments)
        {
            sb.AppendLine($"- {attachment.FileName}（{FormatAttachmentKind(attachment.Kind)}，{FormatAttachmentSize(attachment.SizeBytes)}）");
        }
    }

    private static string FormatAttachmentKind(ChatAttachmentKind kind)
        => kind == ChatAttachmentKind.Image ? "图片" : "文本";

    private static string FormatAttachmentSize(long sizeBytes)
        => sizeBytes switch
        {
            >= 1024 * 1024 => $"{sizeBytes / 1024d / 1024d:F1} MB",
            >= 1024 => $"{sizeBytes / 1024d:F1} KB",
            _ => $"{sizeBytes} B"
        };

}
