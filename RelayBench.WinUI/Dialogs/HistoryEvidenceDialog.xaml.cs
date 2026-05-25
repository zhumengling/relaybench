using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class HistoryEvidenceDialog : ContentDialog
{
    public HistoryEvidenceDialog(HistoryProtocolResult result)
    {
        EvidenceName = result.Name;
        Latency = result.Latency;
        Ttft = result.Ttft;
        State = result.State;
        EvidenceText = string.IsNullOrWhiteSpace(result.EvidenceText)
            ? "当前行没有额外证据文本。"
            : result.EvidenceText;
        Summary = $"{result.Name} · {result.State} · {result.Latency} · {result.Ttft}";
        EvidenceLengthText = $"{EvidenceText.Length:N0} chars";
        AccentToneVisibility = result.AccentToneVisibility;
        HealthyToneVisibility = result.HealthyToneVisibility;
        WarningToneVisibility = result.WarningToneVisibility;
        DangerToneVisibility = result.DangerToneVisibility;
        InitializeComponent();
    }

    public string EvidenceName { get; }

    public string Latency { get; }

    public string Ttft { get; }

    public string State { get; }

    public string Summary { get; }

    public string EvidenceText { get; }

    public string EvidenceLengthText { get; }

    public Visibility AccentToneVisibility { get; }

    public Visibility HealthyToneVisibility { get; }

    public Visibility WarningToneVisibility { get; }

    public Visibility DangerToneVisibility { get; }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(string.Join(
            Environment.NewLine,
            EvidenceName,
            $"状态：{State}",
            $"延迟：{Latency}",
            $"TTFT / 次级指标：{Ttft}",
            string.Empty,
            EvidenceText));
        Clipboard.SetContent(package);
    }
}
