using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels.AdvancedTesting;

public sealed class AdvancedLogEntryViewModel : ObservableObject
{
    public AdvancedLogEntryViewModel(DateTimeOffset timestamp, string level, string message)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
    }

    public DateTimeOffset Timestamp { get; }

    public string TimeText => Timestamp.ToString("HH:mm:ss");

    public string Level { get; }

    public string Message { get; }
}
