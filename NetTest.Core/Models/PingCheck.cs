namespace NetTest.Core.Models;

public sealed record PingCheck(
    string Target,
    string Status,
    long? RoundTripTime,
    string? Address,
    string? Error);
