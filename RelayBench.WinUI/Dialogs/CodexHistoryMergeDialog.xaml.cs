using Microsoft.UI.Xaml.Controls;
using RelayBench.Core.Models;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class CodexHistoryMergeDialog : ContentDialog
{
    public CodexHistoryMergeDialog(string? statusText, string? detailText)
    {
        StatusText = Normalize(statusText, "Codex history status has not been checked yet.");
        DetailText = Normalize(detailText, "Refresh status before merging Codex history.");

        InitializeComponent();
    }

    public string StatusText { get; }

    public string DetailText { get; }

    public CodexChatMergeTarget SelectedTarget
        => OfficialTargetButton.IsChecked == true
            ? CodexChatMergeTarget.OfficialOpenAi
            : CodexChatMergeTarget.ThirdPartyCustom;

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
