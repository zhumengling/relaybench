namespace NetTest.Core.Models;

public sealed record SplitRoutingAdapterView(
    string Name,
    string Description,
    string NetworkType,
    IReadOnlyList<string> LocalAddresses,
    IReadOnlyList<string> DnsServers);
