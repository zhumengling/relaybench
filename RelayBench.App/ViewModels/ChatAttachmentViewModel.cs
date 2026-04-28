using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed class ChatAttachmentViewModel : ObservableObject
{
    public ChatAttachmentViewModel(ChatAttachment attachment)
    {
        Attachment = attachment;
        ImagePreviewSource = attachment.Kind == ChatAttachmentKind.Image
            ? TryBuildImagePreview(attachment.Content)
            : null;
    }

    public ChatAttachment Attachment { get; }

    public string Id => Attachment.Id;

    public ChatAttachmentKind Kind => Attachment.Kind;

    public string FileName => Attachment.FileName;

    public string MediaType => Attachment.MediaType;

    public long SizeBytes => Attachment.SizeBytes;

    public bool IsImage => Kind == ChatAttachmentKind.Image;

    public bool IsTextFile => Kind == ChatAttachmentKind.TextFile;

    public string KindLabel => IsImage ? "\u56fe\u7247" : "\u6587\u672c\u6587\u4ef6";

    public string SizeLabel => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : SizeBytes < 1024 * 1024
            ? $"{SizeBytes / 1024d:F1} KB"
            : $"{SizeBytes / 1024d / 1024d:F1} MB";

    public ImageSource? ImagePreviewSource { get; }

    private static ImageSource? TryBuildImagePreview(string dataUrl)
    {
        try
        {
            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex < 0 || commaIndex >= dataUrl.Length - 1)
            {
                return null;
            }

            var bytes = Convert.FromBase64String(dataUrl[(commaIndex + 1)..]);
            using MemoryStream stream = new(bytes);
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
