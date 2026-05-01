namespace RelayBench.Core.Models;

public enum ChatReasoningEffort
{
    Auto,
    Low,
    Medium,
    High
}

public sealed record ChatRequestOptions(
    string BaseUrl,
    string ApiKey,
    string Model,
    string SystemPrompt,
    double Temperature,
    int MaxTokens,
    bool IgnoreTlsErrors,
    int TimeoutSeconds,
    ChatReasoningEffort ReasoningEffort,
    bool PreferResponsesApi)
{
    public string? PreferredWireApi { get; init; }
}
