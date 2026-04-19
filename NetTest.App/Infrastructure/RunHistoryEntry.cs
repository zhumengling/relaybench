namespace NetTest.App.Infrastructure;

public sealed record RunHistoryEntry(
    DateTimeOffset Timestamp,
    string Category,
    string Title,
    string Summary);
