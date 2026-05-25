using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class EndpointHistoryDialog : ContentDialog
{
    public EndpointHistoryDialog(IReadOnlyList<EndpointHistoryItem> historyItems, string targetLabel)
    {
        Items = historyItems
            .OrderByDescending(static item => item.LastUsedAt)
            .Select(static item => new EndpointHistoryDialogItem(item))
            .ToArray();
        TitleText = "接口历史";
        SummaryText = Items.Count == 0
            ? $"{targetLabel} 暂无保存的接口历史。"
            : $"已保存 {Items.Count} 个接口，选择一项即可填入 {targetLabel}。";
        HintText = "此对话框会隐藏 API 密钥，应用条目时仍使用本地历史中的完整值。";
        EmptyVisibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = Items.Count > 0;
        InitializeComponent();
    }

    public IReadOnlyList<EndpointHistoryDialogItem> Items { get; }

    public string TitleText { get; }

    public string SummaryText { get; }

    public string HintText { get; }

    public Visibility EmptyVisibility { get; }

    public EndpointHistoryItem? Result { get; private set; }

    public bool ClearRequested { get; private set; }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (HistoryList.SelectedItem is EndpointHistoryDialogItem item)
        {
            Result = item.Source;
            return;
        }

        args.Cancel = true;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ClearRequested = true;
        Hide();
    }
}

public sealed class EndpointHistoryDialogItem
{
    public EndpointHistoryDialogItem(EndpointHistoryItem source)
    {
        Source = source;
        BaseUrl = string.IsNullOrWhiteSpace(source.BaseUrl) ? "--" : source.BaseUrl;
        ModelSummary = BuildModelSummary(source);
        MaskedApiKey = EndpointHistoryStore.MaskApiKey(source.ApiKey);
        UsedAtText = source.LastUsedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public EndpointHistoryItem Source { get; }

    public string BaseUrl { get; }

    public string ModelSummary { get; }

    public string MaskedApiKey { get; }

    public string UsedAtText { get; }

    private static string BuildModelSummary(EndpointHistoryItem source)
    {
        var models = source.Models?
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        if (models is { Length: > 0 })
        {
            var suffix = source.Models!.Count > models.Length ? $" +{source.Models.Count - models.Length}" : string.Empty;
            return $"{source.Model} | {string.Join(", ", models)}{suffix}";
        }

        return string.IsNullOrWhiteSpace(source.Model) ? "--" : source.Model;
    }
}
