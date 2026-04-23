namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public string ProxyUnifiedOutput
        => BuildProxyUnifiedOutput();

    private void RefreshProxyUnifiedOutput()
        => OnPropertyChanged(nameof(ProxyUnifiedOutput));

    private string BuildProxyUnifiedOutput()
        => BuildProxyUnifiedOutput(LiveOutput);

    internal static string BuildProxyUnifiedOutput(string? liveOutput)
        => NormalizeLiveOutput(liveOutput) ?? string.Empty;
}
