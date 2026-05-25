using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a single site entry in the batch evaluation site editor.
/// Each entry holds the connection configuration for one relay endpoint.
/// </summary>
public sealed partial class BatchSiteEntry : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string BaseUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string ApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string Model { get; set; } = string.Empty;
    [ObservableProperty] public partial int Timeout { get; set; } = 30;
    [ObservableProperty] public partial bool TlsIgnore { get; set; }
    [ObservableProperty] public partial bool IsIncluded { get; set; } = true;
    [ObservableProperty] public partial string GroupName { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsFetchingModels { get; set; }
    [ObservableProperty] public partial string ModelCatalogSummary { get; set; } = "未拉取模型";
    [ObservableProperty] public partial string ProtocolSummary { get; set; } = "未探测";

    public ObservableCollection<string> AvailableModels { get; } = new();

    public BatchSiteEntry() { }

    public BatchSiteEntry(
        string baseUrl,
        string apiKey,
        string model,
        int timeout = 30,
        bool tlsIgnore = false,
        bool isIncluded = true,
        string groupName = "",
        string name = "")
    {
        Name = name;
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        Model = model;
        Timeout = timeout;
        TlsIgnore = tlsIgnore;
        IsIncluded = isIncluded;
        GroupName = groupName;
    }

    /// <summary>
    /// Creates a deep copy of this entry for template duplication.
    /// </summary>
    public BatchSiteEntry Duplicate()
    {
        var duplicate = new BatchSiteEntry(BaseUrl, ApiKey, Model, Timeout, TlsIgnore, IsIncluded, GroupName, Name)
        {
            ModelCatalogSummary = ModelCatalogSummary,
            ProtocolSummary = ProtocolSummary
        };
        foreach (var model in AvailableModels)
        {
            duplicate.AvailableModels.Add(model);
        }

        return duplicate;
    }

    /// <summary>
    /// Display name for the site (derived from BaseUrl or GroupName).
    /// </summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name
        : !string.IsNullOrWhiteSpace(GroupName)
        ? GroupName
        : !string.IsNullOrWhiteSpace(BaseUrl)
            ? ResolveDisplayNameFromUrl(BaseUrl)
            : "空入口";

    public string EndpointDisplay => string.IsNullOrWhiteSpace(BaseUrl)
        ? "未填写接口地址"
        : BaseUrl.Trim();

    public string GroupDisplay => string.IsNullOrWhiteSpace(GroupName)
        ? "未分组"
        : GroupName.Trim();

    public string ApiKeyPreview => string.IsNullOrWhiteSpace(ApiKey)
        ? "缺 Key"
        : ApiKey.Length <= 8
            ? "Key 已填"
            : $"{ApiKey[..Math.Min(4, ApiKey.Length)]}...{ApiKey[^Math.Min(4, ApiKey.Length)..]}";

    public string ModelDisplay => string.IsNullOrWhiteSpace(Model)
        ? "未指定模型"
        : Model.Trim();

    public string ProtocolDisplay => string.IsNullOrWhiteSpace(ProtocolSummary)
        ? "未探测"
        : ProtocolSummary.Trim();

    public string SchemeDisplay => Uri.TryCreate(BatchEndpointText.NormalizeBaseUrl(BaseUrl), UriKind.Absolute, out var uri)
        ? uri.Scheme.ToUpperInvariant()
        : "URL";

    public string EntryStatusText
    {
        get
        {
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

            return IsIncluded ? "可测试" : "已跳过";
        }
    }

    private static string ResolveDisplayNameFromUrl(string value)
    {
        return BatchEndpointText.TryGetHost(value) ?? value.Trim();
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnBaseUrlChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(EndpointDisplay));
        OnPropertyChanged(nameof(SchemeDisplay));
        OnPropertyChanged(nameof(EntryStatusText));
    }

    partial void OnApiKeyChanged(string value)
    {
        OnPropertyChanged(nameof(ApiKeyPreview));
        OnPropertyChanged(nameof(EntryStatusText));
    }

    partial void OnModelChanged(string value) => OnPropertyChanged(nameof(ModelDisplay));
    partial void OnProtocolSummaryChanged(string value) => OnPropertyChanged(nameof(ProtocolDisplay));
    partial void OnIsIncludedChanged(bool value) => OnPropertyChanged(nameof(EntryStatusText));

    partial void OnGroupNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(GroupDisplay));
    }
}
