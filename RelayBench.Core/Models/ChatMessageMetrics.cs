namespace RelayBench.Core.Models;

public sealed record ChatMessageMetrics(
    TimeSpan Elapsed,
    TimeSpan? FirstTokenLatency,
    int OutputCharacterCount,
    double? CharactersPerSecond,
    string WireApi);
