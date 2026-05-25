namespace RelayBench.Core.Models;

public sealed record ChatMessageMetrics(
    TimeSpan Elapsed,
    TimeSpan? FirstTokenLatency,
    int OutputCharacterCount,
    double? CharactersPerSecond,
    string WireApi)
{
    public int OutputTokenCount { get; init; }

    public bool OutputTokenCountEstimated { get; init; }

    public int InputTokenCount { get; init; }

    public int CachedTokenCount { get; init; }

    public TimeSpan? TokenThroughputWindow { get; init; }

    public double? TokensPerSecond { get; init; }
}
