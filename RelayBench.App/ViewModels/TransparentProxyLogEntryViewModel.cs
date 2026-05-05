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
        IngressKind = string.IsNullOrWhiteSpace(entry.IngressKind) ? "UnifiedLocalEndpoint" : entry.IngressKind;
        SourceApplication = string.IsNullOrWhiteSpace(entry.SourceApplication) ? "本地统一出口" : entry.SourceApplication;
        CaptureMode = string.IsNullOrWhiteSpace(entry.CaptureMode) ? "显式 Base URL" : entry.CaptureMode;
        TargetHost = string.IsNullOrWhiteSpace(entry.TargetHost) ? "-" : entry.TargetHost;
        WasTunnelOnly = entry.WasTunnelOnly;
        SourceText = WasTunnelOnly ? $"{SourceApplication} · tunnel" : SourceApplication;
        DetailText =
            $"Time: {entry.Timestamp:yyyy-MM-dd HH:mm:ss zzz}\n" +
            $"Request ID: {RequestId}\n" +
            $"Ingress: {IngressKind}\n" +
            $"Source: {SourceApplication}\n" +
            $"Capture: {CaptureMode}\n" +
            $"Target host: {TargetHost}\n" +
            $"Tunnel only: {(WasTunnelOnly ? "yes" : "no")}\n" +
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

    public string IngressKind { get; }

    public string SourceApplication { get; }

    public string CaptureMode { get; }

    public string TargetHost { get; }

    public bool WasTunnelOnly { get; }

    public string SourceText { get; }

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
