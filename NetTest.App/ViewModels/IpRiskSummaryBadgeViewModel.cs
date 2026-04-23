namespace NetTest.App.ViewModels;

public sealed class IpRiskSummaryBadgeViewModel
{
    public IpRiskSummaryBadgeViewModel(
        string label,
        string value,
        string detail,
        IpRiskToneViewModel tone)
    {
        Label = label;
        Value = value;
        Detail = detail;
        Tone = tone;
    }

    public string Label { get; }

    public string Value { get; }

    public string Detail { get; }

    public IpRiskToneViewModel Tone { get; }
}
