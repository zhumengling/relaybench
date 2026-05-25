using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a file attachment in the chat input area.
/// </summary>
public sealed partial class ChatAttachmentItem : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty] public partial string FileName { get; set; } = "";
    [ObservableProperty] public partial string FilePath { get; set; } = "";
    [ObservableProperty] public partial bool IsImage { get; set; }
    [ObservableProperty] public partial string? ThumbnailPath { get; set; }
    [ObservableProperty] public partial long SizeBytes { get; set; }
    [ObservableProperty] public partial string? CachedContent { get; set; }
    [ObservableProperty] public partial string? MediaType { get; set; }

    public ChatAttachmentItem() { }

    public ChatAttachmentItem(
        string fileName,
        string filePath,
        bool isImage,
        long sizeBytes,
        string? cachedContent = null,
        string? mediaType = null,
        string? id = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        FileName = fileName;
        FilePath = filePath;
        IsImage = isImage;
        SizeBytes = sizeBytes;
        ThumbnailPath = isImage ? filePath : null;
        CachedContent = cachedContent;
        MediaType = mediaType;
    }

    /// <summary>
    /// Formatted file size display.
    /// </summary>
    public string SizeDisplay => SizeBytes switch
    {
        >= 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes} B"
    };

    public string TypeLabel => IsImage ? "图片" : "文本";

    partial void OnIsImageChanged(bool value)
    {
        OnPropertyChanged(nameof(TypeLabel));
    }
}
