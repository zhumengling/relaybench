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
        ModelName = string.IsNullOrWhiteSpace(entry.ModelName) ? "-" : entry.ModelName;
        RouteName = entry.RouteName;
        StatusCode = entry.StatusCode;
        ElapsedText = entry.ElapsedMs <= 0 ? "-" : $"{entry.ElapsedMs} ms";
        Message = entry.Message;
        RequestId = string.IsNullOrWhiteSpace(entry.RequestId) ? "-" : entry.RequestId;
        WireApi = string.IsNullOrWhiteSpace(entry.WireApi) ? "-" : entry.WireApi;
        AttemptSummary = string.IsNullOrWhiteSpace(entry.AttemptSummary) ? "-" : entry.AttemptSummary;
        DetailText =
            $"Time: {entry.Timestamp:yyyy-MM-dd HH:mm:ss zzz}\n" +
            $"Request ID: {RequestId}\n" +
            $"Method: {Method}\n" +
            $"Path: {Path}\n" +
            $"Model: {ModelName}\n" +
            $"Route: {RouteName}\n" +
            $"Wire API: {WireApi}\n" +
            $"Status: {StatusCode}\n" +
            $"Elapsed: {ElapsedText}\n" +
            $"Attempts: {AttemptSummary}\n" +
            $"Message: {Message}";
    }

    public string TimeText { get; }

    public string Level { get; }

    public string Method { get; }

    public string Path { get; }

    public string ModelName { get; }

    public string RouteName { get; }

    public int StatusCode { get; }

    public string ElapsedText { get; }

    public string Message { get; }

    public string RequestId { get; }

    public string WireApi { get; }

    public string AttemptSummary { get; }

    public string DetailText { get; }

    public string LevelBrush
        => Level switch
        {
            "ERROR" => "#DC2626",
            "WARN" => "#D97706",
            "CACHE" => "#0EA5E9",
            _ => "#059669"
        };
}
