using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed record ProxyWireApiDecision(
    string ProbeModel,
    bool ChatCompletionsSupported,
    bool ResponsesSupported,
    bool AnthropicMessagesSupported,
    string? PreferredWireApi,
    string Summary,
    IReadOnlyList<ProxyProbeScenarioResult> ScenarioResults);

public static class ProxyWireApiProbeService
{
    public const int CurrentProtocolProbeVersion = 3;
    public const string ChatCompletionsWireApi = "chat";
    public const string ResponsesWireApi = "responses";
    public const string AnthropicMessagesWireApi = "anthropic";

    public static string? NormalizeWireApi(string? wireApi)
    {
        var normalized = (wireApi ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "response" or "responses" or "openai-responses" or "openai responses" => ResponsesWireApi,
            "anthropic" or "messages" or "anthropic-messages" or "anthropic/messages" or "anthropic messages" => AnthropicMessagesWireApi,
            "chat" or "chat-completions" or "chat/completions" or "openai" or "openai-chat" or "openai chat completions" => ChatCompletionsWireApi,
            _ => null
        };
    }

    public static string NormalizeWireApiOrChat(string? wireApi)
        => NormalizeWireApi(wireApi) ?? ChatCompletionsWireApi;

    public static string? ResolvePreferredWireApi(
        bool chatSupported,
        bool responsesSupported,
        bool anthropicSupported)
    {
        if (responsesSupported)
        {
            return ResponsesWireApi;
        }

        if (anthropicSupported)
        {
            return AnthropicMessagesWireApi;
        }

        if (chatSupported)
        {
            return ChatCompletionsWireApi;
        }

        return null;
    }

    public static string BuildSummary(
        string model,
        bool chatSupported,
        bool responsesSupported,
        bool anthropicSupported,
        string? preferredWireApi)
    {
        var chatText = chatSupported ? "chat available" : "chat unavailable";
        var responsesText = responsesSupported ? "responses available" : "responses unavailable";
        var anthropicText = anthropicSupported ? "messages available" : "messages unavailable";
        var preferredText = string.IsNullOrWhiteSpace(preferredWireApi)
            ? "no preferred wire_api"
            : $"preferred wire_api={preferredWireApi}";
        return $"Protocol probe model: {model}; {responsesText}; {anthropicText}; {chatText}; {preferredText}.";
    }
}
