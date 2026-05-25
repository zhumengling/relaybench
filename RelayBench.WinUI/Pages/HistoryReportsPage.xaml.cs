using RelayBench.WinUI.ViewModels;
using RelayBench.WinUI.Dialogs;
using RelayBench.WinUI.Services;

namespace RelayBench.WinUI.Pages;

public sealed partial class HistoryReportsPage : PageBase
{
    public HistoryReportsViewModel ViewModel { get; } = new();

    public HistoryReportsPage()
    {
        InitializeComponent();
    }

    private async void OnOpenReportArchiveClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.RefreshReportArchiveCommand.Execute(null);
        var dialog = new ReportArchiveDialog(
            ViewModel.ReportArchives.ToArray(),
            ViewModel.ReportArchiveSummary).UseHostTheme(this);
        await dialog.ShowAsync();
    }

    private async void OnOpenProtocolEvidenceClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Button { Tag: HistoryProtocolResult result })
        {
            return;
        }

        var dialog = new HistoryEvidenceDialog(result).UseHostTheme(this);
        await dialog.ShowAsync();
    }
}
