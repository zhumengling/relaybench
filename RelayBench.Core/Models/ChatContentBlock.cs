namespace RelayBench.Core.Models;

public enum ChatContentBlockKind
{
    Text,
    Code
}

public sealed record ChatContentBlock(
    ChatContentBlockKind Kind,
    string Content,
    string? Language,
    bool IsClosed);
