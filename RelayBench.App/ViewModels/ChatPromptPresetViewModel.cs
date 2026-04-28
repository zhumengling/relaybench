using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class ChatPromptPresetViewModel : ObservableObject
{
    private string _name;
    private string _systemPrompt;
    private string _temperatureText;
    private string _maxTokensText;
    private string _reasoningEffortKey;

    public ChatPromptPresetViewModel(
        string presetId,
        string name,
        string systemPrompt,
        string temperatureText,
        string maxTokensText,
        string reasoningEffortKey,
        bool isBuiltIn)
    {
        PresetId = presetId;
        _name = name;
        _systemPrompt = systemPrompt;
        _temperatureText = temperatureText;
        _maxTokensText = maxTokensText;
        _reasoningEffortKey = reasoningEffortKey;
        IsBuiltIn = isBuiltIn;
    }

    public string PresetId { get; }

    public bool IsBuiltIn { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string SystemPrompt
    {
        get => _systemPrompt;
        set => SetProperty(ref _systemPrompt, value);
    }

    public string TemperatureText
    {
        get => _temperatureText;
        set => SetProperty(ref _temperatureText, value);
    }

    public string MaxTokensText
    {
        get => _maxTokensText;
        set => SetProperty(ref _maxTokensText, value);
    }

    public string ReasoningEffortKey
    {
        get => _reasoningEffortKey;
        set => SetProperty(ref _reasoningEffortKey, value);
    }

    public string KindLabel => IsBuiltIn ? "\u5185\u7f6e" : "\u81ea\u5b9a\u4e49";
}
