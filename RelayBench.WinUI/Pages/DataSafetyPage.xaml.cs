using Microsoft.UI.Xaml;
using RelayBench.WinUI.Storage;
using RelayBench.WinUI.Dialogs;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Pages;

public sealed partial class DataSafetyPage : PageBase
{
    public DataSafetyViewModel ViewModel { get; } = new();

    public DataSafetyPage()
    {
        InitializeComponent();
    }

    private async void OnOpenEndpointHistoryDialogClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var store = new EndpointHistoryStore();
        var items = await store.LoadAsync();
        var dialog = new EndpointHistoryDialog(items, "Data Safety").UseHostTheme(this);
        var result = await dialog.ShowAsync();
        if (dialog.ClearRequested)
        {
            await store.ClearAsync();
            ViewModel.EndpointHistory.Clear();
            return;
        }

        if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary && dialog.Result is not null)
        {
            ViewModel.ApplyHistoryEntryCommand.Execute(dialog.Result);
        }
    }

    private async void OnOpenEvidenceDialogClick(object sender, RoutedEventArgs e)
    {
        var result = (sender as FrameworkElement)?.Tag as SafetyTestResult
            ?? ViewModel.SelectedResult;
        if (result is null)
        {
            return;
        }

        ViewModel.SelectedResult = result;
        var dialog = new DataSafetyEvidenceDialog(result).UseHostTheme(this);
        await dialog.ShowAsync();
    }

    private async void OnConfirmStopTestClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsTesting)
        {
            return;
        }

        var dialog = new ConfirmationDialog(
            "\u505c\u6b62\u6570\u636e\u5b89\u5168\u6d4b\u8bd5",
            "\u786e\u5b9a\u8981\u505c\u6b62\u5f53\u524d\u6570\u636e\u5b89\u5168\u6d4b\u8bd5\u5417\uff1f",
            "\u6b63\u5728\u8fd0\u884c\u7684\u8bf7\u6c42\u4f1a\u5728\u5f53\u524d\u6b65\u9aa4\u7ed3\u675f\u6216\u53d6\u6d88\u540e\u505c\u6b62\uff0c\u5df2\u5b8c\u6210\u7684\u7ed3\u679c\u4f1a\u4fdd\u7559\u3002",
            "\u505c\u6b62\u6d4b\u8bd5",
            "\u7ee7\u7eed\u8fd0\u884c",
            "\u8bf7\u5148\u786e\u8ba4",
            "\uE71A").UseHostTheme(this);

        var result = await dialog.ShowAsync();
        if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            ViewModel.StopTestCommand.Execute(null);
        }
    }
}
