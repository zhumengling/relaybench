using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.ViewModels;

public enum CapabilityState { Supported, Unsupported, Unknown }

public sealed record CapabilityCellState(
    string Name,
    string IconGlyph,
    CapabilityState State,
    string Subtitle = "",
    string PrimaryMetricLabel = "",
    string PrimaryMetricValue = "0",
    string SecondaryMetricLabel = "",
    string SecondaryMetricValue = "0")
{
    public string StateText => State switch
    {
        CapabilityState.Supported => "\u901A\u8FC7",
        CapabilityState.Unsupported => "\u8B66\u544A",
        _ => "\u5F85\u6D4B"
    };

    public Visibility SupportedVisibility => State == CapabilityState.Supported
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility UnsupportedVisibility => State == CapabilityState.Unsupported
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility UnknownVisibility => State == CapabilityState.Unknown
        ? Visibility.Visible
        : Visibility.Collapsed;
}
