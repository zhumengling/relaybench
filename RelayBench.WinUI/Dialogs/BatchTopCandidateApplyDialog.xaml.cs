using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class BatchTopCandidateApplyDialog : ContentDialog
{
    public BatchTopCandidateApplyDialogViewModel ViewModel { get; }

    public BatchTopCandidateApplyDialog(BatchTopCandidateApplyDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        CandidateList.SelectedIndex = ViewModel.Candidates.Count > 0 ? 0 : -1;
    }

    public bool CodexHistoryMergeRequested => ViewModel.CodexHistoryMergeRequested;

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.ApplyAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }
}
