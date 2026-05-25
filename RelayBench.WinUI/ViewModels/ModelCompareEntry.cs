using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a single model's response in a multi-model comparison view.
/// </summary>
public sealed partial class ModelCompareEntry : ObservableObject
{
    [ObservableProperty] public partial string ModelName { get; set; } = "";
    [ObservableProperty] public partial long ResponseTimeMs { get; set; }
    [ObservableProperty] public partial string ResponseText { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }

    /// <summary>
    /// Formatted display string for response time (e.g. "123 ms").
    /// </summary>
    public string ResponseTimeDisplay => ResponseTimeMs > 0 ? $"{ResponseTimeMs:N0} ms" : "--";

    public string ProviderName
    {
        get
        {
            if (ModelName.Contains("claude", StringComparison.OrdinalIgnoreCase))
                return "Anthropic";
            if (ModelName.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
                return "DeepSeek";
            return "OpenAI";
        }
    }

    public string ProviderIcon => ProviderName switch
    {
        "Anthropic" => "AI",
        "DeepSeek" => "QY",
        _ => "OA"
    };

    public Visibility AnthropicProviderVisibility => ProviderName == "Anthropic"
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DeepSeekProviderVisibility => ProviderName == "DeepSeek"
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility OpenAiProviderVisibility => ProviderName == "OpenAI"
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SlowHealthVisibility => ResponseTimeMs > 1000
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility HealthyHealthVisibility => ResponseTimeMs > 1000
        ? Visibility.Collapsed
        : Visibility.Visible;

    partial void OnResponseTimeMsChanged(long value)
    {
        OnPropertyChanged(nameof(ResponseTimeDisplay));
        OnPropertyChanged(nameof(SlowHealthVisibility));
        OnPropertyChanged(nameof(HealthyHealthVisibility));
    }

    partial void OnModelNameChanged(string value)
    {
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(ProviderIcon));
        OnPropertyChanged(nameof(AnthropicProviderVisibility));
        OnPropertyChanged(nameof(DeepSeekProviderVisibility));
        OnPropertyChanged(nameof(OpenAiProviderVisibility));
    }
}
