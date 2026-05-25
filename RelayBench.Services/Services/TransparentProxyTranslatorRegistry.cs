using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.Services;

namespace RelayBench.Services;

internal sealed class TransparentProxyTranslatorRegistry
{
    private readonly IReadOnlyDictionary<string, ITransparentProxyWireTranslator> _translators;

    public TransparentProxyTranslatorRegistry()
    {
        ITransparentProxyWireTranslator[] translators =
        [
            new TransparentProxyResponsesTranslator(),
            new TransparentProxyAnthropicMessagesTranslator(),
            new TransparentProxyOpenAiChatTranslator()
        ];
        _translators = translators.ToDictionary(static item => item.WireApi, StringComparer.Ordinal);
    }

    public AdvancedPreparedWireRequest PreparePostJson(
        string relativePath,
        string requestBody,
        string? preferredWireApi,
        bool stream)
    {
        var normalized = ProxyWireApiProbeService.NormalizeWireApi(preferredWireApi) ??
                         ProxyWireApiProbeService.ChatCompletionsWireApi;
        return _translators.TryGetValue(normalized, out var translator)
            ? translator.PreparePostJson(relativePath, requestBody, stream)
            : AdvancedWireRequestBuilder.PreparePostJson(relativePath, requestBody, normalized, stream);
    }
}

internal interface ITransparentProxyWireTranslator
{
    string WireApi { get; }

    AdvancedPreparedWireRequest PreparePostJson(
        string relativePath,
        string requestBody,
        bool stream);
}

internal sealed class TransparentProxyResponsesTranslator : ITransparentProxyWireTranslator
{
    public string WireApi => ProxyWireApiProbeService.ResponsesWireApi;

    public AdvancedPreparedWireRequest PreparePostJson(
        string relativePath,
        string requestBody,
        bool stream)
        => AdvancedWireRequestBuilder.PreparePostJson(relativePath, requestBody, WireApi, stream);
}

internal sealed class TransparentProxyAnthropicMessagesTranslator : ITransparentProxyWireTranslator
{
    public string WireApi => ProxyWireApiProbeService.AnthropicMessagesWireApi;

    public AdvancedPreparedWireRequest PreparePostJson(
        string relativePath,
        string requestBody,
        bool stream)
        => AdvancedWireRequestBuilder.PreparePostJson(relativePath, requestBody, WireApi, stream);
}

internal sealed class TransparentProxyOpenAiChatTranslator : ITransparentProxyWireTranslator
{
    public string WireApi => ProxyWireApiProbeService.ChatCompletionsWireApi;

    public AdvancedPreparedWireRequest PreparePostJson(
        string relativePath,
        string requestBody,
        bool stream)
        => AdvancedWireRequestBuilder.PreparePostJson(relativePath, requestBody, WireApi, stream);
}
