using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Services;

internal sealed class ChatAttachmentService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".csv", ".log", ".cs", ".xaml", ".xml", ".yaml", ".yml", ".ps1"
    };

    private const long MaxFileSizeBytes = 20 * 1024 * 1024; // 20 MB

    /// <summary>
    /// Validates and imports a file for chat attachment.
    /// Returns the attachment item, or null with error message if validation fails.
    /// </summary>
    public (ChatAttachmentItem? Item, string? Error) Import(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return (null, $"无法读取文件：未找到 {filePath}");
            }

            var extension = Path.GetExtension(filePath) ?? string.Empty;
            var isImage = ImageExtensions.Contains(extension);
            var isText = TextExtensions.Contains(extension);
            if (string.IsNullOrEmpty(extension) || (!isImage && !isText))
            {
                return (null, "不支持的附件类型。支持图片、TXT、Markdown、JSON、CSV、日志、代码和 YAML 文件。");
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                return (null, "文件超过 20 MB 限制");
            }

            string? cachedContent;
            string? mediaType;
            if (isImage)
            {
                mediaType = GetMimeType(extension);
                var bytes = File.ReadAllBytes(filePath);
                cachedContent = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
            }
            else
            {
                mediaType = "text/plain";
                cachedContent = File.ReadAllText(filePath);
            }

            var item = new ChatAttachmentItem(
                fileName: fileInfo.Name,
                filePath: fileInfo.FullName,
                isImage: isImage,
                sizeBytes: fileInfo.Length,
                cachedContent: cachedContent,
                mediaType: mediaType);

            return (item, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, $"无法读取文件：{ex.Message}");
        }
    }

    /// <summary>
    /// Encodes attachments for the target API protocol (base64 data URI for OpenAI vision).
    /// </summary>
    public IReadOnlyList<object> EncodeForApi(
        IReadOnlyList<ChatAttachmentItem> attachments,
        string protocol)
    {
        var results = new List<object>(attachments.Count);

        foreach (var attachment in attachments)
        {
            if (attachment.IsImage)
            {
                var dataUri = attachment.CachedContent;
                if (string.IsNullOrWhiteSpace(dataUri) && File.Exists(attachment.FilePath))
                {
                    var bytes = File.ReadAllBytes(attachment.FilePath);
                    var base64 = Convert.ToBase64String(bytes);
                    var mimeType = GetMimeType(Path.GetExtension(attachment.FilePath));
                    dataUri = $"data:{mimeType};base64,{base64}";
                }

                // OpenAI vision format: image_url content part
                results.Add(new
                {
                    type = "image_url",
                    image_url = new { url = dataUri ?? string.Empty }
                });
            }
            else
            {
                var text = attachment.CachedContent;
                if (string.IsNullOrWhiteSpace(text) && File.Exists(attachment.FilePath))
                {
                    text = File.ReadAllText(attachment.FilePath);
                }

                results.Add(new
                {
                    type = "text",
                    text = text ?? string.Empty
                });
            }
        }

        return results;
    }

    private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}
