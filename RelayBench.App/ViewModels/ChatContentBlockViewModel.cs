using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed class ChatContentBlockViewModel : ObservableObject
{
    public ChatContentBlockViewModel(ChatContentBlock block)
    {
        Kind = block.Kind;
        Content = block.Content;
        Language = string.IsNullOrWhiteSpace(block.Language) ? "text" : block.Language;
        IsClosed = block.IsClosed;
    }

    public ChatContentBlockKind Kind { get; }

    public string Content { get; }

    public string Language { get; }

    public bool IsClosed { get; }

    public bool IsText => Kind == ChatContentBlockKind.Text;

    public bool IsCode => Kind == ChatContentBlockKind.Code;

    public string DisplayLanguage => string.IsNullOrWhiteSpace(Language) ? "text" : Language;

    public string StatusText => IsClosed ? string.Empty : "\u751f\u6210\u4e2d";
}
