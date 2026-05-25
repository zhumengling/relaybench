using CommunityToolkit.Mvvm.Input;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class ApplicationCenterViewModel
{
    [RelayCommand]
    private async Task ExportClientChatHistoryAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"relaybench-chat-history-{DateTime.Now:yyyyMMdd-HHmmss}"
            };
            picker.FileTypeChoices.Add("RelayBench 聊天记录归档", [".zip"]);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                StatusText = "已取消聊天记录导出";
                return;
            }

            IsApplying = true;
            StatusText = "正在导出 Codex / Claude 聊天记录...";
            var result = await _chatHistoryArchiveService.ExportAsync(file.Path);
            ApplyChatHistoryArchiveResult(result, imported: false);
        }
        catch (Exception ex)
        {
            StatusText = $"聊天记录导出失败：{ex.Message}";
            StatusMessage = ex.ToString();
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private async Task ImportClientChatHistoryAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".zip");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                StatusText = "已取消聊天记录导入";
                return;
            }

            IsApplying = true;
            StatusText = "正在导入 Codex / Claude 聊天记录...";
            var result = await _chatHistoryArchiveService.ImportAsync(file.Path);
            ApplyChatHistoryArchiveResult(result, imported: true);

            if (result.Succeeded && result.CodexFileCount > 0)
            {
                await RefreshCodexHistoryStatusAsync();
                CodexHistoryMergeReviewRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"聊天记录导入失败：{ex.Message}";
            StatusMessage = ex.ToString();
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void ApplyChatHistoryArchiveResult(ClientChatHistoryArchiveResult result, bool imported)
    {
        StatusText = result.Summary;
        StatusMessage = string.Join(
            Environment.NewLine,
            new[]
            {
                imported ? "操作：导入聊天记录" : "操作：导出聊天记录",
                $"归档：{result.ArchivePath}",
                $"Codex 文件：{result.CodexFileCount}",
                $"Claude 文件：{result.ClaudeFileCount}",
                string.IsNullOrWhiteSpace(result.BackupDirectory) ? null : $"备份：{result.BackupDirectory}",
                string.IsNullOrWhiteSpace(result.Error) ? null : $"错误：{result.Error}"
            }.Where(static line => !string.IsNullOrWhiteSpace(line)));
    }
}
