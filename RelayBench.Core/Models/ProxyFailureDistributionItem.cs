namespace RelayBench.Core.Models;

public sealed record ProxyFailureDistributionItem(
    ProxyFailureKind FailureKind,
    int Count,
    double Rate,
    string Summary);
