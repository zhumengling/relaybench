namespace RelayBench.Core.Models;

/// <summary>
/// 单个模型在某个接口上针对 Chat/Responses/Anthropic 三种 wire API 的探测结果快照。
/// </summary>
public sealed record ProxyModelProtocolProbeOutcome(
    string Model,
    bool ChatCompletionsSupported,
    bool ResponsesSupported,
    bool AnthropicMessagesSupported,
    string? PreferredWireApi,
    DateTimeOffset CheckedAt,
    string Summary,
    string? Error = null);
