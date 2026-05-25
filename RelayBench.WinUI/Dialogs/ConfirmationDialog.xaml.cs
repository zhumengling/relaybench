using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class ConfirmationDialog : ContentDialog
{
    public ConfirmationDialog(
        string heading,
        string message,
        string detail = "",
        string confirmText = "\u786e\u8ba4",
        string cancelText = "\u53d6\u6d88",
        string badgeText = "\u786e\u8ba4",
        string iconGlyph = "\uE7BA")
    {
        HeadingText = Normalize(heading, "\u786e\u8ba4\u64cd\u4f5c");
        MessageText = Normalize(message, "\u8bf7\u786e\u8ba4\u662f\u5426\u7ee7\u7eed\u3002");
        DetailText = detail?.Trim() ?? string.Empty;
        BadgeText = Normalize(badgeText, "\u786e\u8ba4");
        IconGlyph = string.IsNullOrWhiteSpace(iconGlyph) ? "\uE7BA" : iconGlyph;

        InitializeComponent();

        Title = HeadingText;
        PrimaryButtonText = Normalize(confirmText, "\u786e\u8ba4");
        CloseButtonText = Normalize(cancelText, "\u53d6\u6d88");
        DefaultButton = ContentDialogButton.Close;
    }

    public string HeadingText { get; }

    public string MessageText { get; }

    public string DetailText { get; }

    public string BadgeText { get; }

    public string IconGlyph { get; }

    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(DetailText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public static ConfirmationDialog CreateDestructive(
        string heading,
        string message,
        string detail = "",
        string confirmText = "\u5220\u9664",
        string cancelText = "\u53d6\u6d88")
        => new(
            heading,
            message,
            detail,
            confirmText,
            cancelText,
            "\u9ad8\u5f71\u54cd",
            "\uE783");

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
