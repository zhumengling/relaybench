namespace RelayBench.Core.Models;

public sealed record PortScanProfile(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<int> DefaultPorts,
    int ConnectTimeoutMilliseconds,
    int MaxConcurrency,
    bool EnableBannerProbe,
    bool EnableTlsProbe,
    bool EnableHttpProbe,
    bool EnableUdpProbe)
{
    public string PortListText => string.Join(", ", DefaultPorts);

    public string TransportSummaryText => EnableUdpProbe ? "TCP + UDP 轻量探测" : "TCP Connect";

    public string ProbeSummaryText
    {
        get
        {
            List<string> probes = [];
            if (EnableBannerProbe)
            {
                probes.Add("Banner");
            }

            if (EnableTlsProbe)
            {
                probes.Add("TLS");
            }

            if (EnableHttpProbe)
            {
                probes.Add("HTTP");
            }

            if (EnableUdpProbe)
            {
                probes.Add("UDP");
            }

            return probes.Count == 0
                ? "仅 TCP Connect"
                : string.Join(" / ", probes);
        }
    }
}
