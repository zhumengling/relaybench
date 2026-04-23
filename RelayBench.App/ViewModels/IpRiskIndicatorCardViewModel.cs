namespace RelayBench.App.ViewModels;

public sealed class IpRiskIndicatorCardViewModel
{
    public IpRiskIndicatorCardViewModel(
        string title,
        string status,
        string detail,
        IpRiskToneViewModel tone)
    {
        Title = title;
        Status = status;
        Detail = detail;
        Tone = tone;
    }

    public string Title { get; }

    public string Status { get; }

    public string Detail { get; }

    public IpRiskToneViewModel Tone { get; }
}
