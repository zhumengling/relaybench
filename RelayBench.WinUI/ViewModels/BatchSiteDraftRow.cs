using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class BatchSiteDraftRow : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string BaseUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string ApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string Model { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsIncluded { get; set; } = true;
    [ObservableProperty] public partial bool IsFetchingModels { get; set; }
    [ObservableProperty] public partial string ModelCatalogSummary { get; set; } = "未拉取模型";
    [ObservableProperty] public partial string ProtocolSummary { get; set; } = "未探测";

    public ObservableCollection<string> AvailableModels { get; } = new();

    public bool HasContent => !string.IsNullOrWhiteSpace(Name) ||
                              !string.IsNullOrWhiteSpace(BaseUrl) ||
                              !string.IsNullOrWhiteSpace(ApiKey) ||
                              !string.IsNullOrWhiteSpace(Model);

    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name.Trim()
        : !string.IsNullOrWhiteSpace(BaseUrl)
            ? BatchEndpointText.TryGetHost(BaseUrl) ?? BaseUrl.Trim()
            : "Draft endpoint row";

    public string KeyPreview => string.IsNullOrWhiteSpace(ApiKey)
        ? "缺 Key"
        : ApiKey.Length <= 8
            ? "Key 已填"
            : $"{ApiKey[..Math.Min(4, ApiKey.Length)]}...{ApiKey[^Math.Min(4, ApiKey.Length)..]}";

    public string StatusText
    {
        get
        {
            if (!HasContent)
            {
                return "空白";
            }

            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                return "缺 URL";
            }

            if (!BatchEndpointText.LooksLikeBaseUrl(BaseUrl))
            {
                return "URL 无效";
            }

            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                return "缺 Key";
            }

            return IsIncluded ? "可加入" : "跳过";
        }
    }

    public BatchSiteDraftRow Duplicate()
    {
        var copy = new BatchSiteDraftRow
        {
            Name = Name,
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            Model = Model,
            IsIncluded = IsIncluded,
            ModelCatalogSummary = ModelCatalogSummary,
            ProtocolSummary = ProtocolSummary,
        };

        foreach (var model in AvailableModels)
        {
            copy.AvailableModels.Add(model);
        }

        return copy;
    }

    partial void OnNameChanged(string value) => RefreshComputed();
    partial void OnBaseUrlChanged(string value) => RefreshComputed();

    partial void OnApiKeyChanged(string value)
    {
        OnPropertyChanged(nameof(KeyPreview));
        RefreshComputed();
    }

    partial void OnModelChanged(string value) => RefreshComputed();
    partial void OnIsIncludedChanged(bool value) => RefreshComputed();

    private void RefreshComputed()
    {
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(StatusText));
    }
}
