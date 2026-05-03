using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

public sealed class TransparentProxyLogEntryViewModel
{
    public TransparentProxyLogEntryViewModel(TransparentProxyLogEntry entry)
    {
        TimeText = entry.Timestamp.ToString("HH:mm:ss");
        Level = entry.Level;
        Method = entry.Method;
        Path = entry.Path;
        RouteName = entry.RouteName;
        StatusCode = entry.StatusCode;
        ElapsedText = entry.ElapsedMs <= 0 ? "-" : $"{entry.ElapsedMs} ms";
        Message = entry.Message;
    }

    public string TimeText { get; }

    public string Level { get; }

    public string Method { get; }

    public string Path { get; }

    public string RouteName { get; }

    public int StatusCode { get; }

    public string ElapsedText { get; }

    public string Message { get; }

    public string LevelBrush
        => Level switch
        {
            "ERROR" => "#DC2626",
            "WARN" => "#D97706",
            "CACHE" => "#0EA5E9",
            _ => "#059669"
        };
}
