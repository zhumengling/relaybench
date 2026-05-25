namespace RelayBench.Core.Models;

public sealed record SpeedTransferMeasurement(
    int Sequence,
    long Bytes,
    double DurationMilliseconds,
    double BitsPerSecond,
    IReadOnlyList<double> LoadedLatencyPointsMilliseconds);
