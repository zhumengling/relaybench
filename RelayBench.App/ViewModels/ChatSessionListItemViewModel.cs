using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class ChatSessionListItemViewModel : ObservableObject
{
    private string _title;
    private DateTimeOffset _updatedAt;
    private int _messageCount;
    private bool _isManualTitle;
    private bool _isRenaming;
    private string _draftTitle;

    public ChatSessionListItemViewModel(
        string sessionId,
        string title,
        DateTimeOffset updatedAt,
        int messageCount,
        bool isManualTitle = false)
    {
        SessionId = sessionId;
        _title = title;
        _updatedAt = updatedAt;
        _messageCount = messageCount;
        _isManualTitle = isManualTitle;
        _draftTitle = title;
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

    public bool IsManualTitle
    {
        get => _isManualTitle;
        set => SetProperty(ref _isManualTitle, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (SetProperty(ref _isRenaming, value))
            {
                OnPropertyChanged(nameof(IsNotRenaming));
            }
        }
    }

    public bool IsNotRenaming => !IsRenaming;

    public string DraftTitle
    {
        get => _draftTitle;
        set => SetProperty(ref _draftTitle, value);
    }

    public string UpdatedAtLabel => UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");

    public string Summary => $"{UpdatedAtLabel} · {MessageCount} \u6761\u6d88\u606f";
}
