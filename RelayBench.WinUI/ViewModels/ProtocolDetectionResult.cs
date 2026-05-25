namespace RelayBench.WinUI.ViewModels;

public sealed record ProtocolDetectionResult(string Format, string Version)
{
    public static ProtocolDetectionResult Unknown => new("Unknown", "N/A");
}
