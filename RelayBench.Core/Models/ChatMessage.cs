namespace RelayBench.Core.Models;

public sealed record ChatMessage(
    string Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChatAttachment> Attachments,
    ChatMessageMetrics? Metrics,
    string? Error);
