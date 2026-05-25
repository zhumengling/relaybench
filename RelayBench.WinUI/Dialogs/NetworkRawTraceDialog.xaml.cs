using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class NetworkRawTraceDialog : ContentDialog
{
    public NetworkRawTraceDialog(string? traceTitle, string? traceContent)
    {
        TraceTitle = Normalize(traceTitle, "网络原始追踪");
        TraceContent = Normalize(traceContent, "No raw trace captured yet.");

        InitializeComponent();
    }

    public string TraceTitle { get; }

    public string TraceContent { get; }

    private void OnCopyTraceClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(TraceContent);
        Clipboard.SetContent(package);
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
