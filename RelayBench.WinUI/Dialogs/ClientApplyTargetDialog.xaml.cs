using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class ClientApplyTargetDialog : ContentDialog
{
    public ClientApplyTargetDialog(ApplicationCenterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
        RefreshButtonState();
    }

    public ApplicationCenterViewModel ViewModel { get; }

    public string DialogSummary => BuildSummary(ViewModel);

    public string DialogHint
        => "\u5e94\u7528\u524d\u4f1a\u6cbf\u7528\u5f53\u524d\u7aef\u70b9\u3001\u6a21\u578b\u548c\u534f\u8bae\u590d\u6838\u7ed3\u679c\uff1b\u8fd8\u539f\u4ec5\u9488\u5bf9\u5df2\u9009\u5ba2\u6237\u7aef\u6267\u884c\u3002";

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApplicationCenterViewModel.SelectedTargetCount)
            or nameof(ApplicationCenterViewModel.SelectedRestoreTargetCount)
            or nameof(ApplicationCenterViewModel.SelectedTargetOverviewText))
        {
            RefreshButtonState();
        }
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnSelectAllClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        foreach (var target in ViewModel.Targets.Where(static target => target.IsSelectable))
        {
            target.IsSelected = true;
        }

        RefreshButtonState();
    }

    private void OnInvertClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        foreach (var target in ViewModel.Targets.Where(static target => target.IsSelectable))
        {
            target.IsSelected = !target.IsSelected;
        }

        RefreshButtonState();
    }

    private void OnClearClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        foreach (var target in ViewModel.Targets)
        {
            target.IsSelected = false;
        }

        RefreshButtonState();
    }

    private void RefreshButtonState()
    {
        IsPrimaryButtonEnabled = ViewModel.SelectedTargetCount > 0;
        IsSecondaryButtonEnabled = ViewModel.SelectedRestoreTargetCount > 0;
        Bindings.Update();
    }

    private static string BuildSummary(ApplicationCenterViewModel viewModel)
        => string.Join(
            "  |  ",
            [
                viewModel.TargetOverviewText,
                viewModel.SelectedTargetOverviewText,
                viewModel.EndpointTakeoverText
            ]);
}
