namespace RelayBench.Core.Models;

public enum ChatAttachmentKind
{
    Image,
    TextFile
}

public sealed record ChatAttachment(
    string Id,
    ChatAttachmentKind Kind,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Content);
