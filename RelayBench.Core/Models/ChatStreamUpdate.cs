namespace RelayBench.Core.Models;

public enum ChatStreamUpdateKind
{
    Started,
    Delta,
    Completed,
    Failed
}

public sealed record ChatStreamUpdate(
    ChatStreamUpdateKind Kind,
    string? Delta,
    ChatMessageMetrics? Metrics,
    string? Error);
