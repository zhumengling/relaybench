namespace RelayBench.Core.Models;

public sealed record NetworkSnapshot(
    DateTimeOffset CapturedAt,
    string HostName,
    string? PublicIp,
    string? CloudflareColo,
    IReadOnlyList<NetworkAdapterInfo> Adapters,
    IReadOnlyList<PingCheck> PingChecks);
