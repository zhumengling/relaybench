using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isConfirmationDialogOpen;
    private string _confirmationDialogTitle = "确认操作";
    private string _confirmationDialogMessage = "请确认是否继续。";
    private string _confirmationDialogDetail = string.Empty;
    private string _confirmationDialogConfirmText = "确认";
    private string _confirmationDialogCancelText = "取消";
    private TaskCompletionSource<bool>? _confirmationDialogCompletionSource;

    public bool IsConfirmationDialogOpen
    {
        get => _isConfirmationDialogOpen;
        private set => SetProperty(ref _isConfirmationDialogOpen, value);
    }

    public string ConfirmationDialogTitle
    {
        get => _confirmationDialogTitle;
        private set => SetProperty(ref _confirmationDialogTitle, value);
    }

    public string ConfirmationDialogMessage
    {
        get => _confirmationDialogMessage;
        private set => SetProperty(ref _confirmationDialogMessage, value);
    }

    public string ConfirmationDialogDetail
    {
        get => _confirmationDialogDetail;
        private set => SetProperty(ref _confirmationDialogDetail, value);
    }

    public string ConfirmationDialogConfirmText
    {
        get => _confirmationDialogConfirmText;
        private set => SetProperty(ref _confirmationDialogConfirmText, value);
    }

    public string ConfirmationDialogCancelText
    {
        get => _confirmationDialogCancelText;
        private set => SetProperty(ref _confirmationDialogCancelText, value);
    }

    private Task<bool> ShowConfirmationDialogAsync(
        string title,
        string message,
        string detail,
        string confirmText = "确认",
        string cancelText = "取消")
    {
        _confirmationDialogCompletionSource?.TrySetResult(false);

        ConfirmationDialogTitle = string.IsNullOrWhiteSpace(title) ? "确认操作" : title.Trim();
        ConfirmationDialogMessage = string.IsNullOrWhiteSpace(message) ? "请确认是否继续。" : message.Trim();
        ConfirmationDialogDetail = detail?.Trim() ?? string.Empty;
        ConfirmationDialogConfirmText = string.IsNullOrWhiteSpace(confirmText) ? "确认" : confirmText.Trim();
        ConfirmationDialogCancelText = string.IsNullOrWhiteSpace(cancelText) ? "取消" : cancelText.Trim();

        _confirmationDialogCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsConfirmationDialogOpen = true;
        return _confirmationDialogCompletionSource.Task;
    }

    private Task ConfirmConfirmationDialogAsync()
    {
        IsConfirmationDialogOpen = false;
        _confirmationDialogCompletionSource?.TrySetResult(true);
        _confirmationDialogCompletionSource = null;
        return Task.CompletedTask;
    }

    private Task CancelConfirmationDialogAsync()
    {
        IsConfirmationDialogOpen = false;
        _confirmationDialogCompletionSource?.TrySetResult(false);
        _confirmationDialogCompletionSource = null;
        return Task.CompletedTask;
    }

    private Task<bool> ConfirmCodexChatMergeAsync(
        CodexChatMergeTarget target,
        string actionLabel)
    {
        var targetLabel = CodexChatMergeService.BuildTargetDisplayName(target);

        return ShowConfirmationDialogAsync(
            "是否合并所有 Codex 聊天记录",
            $"{actionLabel}后，是否把本机 Codex 中官方、第三方、不同中转站/不同 provider 名称下的历史聊天统一整理到“{targetLabel}”下？",
            "选择合并后，会修改本地 Codex 聊天索引和 session_meta 的 provider 归属，让不同入口、不同中转站和官方状态下产生的聊天集中显示在当前目标下。\n" +
            "操作前会自动备份数据库和被修改的记录文件；不会改写聊天正文内容。\n\n" +
            "如果只想切换接口配置，不整理历史聊天，请选择“仅切换配置”。",
            "合并全部聊天",
            "仅切换配置");
    }
}
