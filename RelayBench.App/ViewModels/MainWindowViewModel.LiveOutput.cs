namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int MaxLiveOutputCharacters = 60_000;
    private string _liveOutput = string.Empty;
    private string? _lastLiveStatusMessage;

    public string LiveOutput
    {
        get => _liveOutput;
        private set
        {
            if (SetProperty(ref _liveOutput, value))
            {
                RefreshProxyUnifiedOutput();
            }
        }
    }

    private void AppendLiveStatus(string? message)
    {
        var normalized = NormalizeLiveOutput(message);
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(_lastLiveStatusMessage, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _lastLiveStatusMessage = normalized;
        AppendLiveOutput("运行状态", normalized);
    }

    private void AppendModuleOutput(string title, params string?[] sections)
    {
        var normalizedSections = sections
            .Select(NormalizeLiveOutput)
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToArray();

        if (normalizedSections.Length == 0)
        {
            return;
        }

        AppendLiveOutput(title, string.Join("\n\n", normalizedSections));
    }

    private void AppendLiveOutput(string title, string? content)
    {
        var normalized = NormalizeLiveOutput(content);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var block = $"[{DateTime.Now:HH:mm:ss}] {title}\n{normalized}";
        var existing = string.IsNullOrWhiteSpace(_liveOutput)
            ? string.Empty
            : _liveOutput;

        var next = string.IsNullOrWhiteSpace(existing)
            ? block
            : existing + "\n\n" + block;

        if (next.Length > MaxLiveOutputCharacters)
        {
            next = next[^MaxLiveOutputCharacters..];
            var separatorIndex = next.IndexOf("\n\n", StringComparison.Ordinal);
            if (separatorIndex > 0 && separatorIndex < next.Length - 2)
            {
                next = next[(separatorIndex + 2)..];
            }
        }

        LiveOutput = next;
    }

    private static string? NormalizeLiveOutput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (normalized.Length > 6_000)
        {
            normalized = normalized[..6_000] + "\n...(输出已截断)";
        }

        return normalized;
    }
}
