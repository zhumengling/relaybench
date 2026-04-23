namespace NetTest.App.ViewModels;

public sealed class IpRiskSourceRowViewModel
{
    public IpRiskSourceRowViewModel(
        string sourceName,
        string sourceMetaText,
        string summary,
        string verdictText,
        IpRiskToneViewModel verdictTone,
        string datacenterText,
        IpRiskToneViewModel datacenterTone,
        string proxyText,
        IpRiskToneViewModel proxyTone,
        string vpnText,
        IpRiskToneViewModel vpnTone,
        string torText,
        IpRiskToneViewModel torTone,
        string abuseText,
        IpRiskToneViewModel abuseTone,
        string riskScoreText,
        IpRiskToneViewModel riskScoreTone)
    {
        SourceName = sourceName;
        SourceMetaText = sourceMetaText;
        Summary = summary;
        VerdictText = verdictText;
        VerdictTone = verdictTone;
        DatacenterText = datacenterText;
        DatacenterTone = datacenterTone;
        ProxyText = proxyText;
        ProxyTone = proxyTone;
        VpnText = vpnText;
        VpnTone = vpnTone;
        TorText = torText;
        TorTone = torTone;
        AbuseText = abuseText;
        AbuseTone = abuseTone;
        RiskScoreText = riskScoreText;
        RiskScoreTone = riskScoreTone;
    }

    public string SourceName { get; }

    public string SourceMetaText { get; }

    public string Summary { get; }

    public string VerdictText { get; }

    public IpRiskToneViewModel VerdictTone { get; }

    public string DatacenterText { get; }

    public IpRiskToneViewModel DatacenterTone { get; }

    public string ProxyText { get; }

    public IpRiskToneViewModel ProxyTone { get; }

    public string VpnText { get; }

    public IpRiskToneViewModel VpnTone { get; }

    public string TorText { get; }

    public IpRiskToneViewModel TorTone { get; }

    public string AbuseText { get; }

    public IpRiskToneViewModel AbuseTone { get; }

    public string RiskScoreText { get; }

    public IpRiskToneViewModel RiskScoreTone { get; }
}
