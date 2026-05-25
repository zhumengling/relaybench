using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.ViewModels;
using Windows.System;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class BatchSiteEditorDialog : UserControl
{
    private readonly ResponsiveLayoutService _responsiveLayout;

    public BatchComparisonViewModel ViewModel { get; }
    public BatchSiteEditorViewModel SiteEditor => ViewModel.SiteEditor;
    public event EventHandler? CloseRequested;

    public BatchSiteEditorDialog(BatchComparisonViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _responsiveLayout = ResponsiveLayoutService.AttachDialog(DialogRoot);
    }

    private void ExistingSiteRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        SelectExistingSite(sender);
    }

    private void ExistingSiteRow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ShouldActivateRowFromKeyboard(sender, e) && SelectExistingSite(sender))
        {
            e.Handled = true;
        }
    }

    private bool SelectExistingSite(object sender)
    {
        if (sender is FrameworkElement { DataContext: BatchSiteEntry site })
        {
            SiteEditor.SelectedSite = site;
            return true;
        }

        return false;
    }

    private void SiteGroup_Tapped(object sender, TappedRoutedEventArgs e)
    {
        LoadDraftFromGroup(sender);
    }

    private void SiteGroup_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ShouldActivateRowFromKeyboard(sender, e) && LoadDraftFromGroup(sender))
        {
            e.Handled = true;
        }
    }

    private bool LoadDraftFromGroup(object sender)
    {
        if (sender is FrameworkElement { DataContext: BatchSiteGroupSummary group })
        {
            SiteEditor.LoadDraftFromGroup(group);
            return true;
        }

        return false;
    }

    private void DraftRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        SelectDraftRow(sender);
    }

    private void DraftRow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ShouldActivateRowFromKeyboard(sender, e) && SelectDraftRow(sender))
        {
            e.Handled = true;
        }
    }

    private bool SelectDraftRow(object sender)
    {
        if (TryGetDraftRow(sender, out var row))
        {
            SiteEditor.SelectedDraftRow = row;
            return true;
        }

        return false;
    }

    private static bool ShouldActivateRowFromKeyboard(object sender, KeyRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
        {
            return false;
        }

        return e.Key is VirtualKey.Enter or VirtualKey.Space;
    }

    private void OnDeleteDraftRowClick(object sender, RoutedEventArgs e)
    {
        if (TryGetDraftRow(sender, out var row))
        {
            SiteEditor.DeleteProxyBatchTemplateRowCommand.Execute(row);
        }
    }

    private async void OnFetchDraftRowModelsClick(object sender, RoutedEventArgs e)
    {
        if (TryGetDraftRow(sender, out var row))
        {
            SiteEditor.SelectedDraftRow = row;
            await ViewModel.FetchProxyBatchTemplateRowModelsCommand.ExecuteAsync(row);
        }
    }

    private void OnDraftModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            CommitDraftModelComboValue(combo, allowEmpty: false);
        }
    }

    private void OnDraftModelComboLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            CommitDraftModelComboValue(combo, allowEmpty: true);
        }
    }

    private void OnDraftModelDropDownClosed(object? sender, object e)
    {
        if (sender is ComboBox combo)
        {
            CommitDraftModelComboValue(combo, allowEmpty: false);
        }
    }

    private static void CommitDraftModelComboValue(ComboBox combo, bool allowEmpty)
    {
        if (!TryGetDraftRow(combo, out var row))
        {
            return;
        }

        var selectedText = combo.SelectedItem as string;
        var text = !string.IsNullOrWhiteSpace(selectedText)
            ? selectedText
            : combo.Text?.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            if (allowEmpty && !string.IsNullOrWhiteSpace(row.Model) && string.IsNullOrWhiteSpace(combo.Text))
            {
                row.Model = string.Empty;
            }

            return;
        }

        if (!string.Equals(row.Model, text, StringComparison.Ordinal))
        {
            row.Model = text;
        }

        if (!row.AvailableModels.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            row.AvailableModels.Add(text);
        }
    }

    private static bool TryGetDraftRow(object sender, out BatchSiteDraftRow row)
    {
        row = null!;
        if (sender is not FrameworkElement element)
        {
            return false;
        }

        var resolved = element.Tag as BatchSiteDraftRow ??
                       element.DataContext as BatchSiteDraftRow;
        if (resolved is null)
        {
            return false;
        }

        row = resolved;
        return true;
    }

    private void OnBringCurrentEndpointClick(object sender, RoutedEventArgs e)
    {
        SiteEditor.BringCurrentEndpointToDraft(ViewModel.BaseUrl, ViewModel.ApiKey, ViewModel.Model);
    }

}
