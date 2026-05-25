using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class MultiModelSelectionDialog : ContentDialog
{
    public MultiModelSelectionDialog(
        IReadOnlyList<string> candidates,
        IReadOnlyCollection<string> selectedModels,
        string currentModel)
    {
        var selected = selectedModels
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        CurrentModelText = string.IsNullOrWhiteSpace(currentModel) ? "--" : currentModel.Trim();
        Items = candidates
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static model => model, StringComparer.OrdinalIgnoreCase)
            .Select(model => new MultiModelSelectionItem(model, selected.Contains(model), IsCurrentModel(model, CurrentModelText)))
            .ToArray();
        HintText = Items.Count == 0
            ? "\u5f53\u524d\u6ca1\u6709\u53ef\u9009\u6a21\u578b\u3002\u5148\u62c9\u53d6\u6a21\u578b\u6216\u8f93\u5165\u4e3b\u6a21\u578b\u540e\uff0c\u518d\u9009\u62e9\u6df1\u5ea6\u591a\u6a21\u578b\u6d4b\u901f\u5bf9\u8c61\u3002"
            : "\u786e\u5b9a\u540e\uff0c\u6df1\u5ea6\u6d4b\u8bd5\u4f1a\u5728\u57fa\u7840\u63a2\u9488\u540e\u8ffd\u52a0\u591a\u6a21\u578b tok/s \u6d4b\u901f\uff1b\u4e0d\u4f1a\u751f\u6210\u5047\u6a21\u578b\u6216\u5360\u4f4d\u6570\u636e\u3002";

        InitializeComponent();
        RefreshState();
    }

    public IReadOnlyList<MultiModelSelectionItem> Items { get; }

    public IReadOnlyList<string> Result { get; private set; } = [];

    public string CurrentModelText { get; }

    public string HintText { get; }

    public string CandidateCountText => Items.Count.ToString();

    public string SelectedCountText => Items.Count(static item => item.IsSelected).ToString();

    public string SummaryText
        => Items.Count == 0
            ? "\u5019\u9009 0 | \u5df2\u9009 0"
            : $"\u5019\u9009 {Items.Count} | \u5df2\u9009 {Items.Count(static item => item.IsSelected)}";

    private void OnSelectAllClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }

        RefreshState();
    }

    private void OnInvertClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = !item.IsSelected;
        }

        RefreshState();
    }

    private void OnClearClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        RefreshState();
    }

    private void OnModelCheckChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => RefreshState();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        => Result = SelectedModels();

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        => Result = [];

    private IReadOnlyList<string> SelectedModels()
        => Items
            .Where(static item => item.IsSelected)
            .Select(static item => item.Model)
            .ToArray();

    private void RefreshState()
    {
        IsPrimaryButtonEnabled = Items.Count > 0;
        IsSecondaryButtonEnabled = Items.Any(static item => item.IsSelected);
        Bindings.Update();
    }

    private static bool IsCurrentModel(string model, string current)
        => !string.IsNullOrWhiteSpace(current) &&
           !string.Equals(current, "--", StringComparison.Ordinal) &&
           string.Equals(model, current, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class MultiModelSelectionItem : ObservableObject
{
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public MultiModelSelectionItem(string model, bool isSelected, bool isCurrent)
    {
        Model = model;
        IsSelected = isSelected;
        IsCurrent = isCurrent;
    }

    public string Model { get; }

    public bool IsCurrent { get; }

    public string DetailText => IsCurrent ? "\u5f53\u524d\u4e3b\u6a21\u578b" : "\u5019\u9009\u6d4b\u901f\u6a21\u578b";

    public string BadgeText => IsCurrent ? "\u5f53\u524d" : "\u5019\u9009";

    public Visibility CurrentModelVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CandidateVisibility => IsCurrent ? Visibility.Collapsed : Visibility.Visible;
}
