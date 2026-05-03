using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed class TransparentProxyRouteViewModel : ObservableObject
{
    private int _sent;
    private int _success;
    private int _failed;
    private int _lastStatusCode;
    private long _lastLatencyMs;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private string _circuitState = "Closed";
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;
    private string? _preferredWireApi;
    private bool? _chatCompletionsSupported;
    private bool? _responsesSupported;
    private bool? _anthropicMessagesSupported;
    private DateTimeOffset? _protocolCheckedAt;

    public TransparentProxyRouteViewModel(TransparentProxyRoute route, int priority = 0)
    {
        Id = route.Id;
        Priority = priority;
        Name = route.Name;
        BaseUrl = route.BaseUrl;
        Model = route.Model;
        ApiKeyPreview = BuildApiKeyPreview(route.ApiKey);
        ApplyProtocol(
            route.PreferredWireApi,
            route.ChatCompletionsSupported,
            route.ResponsesSupported,
            route.AnthropicMessagesSupported,
            route.ProtocolCheckedAt);
    }

    public string Id { get; }

    public int Priority { get; }

    public string PriorityText => Priority <= 0 ? "-" : $"P{Priority}";

    public string Name { get; }

    public string BaseUrl { get; }

    public string Model { get; }

    public string ApiKeyPreview { get; }

    public int Sent
    {
        get => _sent;
        private set => SetProperty(ref _sent, value);
    }

    public int Success
    {
        get => _success;
        private set => SetProperty(ref _success, value);
    }

    public int Failed
    {
        get => _failed;
        private set => SetProperty(ref _failed, value);
    }

    public int LastStatusCode
    {
        get => _lastStatusCode;
        private set => SetProperty(ref _lastStatusCode, value);
    }

    public long LastLatencyMs
    {
        get => _lastLatencyMs;
        private set => SetProperty(ref _lastLatencyMs, value);
    }

    public int ConsecutiveFailures
    {
        get => _consecutiveFailures;
        private set => SetProperty(ref _consecutiveFailures, value);
    }

    public int ConsecutiveSuccesses
    {
        get => _consecutiveSuccesses;
        private set => SetProperty(ref _consecutiveSuccesses, value);
    }

    public string CircuitState
    {
        get => _circuitState;
        private set => SetProperty(ref _circuitState, value);
    }

    public DateTimeOffset CircuitOpenUntil
    {
        get => _circuitOpenUntil;
        private set => SetProperty(ref _circuitOpenUntil, value);
    }

    public string EndpointText => ProbeTraceRedactor.RedactUrl(BaseUrl);

    public string ModelText => string.IsNullOrWhiteSpace(Model) ? "使用请求模型" : Model;

    public string StatusText
    {
        get
        {
            if (string.Equals(CircuitState, "Open", StringComparison.OrdinalIgnoreCase))
            {
                return "熔断";
            }

            if (string.Equals(CircuitState, "HalfOpen", StringComparison.OrdinalIgnoreCase))
            {
                return "半开";
            }

            if (Sent == 0)
            {
                return "待命";
            }

            if (ConsecutiveFailures > 0)
            {
                return $"观察 {ConsecutiveFailures}";
            }

            return LastStatusCode is >= 200 and < 500 ? "可用" : "异常";
        }
    }

    public string StatusBrush
        => StatusText.StartsWith("观察", StringComparison.Ordinal)
            ? "#F1C21B"
            : StatusText switch
        {
            "可用" => "#24A148",
            "半开" => "#F59E0B",
            "熔断" => "#DA1E28",
            "待命" => "#64748B",
            _ => "#DA1E28"
        };

    public string StatsText => $"请求 {Sent}  成功 {Success}  失败 {Failed}";

    public string LatencyText => LastLatencyMs <= 0 ? "-" : $"{LastLatencyMs} ms";

    public string StatusToolTip
    {
        get
        {
            if (string.Equals(CircuitState, "Open", StringComparison.OrdinalIgnoreCase) &&
                CircuitOpenUntil > DateTimeOffset.UtcNow)
            {
                return $"熔断中，预计 {CircuitOpenUntil.ToLocalTime():HH:mm:ss} 半开探测";
            }

            if (string.Equals(CircuitState, "HalfOpen", StringComparison.OrdinalIgnoreCase))
            {
                return $"半开探测中，连续成功 {ConsecutiveSuccesses} / 2 后恢复";
            }

            return ConsecutiveFailures > 0
                ? $"观察中，连续失败 {ConsecutiveFailures} 次"
                : "路由可参与调度";
        }
    }

    public string ProtocolText
        => string.IsNullOrWhiteSpace(_preferredWireApi)
            ? "待探测"
            : _preferredWireApi switch
            {
                ProxyWireApiProbeService.ResponsesWireApi => "Responses",
                ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic",
                ProxyWireApiProbeService.ChatCompletionsWireApi => "OpenAI Chat",
                _ => _preferredWireApi
            };

    public string ProtocolSupportText
        => $"R:{FormatSupport(_responsesSupported)} A:{FormatSupport(_anthropicMessagesSupported)} C:{FormatSupport(_chatCompletionsSupported)}";

    public string ProtocolCheckedText
        => _protocolCheckedAt is null || _protocolCheckedAt == DateTimeOffset.MinValue
            ? "未写入"
            : _protocolCheckedAt.Value.ToLocalTime().ToString("MM-dd HH:mm");

    public void ApplyMetrics(TransparentProxyRouteMetrics? metrics)
    {
        Sent = metrics?.Sent ?? 0;
        Success = metrics?.Success ?? 0;
        Failed = metrics?.Failed ?? 0;
        LastStatusCode = metrics?.LastStatusCode ?? 0;
        LastLatencyMs = metrics?.LastLatencyMs ?? 0;
        ConsecutiveFailures = metrics?.ConsecutiveFailures ?? 0;
        ConsecutiveSuccesses = metrics?.ConsecutiveSuccesses ?? 0;
        CircuitState = metrics?.CircuitState ?? "Closed";
        CircuitOpenUntil = metrics?.CircuitOpenUntil ?? DateTimeOffset.MinValue;
        if (metrics is not null)
        {
            ApplyProtocol(
                metrics.PreferredWireApi,
                metrics.ChatCompletionsSupported,
                metrics.ResponsesSupported,
                metrics.AnthropicMessagesSupported,
                metrics.ProtocolCheckedAt);
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusToolTip));
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(LatencyText));
    }

    public void ApplyProtocol(
        string? preferredWireApi,
        bool? chatCompletionsSupported,
        bool? responsesSupported,
        bool? anthropicMessagesSupported,
        DateTimeOffset? protocolCheckedAt)
    {
        _preferredWireApi = ProxyWireApiProbeService.NormalizeWireApi(preferredWireApi);
        _chatCompletionsSupported = chatCompletionsSupported;
        _responsesSupported = responsesSupported;
        _anthropicMessagesSupported = anthropicMessagesSupported;
        _protocolCheckedAt = protocolCheckedAt;
        OnPropertyChanged(nameof(ProtocolText));
        OnPropertyChanged(nameof(ProtocolSupportText));
        OnPropertyChanged(nameof(ProtocolCheckedText));
    }

    private static string BuildApiKeyPreview(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "透传客户端 Authorization";
        }

        return apiKey.Length <= 8
            ? "***"
            : $"{apiKey[..Math.Min(3, apiKey.Length)]}...{apiKey[^Math.Min(4, apiKey.Length)..]}";
    }

    private static string FormatSupport(bool? supported)
        => supported switch
        {
            true => "Y",
            false => "N",
            _ => "-"
        };
}
