using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.ViewModels;

public sealed record TransparentProxyPolicyStatusItem(
    string Title,
    string Value,
    string Detail,
    string BadgeText,
    TransparentProxyPolicyTone Tone)
{
    public Visibility AccentToneVisibility => Tone == TransparentProxyPolicyTone.Accent
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility HealthyToneVisibility => Tone == TransparentProxyPolicyTone.Healthy
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility WarningToneVisibility => Tone == TransparentProxyPolicyTone.Warning
        ? Visibility.Visible
        : Visibility.Collapsed;
}

public enum TransparentProxyPolicyTone
{
    Accent,
    Healthy,
    Warning
}
