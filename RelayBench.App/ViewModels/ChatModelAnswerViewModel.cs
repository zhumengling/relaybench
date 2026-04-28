using System.Collections.ObjectModel;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed class ChatModelAnswerViewModel : ObservableObject
{
    private string _content = string.Empty;
    private ChatMessageMetrics? _metrics;
    private string? _error;
    private bool _isStreaming;
    private string _statusText = "\u7b49\u5f85\u4e2d";

    public ChatModelAnswerViewModel(int ordinal, string modelName)
    {
        Ordinal = ordinal;
        ModelName = modelName;
        Blocks = [];
    }

    public int Ordinal { get; }

    public string ModelName { get; }

    public ObservableCollection<ChatContentBlockViewModel> Blocks { get; }

    public string Header => $"{Ordinal}. {ModelName}";

    public string Content
    {
        get => _content;
        private set
        {
            if (SetProperty(ref _content, value))
            {
                RefreshBlocks();
                OnPropertyChanged(nameof(HasContent));
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
            }
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set => SetProperty(ref _isStreaming, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

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

    public void MarkStarted()
    {
        IsStreaming = true;
        StatusText = "\u751f\u6210\u4e2d";
    }

    public void AppendDelta(string delta)
        => Content += delta;

    public void Complete(ChatMessageMetrics? metrics)
    {
        Metrics = metrics;
        IsStreaming = false;
        StatusText = "\u5b8c\u6210";
    }

    public void Fail(string? error, ChatMessageMetrics? metrics)
    {
        Metrics = metrics;
        Error = error;
        IsStreaming = false;
        StatusText = "\u5931\u8d25";
    }

    public void Cancel()
    {
        Error = "\u7528\u6237\u5df2\u505c\u6b62\u751f\u6210\u3002";
        IsStreaming = false;
        StatusText = "\u5df2\u505c\u6b62";
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
