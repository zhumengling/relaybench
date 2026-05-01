using System.Collections.ObjectModel;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed class ChatMessageViewModel : ObservableObject
{
    private const double BubblePaddingWidth = 32d;
    private const double BubbleMinimumWidth = 112d;
    private const double BubbleMaximumWidth = 920d;

    private string _content;
    private ChatMessageMetrics? _metrics;
    private string? _error;
    private readonly bool _isMultiModelAnswer;

    public ChatMessageViewModel(ChatMessage message)
    {
        Id = message.Id;
        Role = message.Role;
        _content = message.Content;
        CreatedAt = message.CreatedAt;
        _metrics = message.Metrics;
        _error = message.Error;
        Attachments = new ObservableCollection<ChatAttachmentViewModel>(
            message.Attachments.Select(static attachment => new ChatAttachmentViewModel(attachment)));
        Blocks = new ObservableCollection<ChatContentBlockViewModel>();
        ModelAnswers = [];
        RefreshBlocks();
    }

    private ChatMessageViewModel(string id, IReadOnlyList<string> modelNames)
    {
        Id = id;
        Role = "assistant-group";
        _content = string.Empty;
        CreatedAt = DateTimeOffset.Now;
        Attachments = [];
        Blocks = [];
        ModelAnswers = new ObservableCollection<ChatModelAnswerViewModel>(
            modelNames.Select((model, index) => new ChatModelAnswerViewModel(index + 1, model)));
        _isMultiModelAnswer = true;
    }

    public string Id { get; }

    public string Role { get; }

    public DateTimeOffset CreatedAt { get; }

    public ObservableCollection<ChatAttachmentViewModel> Attachments { get; }

    public ObservableCollection<ChatContentBlockViewModel> Blocks { get; }

    public ObservableCollection<ChatModelAnswerViewModel> ModelAnswers { get; }

    public string Content
    {
        get => _content;
        private set
        {
            if (SetProperty(ref _content, value))
            {
                RefreshBlocks();
                OnPropertyChanged(nameof(HasContent));
                OnPropertyChanged(nameof(CopyText));
                OnPropertyChanged(nameof(CanCopy));
                OnPropertyChanged(nameof(BubbleWidth));
            }
        }
    }

    public ChatMessageMetrics? Metrics
    {
        get => _metrics;
        private set
        {
            if (SetProperty(ref _metrics, value))
            {
                OnPropertyChanged(nameof(MetricsSummary));
                OnPropertyChanged(nameof(BubbleWidth));
            }
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(BubbleWidth));
            }
        }
    }

    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    public bool IsAssistant => string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase) || IsMultiModelAnswer;

    public bool IsMultiModelAnswer => _isMultiModelAnswer || string.Equals(Role, "assistant-group", StringComparison.OrdinalIgnoreCase);

    public bool IsStandardMessage => !IsMultiModelAnswer;

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool CanCopy => !string.IsNullOrWhiteSpace(CopyText);

    public string CopyText => IsMultiModelAnswer ? BuildMultiModelCopyText() : BuildStandardCopyText();

    public double BubbleWidth => CalculateBubbleWidth();

    public int BubbleColumn => IsUser ? 2 : 0;

    public string BubbleHorizontalAlignment => IsUser ? "Right" : "Left";

    public string BubbleOuterMargin => IsUser ? "0,0,24,0" : "0";

    public string BubbleBackground => IsUser ? "#E0F2FE" : "#F8FAFC";

    public string BubbleBorderBrush => IsUser ? "#7DD3FC" : "#E4E7EC";

    public string DisplayRole => IsUser ? "\u7528\u6237" : IsMultiModelAnswer ? "\u591a\u6a21\u578b" : "\u52a9\u624b";

    public string CreatedAtLabel => CreatedAt.ToLocalTime().ToString("HH:mm:ss");

    public int AnswerColumnCount => Math.Clamp(ModelAnswers.Count, 1, 4);

    public string MetricsSummary
    {
        get
        {
            if (Metrics is null)
            {
                return string.Empty;
            }

            var ttft = Metrics.FirstTokenLatency is null
                ? "TTFT --"
                : $"TTFT {Metrics.FirstTokenLatency.Value.TotalMilliseconds:F0} ms";
            var speed = Metrics.CharactersPerSecond is null
                ? "-- chars/s"
                : $"{Metrics.CharactersPerSecond.Value:F1} chars/s";
            return $"{Metrics.WireApi} | {Metrics.Elapsed.TotalMilliseconds:F0} ms | {ttft} | {speed}";
        }
    }

    public void AppendDelta(string delta)
        => Content += delta;

    public void SetMetrics(ChatMessageMetrics? metrics)
        => Metrics = metrics;

    public void SetError(string? error)
        => Error = error;

    public ChatMessage ToCore()
        => new(
            Id,
            IsMultiModelAnswer ? "assistant" : Role,
            IsMultiModelAnswer ? BuildMultiModelContent() : Content,
            CreatedAt,
            Attachments.Select(static attachment => attachment.Attachment).ToArray(),
            Metrics,
            IsMultiModelAnswer ? BuildMultiModelError() : Error);

    public static ChatMessageViewModel CreateMultiModelAnswer(IReadOnlyList<string> modelNames)
        => new(Guid.NewGuid().ToString("N"), modelNames);

    private string BuildMultiModelContent()
        => string.Join(
            Environment.NewLine + Environment.NewLine,
            ModelAnswers.Select(answer =>
                $"### {answer.ModelName}{Environment.NewLine}{(string.IsNullOrWhiteSpace(answer.Content) ? "\u6682\u65e0\u8f93\u51fa\u3002" : answer.Content)}"));

    private string BuildStandardCopyText()
    {
        if (Attachments.Count == 0)
        {
            return Content;
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(Content))
        {
            lines.Add(Content);
        }

        lines.Add("[附件]");
        lines.AddRange(Attachments.Select(static attachment => $"- {attachment.KindLabel}: {attachment.FileName} ({attachment.SizeLabel})"));
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildMultiModelCopyText()
        => string.Join(
            Environment.NewLine + Environment.NewLine,
            ModelAnswers.Select(static answer => answer.CopyText));

    private string? BuildMultiModelError()
    {
        var errors = ModelAnswers
            .Where(static answer => answer.HasError)
            .Select(static answer => $"{answer.ModelName}: {answer.Error}")
            .ToArray();
        return errors.Length == 0 ? null : string.Join(Environment.NewLine, errors);
    }

    private double CalculateBubbleWidth()
    {
        if (IsMultiModelAnswer)
        {
            return BubbleMaximumWidth;
        }

        var headerWidth = EstimateTextWidth(DisplayRole, 12d) +
            EstimateTextWidth(CreatedAtLabel, 10.5d) +
            74d;
        var contentWidth = EstimateMultilineTextWidth(Content, 12.5d);
        var metricsWidth = EstimateTextWidth(MetricsSummary, 10.5d);
        var errorWidth = EstimateMultilineTextWidth(Error ?? string.Empty, 11d);
        var attachmentWidth = HasAttachments ? 220d : 0d;

        var desiredWidth = new[] { headerWidth, contentWidth, metricsWidth, errorWidth, attachmentWidth }.Max() + BubblePaddingWidth;
        return Math.Clamp(Math.Ceiling(desiredWidth), BubbleMinimumWidth, BubbleMaximumWidth);
    }

    private static double EstimateMultilineTextWidth(string? value, double fontSize)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0d;
        }

        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => EstimateTextWidth(line, fontSize))
            .DefaultIfEmpty(0d)
            .Max();
    }

    private static double EstimateTextWidth(string? value, double fontSize)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0d;
        }

        var width = 0d;
        foreach (var ch in value)
        {
            width += ch switch
            {
                '\t' => fontSize * 1.8d,
                ' ' => fontSize * 0.35d,
                >= '\u2E80' => fontSize * 1.05d,
                >= 'A' and <= 'Z' => fontSize * 0.66d,
                >= 'a' and <= 'z' => fontSize * 0.56d,
                >= '0' and <= '9' => fontSize * 0.56d,
                _ => fontSize * 0.45d
            };
        }

        return width;
    }

    private void RefreshBlocks()
    {
        Blocks.Clear();
        foreach (var block in ChatMarkdownBlockParser.Parse(Content))
        {
            Blocks.Add(new ChatContentBlockViewModel(block));
        }
    }
}
