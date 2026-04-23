namespace RelayBench.App.ViewModels;

public sealed class IpRiskToneViewModel
{
    public static IpRiskToneViewModel Neutral { get; } = new("#F8FAFC", "#D0D5DD", "#101828", "#98A2B3");

    public static IpRiskToneViewModel Info { get; } = new("#EFF8FF", "#B2DDFF", "#175CD3", "#2E90FA");

    public static IpRiskToneViewModel Success { get; } = new("#ECFDF3", "#ABEFC6", "#027A48", "#12B76A");

    public static IpRiskToneViewModel Warning { get; } = new("#FFFAEB", "#FEDF89", "#B54708", "#F79009");

    public static IpRiskToneViewModel Danger { get; } = new("#FEF3F2", "#FECDCA", "#B42318", "#F04438");

    public IpRiskToneViewModel(
        string background,
        string borderBrush,
        string foreground,
        string accentBrush)
    {
        Background = background;
        BorderBrush = borderBrush;
        Foreground = foreground;
        AccentBrush = accentBrush;
    }

    public string Background { get; }

    public string BorderBrush { get; }

    public string Foreground { get; }

    public string AccentBrush { get; }
}
