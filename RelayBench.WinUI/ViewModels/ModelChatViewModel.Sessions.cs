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
    private void NewChatSession()
        => CreateNewSession();

    [RelayCommand]
    private void CreateNewSession()
    {
        CancelChatEdit(restoreMessages: true);

        // Save current session messages before creating new one
        SaveCurrentSessionMessages();

        var session = new ChatSession(
            Guid.NewGuid().ToString("N"),
            "新会话",
            DateTime.UtcNow)
        {
            LastMessageAtUtc = DateTime.UtcNow,
            MessageCount = 0
        };

        _sessionStore.CreateSession(session);
        Sessions.Insert(0, session);
        CurrentSession = session;

        // Clear messages for the new session
        Messages.Clear();
        ClearPendingAttachments();
        InputTokens = 0;
        OutputTokens = 0;
        CachedTokens = 0;
        CacheHitRate = "0.0%";
        TokensPerSecond = "--";
        MessageCount = 0;
        StatusText = "已创建新会话";
    }

    [RelayCommand]
    private void DeleteChatSession(ChatSession? session)
        => DeleteSession(session ?? CurrentSession);

    [RelayCommand]
    private void DeleteSession(ChatSession? session)
    {
        if (session is null) return;

        _sessionStore.DeleteSession(session.Id);
        Sessions.Remove(session);

        // If we deleted the current session, switch to first available or create new
        if (CurrentSession?.Id == session.Id)
        {
            if (Sessions.Count > 0)
            {
                SwitchToSession(Sessions[0]);
            }
            else
            {
                CreateNewSession();
            }
        }
    }

    [RelayCommand]
    private void SwitchSession(ChatSession? session)
    {
        if (session is null || session.Id == CurrentSession?.Id) return;
        SwitchToSession(session);
    }

    private void SwitchToSession(ChatSession session)
    {
        CancelChatEdit(restoreMessages: true);

        // Save current session messages
        SaveCurrentSessionMessages();

        // Load target session messages
        CurrentSession = session;
        Messages.Clear();
        ClearPendingAttachments();
        var messages = _sessionStore.LoadMessages(session.Id);
        foreach (var msg in messages)
            Messages.Add(msg);

        MessageCount = Messages.Count;
        InputTokens = 0;
        OutputTokens = 0;
        CachedTokens = 0;
        CacheHitRate = "0.0%";
        TokensPerSecond = "--";
        StatusText = $"已切换到：{session.Title}";
    }

    private void SaveCurrentSessionMessages()
    {
        if (CurrentSession is null || Messages.Count == 0) return;

        _sessionStore.SaveMessages(CurrentSession.Id, Messages.ToList());
        CurrentSession.MessageCount = Messages.Count;
        CurrentSession.LastMessageAtUtc = DateTime.UtcNow;
    }

    // ─── Inline Rename ────────────────────────────────────────────────────

    [RelayCommand]
    private void BeginRenameChatSession(ChatSession? session)
        => BeginRename(session ?? CurrentSession);

    [RelayCommand]
    private void BeginRename(ChatSession? session)
    {
        if (session is null) return;
        session.RenameText = session.Title;
        session.IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRenameChatSession(ChatSession? session)
        => CommitRename(session ?? CurrentSession);

    [RelayCommand]
    private void CommitRename(ChatSession? session)
    {
        if (session is null) return;

        // If still in rename mode, apply the rename text
        if (session.IsRenaming)
        {
            var newTitle = session.RenameText?.Trim();
            if (!string.IsNullOrEmpty(newTitle) && newTitle != session.Title)
            {
                session.Title = newTitle;
            }
            session.IsRenaming = false;
        }

        // Persist the current title to SQLite
        _sessionStore.RenameSession(session.Id, session.Title);
    }

    [RelayCommand]
    private void CancelRenameChatSession(ChatSession? session)
        => CancelRename(session ?? CurrentSession);

    [RelayCommand]
    private void CancelRename(ChatSession? session)
    {
        if (session is null) return;
        session.IsRenaming = false;
        session.RenameText = session.Title;
    }

    // ─── Phase 9: Preset Commands ─────────────────────────────────────────

    /// <summary>
    /// Loads presets from the store. Called on page initialization.
    /// </summary>
}
