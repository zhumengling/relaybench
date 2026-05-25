using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a conversation session in the Model Chat page.
/// Used as the item type for the session list and bound to ConversationSessionList control.
/// </summary>
public sealed partial class ChatSession : ObservableObject
{
    public string Id { get; set; } = string.Empty;

    [ObservableProperty] public partial string Title { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    [ObservableProperty] public partial DateTime LastMessageAtUtc { get; set; }

    [ObservableProperty] public partial int MessageCount { get; set; }

    [ObservableProperty] public partial string ModelName { get; set; } = "--";

    [ObservableProperty] public partial string TokenSummary { get; set; } = "0 tokens";

    /// <summary>
    /// When true, the session title is in inline-rename mode.
    /// </summary>
    [ObservableProperty] public partial bool IsRenaming { get; set; }

    /// <summary>
    /// The text being edited during inline rename.
    /// </summary>
    [ObservableProperty] public partial string RenameText { get; set; } = string.Empty;

    public ChatSession() { }

    public ChatSession(string id, string title, DateTime createdAtUtc)
    {
        Id = id;
        Title = title;
        CreatedAtUtc = createdAtUtc;
        LastMessageAtUtc = createdAtUtc;
    }

    public string DisplayTime => LastMessageAtUtc.ToLocalTime().Date == DateTime.Today
        ? LastMessageAtUtc.ToLocalTime().ToString("HH:mm")
        : LastMessageAtUtc.ToLocalTime().ToString("MM-dd");

    public string SummaryLine => $"{ModelName} \u00B7 {TokenSummary}";

    partial void OnLastMessageAtUtcChanged(DateTime value)
    {
        OnPropertyChanged(nameof(DisplayTime));
    }

    partial void OnModelNameChanged(string value)
    {
        OnPropertyChanged(nameof(SummaryLine));
    }

    partial void OnTokenSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(SummaryLine));
    }
}
