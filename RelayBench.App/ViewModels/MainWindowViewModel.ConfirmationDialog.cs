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
        var sourceLabel = target == CodexChatMergeTarget.OfficialOpenAi
            ? "第三方"
            : "ChatGPT 官方";

        return ShowConfirmationDialogAsync(
            "是否一起带上聊天记录",
            $"{actionLabel}后，是否把已有 Codex 聊天一起整理到“{targetLabel}”下面继续查看？",
            $"如果选择“是”，当前在“{sourceLabel}”下面显示的聊天会一起整理到“{targetLabel}”下面。\n" +
            "如果选择“否”，只切换配置，不处理现有聊天。\n\n" +
            "操作前会自动备份本地聊天索引和状态文件，不会直接覆盖聊天内容。",
            "一起整理",
            "仅切换配置");
    }
}
