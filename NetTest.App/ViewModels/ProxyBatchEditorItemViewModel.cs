using NetTest.App.Infrastructure;

namespace NetTest.App.ViewModels;

public sealed class ProxyBatchEditorItemViewModel : ObservableObject
{
    private string _entryName;
    private string _baseUrl;
    private string? _entryApiKey;
    private string? _entryModel;
    private string? _siteGroupName;
    private string? _siteGroupApiKey;
    private string? _siteGroupModel;

    public ProxyBatchEditorItemViewModel(
        string entryName,
        string baseUrl,
        string? entryApiKey,
        string? entryModel,
        string? siteGroupName,
        string? siteGroupApiKey,
        string? siteGroupModel)
    {
        _entryName = entryName;
        _baseUrl = baseUrl;
        _entryApiKey = entryApiKey;
        _entryModel = entryModel;
        _siteGroupName = siteGroupName;
        _siteGroupApiKey = siteGroupApiKey;
        _siteGroupModel = siteGroupModel;
    }

    public string EntryName
    {
        get => _entryName;
        set
        {
            if (SetProperty(ref _entryName, value))
            {
                NotifySummaryChanged();
            }
        }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value))
            {
                NotifySummaryChanged();
            }
        }
    }

    public string? EntryApiKey
    {
        get => _entryApiKey;
        set
        {
            if (SetProperty(ref _entryApiKey, value))
            {
                NotifySummaryChanged();
            }
        }
    }

    public string? EntryModel
    {
        get => _entryModel;
        set
        {
            if (SetProperty(ref _entryModel, value))
            {
                NotifySummaryChanged();
            }
        }
    }

    public string? SiteGroupName
    {
        get => _siteGroupName;
        set
        {
            if (SetProperty(ref _siteGroupName, value))
            {
                NotifySummaryChanged();
            }
        }
    }

    public string? SiteGroupApiKey
    {
        get => _siteGroupApiKey;
        set
        {
            if (SetProperty(ref _siteGroupApiKey, value))
            {
                NotifySummaryChanged();
            }
        }
    }

    public string? SiteGroupModel
    {
        get => _siteGroupModel;
        set
        {
            if (SetProperty(ref _siteGroupModel, value))
            {
                NotifySummaryChanged();
            }
        }
    }

    public string ResolvedEntryName
        => string.IsNullOrWhiteSpace(EntryName)
            ? BuildFallbackEntryName(BaseUrl)
            : EntryName.Trim();

    public string TemplateStatus
        => string.IsNullOrWhiteSpace(BaseUrl)
            ? "缺 URL"
            : LooksLikeUrl(BaseUrl)
                ? "有效"
                : "URL 无效";

    public string DisplayTitle
        => string.IsNullOrWhiteSpace(SiteGroupName) ? ResolvedEntryName : $"{SiteGroupName} / {ResolvedEntryName}";

    public string KeyDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(EntryApiKey))
            {
                return $"本条目 key：{MaskKey(EntryApiKey!)}";
            }

            if (!string.IsNullOrWhiteSpace(SiteGroupApiKey))
            {
                return $"同站共用 key：{MaskKey(SiteGroupApiKey!)}";
            }

            return "未单独填写 key";
        }
    }

    public string ModelDisplay
        => !string.IsNullOrWhiteSpace(EntryModel)
            ? $"本条目模型：{EntryModel}"
            : !string.IsNullOrWhiteSpace(SiteGroupModel)
                ? $"同站共用模型：{SiteGroupModel}"
                : "未单独填写模型";

    public string SiteGroupDisplay
        => string.IsNullOrWhiteSpace(SiteGroupName) ? "独立入口" : $"同站组：{SiteGroupName}";

    public void ApplyFrom(ProxyBatchEditorItemViewModel other)
    {
        EntryName = other.EntryName;
        BaseUrl = other.BaseUrl;
        EntryApiKey = other.EntryApiKey;
        EntryModel = other.EntryModel;
        SiteGroupName = other.SiteGroupName;
        SiteGroupApiKey = other.SiteGroupApiKey;
        SiteGroupModel = other.SiteGroupModel;
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(ResolvedEntryName));
        OnPropertyChanged(nameof(TemplateStatus));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(KeyDisplay));
        OnPropertyChanged(nameof(ModelDisplay));
        OnPropertyChanged(nameof(SiteGroupDisplay));
    }

    private static string MaskKey(string apiKey)
    {
        var value = apiKey.Trim();
        return value.Length <= 10 ? "******" : $"{value[..6]}...{value[^4..]}";
    }

    private static bool LooksLikeUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string BuildFallbackEntryName(string? baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return "未命名入口";
    }
}
