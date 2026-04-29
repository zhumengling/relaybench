namespace RelayBench.Core.Models;

public sealed record ClientApplyTargetSelection(
    string TargetId,
    ClientApplyProtocolKind Protocol);
