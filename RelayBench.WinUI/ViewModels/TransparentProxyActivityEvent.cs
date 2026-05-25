namespace RelayBench.WinUI.ViewModels;

public sealed record TransparentProxyActivityEvent(
    string TimeText,
    string Level,
    string Source,
    string Detail,
    string StatusText,
    string TraceId)
{
    public string BadgeText => string.IsNullOrWhiteSpace(Level) ? "INFO" : Level.Trim().ToUpperInvariant();
}
