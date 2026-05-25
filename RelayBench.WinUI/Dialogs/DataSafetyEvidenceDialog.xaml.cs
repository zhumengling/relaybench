using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class DataSafetyEvidenceDialog : ContentDialog
{
    public DataSafetyEvidenceDialog(SafetyTestResult result)
    {
        Result = result;
        CopyStatusText = "\u590d\u5236\u5185\u5bb9\u5305\u542b\u6458\u8981\u3001\u5224\u5b9a\u4f9d\u636e\u3001\u539f\u59cb\u8bf7\u6c42\u548c\u8131\u654f\u54cd\u5e94\u3002";
        InitializeComponent();
    }

    public SafetyTestResult Result { get; }

    public string CopyStatusText { get; private set; }

    private void OnCopyClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(BuildCopyContent());
        Clipboard.SetContent(package);
        CopyStatusText = $"\u5df2\u590d\u5236\u8bc1\u636e\u8be6\u60c5 - {DateTime.Now:HH:mm:ss}";
        Bindings.Update();
    }

    private string BuildCopyContent()
        => string.Join(
            Environment.NewLine,
            [
                "RelayBench Data Safety Evidence",
                $"Scenario: {Result.ScenarioName}",
                $"Scenario ID: {Result.ScenarioId}",
                $"Category: {Result.Category}",
                $"Risk: {Result.RiskLevelText}",
                $"Result: {Result.ResultText}",
                $"Score: {Result.RiskScore}/100",
                $"Captured: {Result.CompletedAtText}",
                string.Empty,
                "[Summary]",
                Result.DetailText,
                string.Empty,
                "[Checks]",
                Result.CheckEvidenceText,
                string.Empty,
                "[Request]",
                Result.RequestLogText,
                string.Empty,
                "[Response]",
                Result.SanitizedLogText
            ]);
}
