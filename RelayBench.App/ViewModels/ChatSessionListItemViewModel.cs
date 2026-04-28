using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class ChatSessionListItemViewModel : ObservableObject
{
    private string _title;
    private DateTimeOffset _updatedAt;
    private int _messageCount;

    public ChatSessionListItemViewModel(string sessionId, string title, DateTimeOffset updatedAt, int messageCount)
    {
        SessionId = sessionId;
        _title = title;
        _updatedAt = updatedAt;
        _messageCount = messageCount;
    }

    public string SessionId { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (SetProperty(ref _updatedAt, value))
            {
                OnPropertyChanged(nameof(UpdatedAtLabel));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public int MessageCount
    {
        get => _messageCount;
        set
        {
            if (SetProperty(ref _messageCount, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string UpdatedAtLabel => UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");

    public string Summary => $"{UpdatedAtLabel} · {MessageCount} \u6761\u6d88\u606f";
}
