namespace RelayBench.Core.Models;

public sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    string NetworkType,
    bool SupportsIPv4,
    bool SupportsIPv6,
    IReadOnlyList<string> UnicastAddresses,
    IReadOnlyList<string> DnsServers,
    string? MacAddress = null,
    long? LinkSpeedBitsPerSecond = null,
    IReadOnlyList<string>? GatewayAddresses = null);
