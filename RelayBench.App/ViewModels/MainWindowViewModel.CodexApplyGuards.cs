using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static bool CanApplyModelToCodexApps(string? model)
        => CodexFamilyConfigApplyService.IsCodexSupportedChatGptModel(model);

    private static string BuildCodexUnsupportedModelMessage(string? model)
        => $"当前模型“{FormatPreviewValue(model)}”不是 ChatGPT/OpenAI 系列模型，暂不允许应用到 Codex。";

    private static string BuildCodexUnsupportedModelMessage(string entryName, string? model)
        => $"“{entryName}”的模型“{FormatPreviewValue(model)}”不是 ChatGPT/OpenAI 系列模型，暂不允许应用到 Codex。";
}
