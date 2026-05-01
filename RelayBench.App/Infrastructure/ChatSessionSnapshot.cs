using RelayBench.Core.Models;

namespace RelayBench.App.Infrastructure;

public sealed class ChatSessionSnapshot
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    public string ManualTitle { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string SystemPrompt { get; set; } = string.Empty;

    public string TemperatureText { get; set; } = "0.7";

    public string MaxTokensText { get; set; } = "2048";

    public string ReasoningEffortKey { get; set; } = "auto";

    public List<string> SelectedModels { get; set; } = [];

    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed class ChatPresetSnapshot
{
    public string PresetId { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } = string.Empty;

    public string TemperatureText { get; set; } = "0.7";

    public string MaxTokensText { get; set; } = "2048";

    public string ReasoningEffortKey { get; set; } = "auto";

    public bool IsBuiltIn { get; set; }
}

public sealed class ChatSessionsDocument
{
    public string ActiveSessionId { get; set; } = string.Empty;

    public List<ChatSessionSnapshot> Sessions { get; set; } = [];

    public List<ChatPresetSnapshot> Presets { get; set; } = [];
}
