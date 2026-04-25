using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class ProxyEndpointHistoryItemViewModel : ObservableObject
{
    private bool _isSelected;

    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset LastUsedAt { get; init; } = DateTimeOffset.Now;

    public int UseCount { get; init; } = 1;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DisplayTitle
        => string.IsNullOrWhiteSpace(Model)
            ? BaseUrl
            : $"{Model}  ·  {BaseUrl}";

    public string DisplayMeta
        => $"最近使用 {LastUsedAt:yyyy-MM-dd HH:mm:ss}  /  累计 {Math.Max(1, UseCount)} 次";

    public string KeyPreview
        => string.IsNullOrWhiteSpace(ApiKey) ? "未保存 Key" : MaskApiKey(ApiKey);

    public string ModelPreview
        => string.IsNullOrWhiteSpace(Model) ? "模型：未填写" : $"模型：{Model}";

    public string BaseUrlPreview
        => string.IsNullOrWhiteSpace(BaseUrl) ? "接口地址：未填写" : $"接口地址：{BaseUrl}";

    private static string MaskApiKey(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= 8)
        {
            return new string('•', Math.Max(3, trimmed.Length));
        }

        return $"{trimmed[..4]}••••{trimmed[^4..]}";
    }
}
