using System.IO;
using System.Text;
using RelayBench.Core.Models;

namespace RelayBench.App.Services;

public sealed class ChatAttachmentImportService
{
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private const long MaxTextBytes = 1024 * 1024;

    private static readonly Dictionary<string, string> ImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md",
        ".json",
        ".csv",
        ".log",
        ".cs",
        ".xaml",
        ".xml",
        ".yaml",
        ".yml",
        ".ps1"
    };

    public ChatAttachment ImportImage(string path)
    {
        var file = ValidateFile(path);
        var extension = file.Extension;
        if (!ImageMediaTypes.TryGetValue(extension, out var mediaType))
        {
            throw new InvalidOperationException("\u4ec5\u652f\u6301 png\u3001jpg\u3001webp \u548c gif \u56fe\u7247\u3002");
        }

        if (file.Length > MaxImageBytes)
        {
            throw new InvalidOperationException("\u5355\u5f20\u56fe\u7247\u4e0d\u80fd\u8d85\u8fc7 10 MB\u3002");
        }

        var bytes = File.ReadAllBytes(file.FullName);
        var dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
        return new ChatAttachment(
            Guid.NewGuid().ToString("N"),
            ChatAttachmentKind.Image,
            file.Name,
            mediaType,
            file.Length,
            dataUrl);
    }

    public ChatAttachment ImportTextFile(string path)
    {
        var file = ValidateFile(path);
        if (!TextExtensions.Contains(file.Extension))
        {
            throw new InvalidOperationException("\u4ec5\u652f\u6301\u5e38\u89c1\u6587\u672c\u3001\u4ee3\u7801\u548c\u914d\u7f6e\u6587\u4ef6\u3002");
        }

        if (file.Length > MaxTextBytes)
        {
            throw new InvalidOperationException("\u5355\u4e2a\u6587\u672c\u6587\u4ef6\u4e0d\u80fd\u8d85\u8fc7 1 MB\u3002");
        }

        var content = ReadText(file.FullName);
        return new ChatAttachment(
            Guid.NewGuid().ToString("N"),
            ChatAttachmentKind.TextFile,
            file.Name,
            "text/plain",
            file.Length,
            content);
    }

    private static FileInfo ValidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("\u672a\u9009\u62e9\u6587\u4ef6\u3002");
        }

        FileInfo file = new(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("\u6587\u4ef6\u4e0d\u5b58\u5728\u3002", path);
        }

        return file;
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (DecoderFallbackException)
        {
            return File.ReadAllText(path, Encoding.Default);
        }
    }
}
