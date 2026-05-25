using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RelayBench.WinUI.Dialogs;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.ViewModels;
using Windows.System;

namespace RelayBench.WinUI.Pages.Controls;

/// <summary>
/// A left-rail control that displays a list of conversation sessions
/// with add, select, delete, and inline-rename functionality.
/// </summary>
public sealed partial class ConversationSessionList : UserControl
{
    /// <summary>
    /// The collection of chat sessions to display.
    /// </summary>
    public ObservableCollection<ChatSession> Sessions
    {
        get => (ObservableCollection<ChatSession>)GetValue(SessionsProperty);
        set => SetValue(SessionsProperty, value);
    }

    public static readonly DependencyProperty SessionsProperty =
        DependencyProperty.Register(
            nameof(Sessions),
            typeof(ObservableCollection<ChatSession>),
            typeof(ConversationSessionList),
            new PropertyMetadata(null));

    /// <summary>
    /// The currently selected session.
    /// </summary>
    public ChatSession? SelectedSession
    {
        get => (ChatSession?)GetValue(SelectedSessionProperty);
        set => SetValue(SelectedSessionProperty, value);
    }

    public static readonly DependencyProperty SelectedSessionProperty =
        DependencyProperty.Register(
            nameof(SelectedSession),
            typeof(ChatSession),
            typeof(ConversationSessionList),
            new PropertyMetadata(null));

    /// <summary>
    /// Raised when a session is selected from the list.
    /// </summary>
    public event EventHandler<ChatSession>? SessionSelected;

    /// <summary>
    /// Raised when the user clicks the New Session button.
    /// </summary>
    public event EventHandler? SessionCreated;

    /// <summary>
    /// Raised when a session is deleted (after confirmation).
    /// </summary>
    public event EventHandler<ChatSession>? SessionDeleted;

    /// <summary>
    /// Raised when a session rename is committed.
    /// </summary>
    public event EventHandler<ChatSession>? SessionRenamed;

    public ConversationSessionList()
    {
        InitializeComponent();
    }

    private void OnNewSessionClick(object sender, RoutedEventArgs e)
    {
        SessionCreated?.Invoke(this, EventArgs.Empty);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionListView.SelectedItem is ChatSession session)
        {
            SessionSelected?.Invoke(this, session);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string sessionId)
            return;

        var sessions = Sessions;
        if (sessions is null)
            return;

        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null)
            return;

        var dialog = ConfirmationDialog.CreateDestructive(
            "\u5220\u9664\u5bf9\u8bdd\uff1f",
            $"\"{session.Title}\" \u5c06\u4ece\u672c\u5730\u4f1a\u8bdd\u5217\u8868\u4e2d\u79fb\u9664\u3002",
            "\u6b64\u64cd\u4f5c\u4e0d\u4f1a\u5f71\u54cd\u5df2\u5bfc\u51fa\u7684\u62a5\u544a\u6216\u900f\u660e\u4ee3\u7406\u8bbe\u7f6e\u3002");
        dialog.UseHostTheme(this);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            SessionDeleted?.Invoke(this, session);
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string sessionId)
            return;

        var session = FindSessionById(sessionId);
        if (session is null) return;

        // Enter rename mode
        session.RenameText = session.Title;
        session.IsRenaming = true;
    }

    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not string sessionId)
            return;

        if (e.Key == VirtualKey.Enter)
        {
            CommitRename(sessionId);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CancelRename(sessionId);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not string sessionId)
            return;

        // Commit on focus loss
        CommitRename(sessionId);
    }

    private void CommitRename(string sessionId)
    {
        var session = FindSessionById(sessionId);
        if (session is null || !session.IsRenaming) return;

        var newTitle = session.RenameText?.Trim();
        if (!string.IsNullOrEmpty(newTitle) && newTitle != session.Title)
        {
            session.Title = newTitle;
            SessionRenamed?.Invoke(this, session);
        }
        session.IsRenaming = false;
    }

    private void CancelRename(string sessionId)
    {
        var session = FindSessionById(sessionId);
        if (session is null) return;

        session.IsRenaming = false;
        session.RenameText = session.Title;
    }

    private ChatSession? FindSessionById(string sessionId)
    {
        var sessions = Sessions;
        return sessions?.FirstOrDefault(s => s.Id == sessionId);
    }
}
