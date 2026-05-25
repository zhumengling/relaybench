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
    private void StopChatStreaming()
        => StopStreaming();

    [RelayCommand]
    private void StopStreaming()
    {
        _cts?.Cancel();
        IsStreaming = false;
        StatusText = "已停止";
    }

    [RelayCommand]
    private void ClearChatSession()
        => ClearChat();

    [RelayCommand]
    private void ClearChat()
    {
        ClearChatEditStateAfterCommit();
        Messages.Clear();
        InputTokens = 0;
        OutputTokens = 0;
        CachedTokens = 0;
        CacheHitRate = "0.0%";
        TokensPerSecond = "--";
        MessageCount = 0;
        ClearPendingAttachments();
        StatusText = "已清空对话";

        // Also clear persisted messages for current session
        if (CurrentSession is not null)
        {
            _sessionStore.SaveMessages(CurrentSession.Id, []);
            CurrentSession.MessageCount = 0;
        }
    }

    [RelayCommand]
    private void ToggleChatSettingsPanel()
        => IsChatSettingsPanelOpen = !IsChatSettingsPanelOpen;

    [RelayCommand]
    private void CloseChatSettingsPanel()
        => IsChatSettingsPanelOpen = false;

    private sealed record ChatEditSnapshot(int StartIndex, IReadOnlyList<ChatMessageItem> RemovedMessages);
}
