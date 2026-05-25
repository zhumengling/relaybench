using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;



namespace RelayBench.WinUI.ViewModels;

public sealed partial class ChatMessageItem : ObservableObject
{
    public bool IsUser { get; }
    public bool IsAssistant => !IsUser;

    [ObservableProperty] public partial string Content { get; set; }

    [ObservableProperty] public partial string? Metrics { get; set; }

    [ObservableProperty] public partial string? CodeBlock { get; set; }

    public IReadOnlyList<ChatAttachment> Attachments { get; }

    public IReadOnlyList<ChatAttachmentItem> AttachmentItems { get; }

    public bool HasAttachments => AttachmentItems.Count > 0;

    /// <summary>
    /// The full markdown content for rendering via MarkdownPresenter.
    /// For assistant messages, this includes both text and code blocks.
    /// </summary>
    public string MarkdownContent => CodeBlock is not null
        ? $"{Content}\n```\n{CodeBlock}\n```"
        : Content;

    public bool HasCodeBlock => !string.IsNullOrWhiteSpace(CodeBlock);
    public bool HasMetrics => !string.IsNullOrWhiteSpace(Metrics);

    public ChatMessageItem(
        bool isUser,
        string content,
        string? metrics,
        string? codeBlock = null,
        IReadOnlyList<ChatAttachment>? attachments = null,
        IReadOnlyList<ChatAttachmentItem>? attachmentItems = null)
    {
        IsUser = isUser;
        Content = content;
        Metrics = metrics;
        CodeBlock = codeBlock;
        Attachments = attachments ?? Array.Empty<ChatAttachment>();
        AttachmentItems = attachmentItems ?? Array.Empty<ChatAttachmentItem>();
    }

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(MarkdownContent));
    }

    partial void OnCodeBlockChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCodeBlock));
        OnPropertyChanged(nameof(MarkdownContent));
    }

    partial void OnMetricsChanged(string? value)
    {
        OnPropertyChanged(nameof(HasMetrics));
    }
}
