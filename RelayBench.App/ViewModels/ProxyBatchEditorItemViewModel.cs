using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class ProxyBatchEditorItemViewModel : ObservableObject
{
    private string _entryName;
    private string _baseUrl;
    private string? _entryApiKey;
    private string? _entryModel;
    private string? _siteGroupName;
    private string? _siteGroupApiKey;
    private string? _siteGroupModel;
    private bool _includeInBatchTest;

    public ProxyBatchEditorItemViewModel(
        string entryName,
        string baseUrl,
        string? entryApiKey,
        string? entryModel,
        string? siteGroupName,
        string? siteGroupApiKey,
        string? siteGroupModel,
        bool includeInBatchTest = true)
    {
        _entryName = entryName;
        _baseUrl = baseUrl;
        _entryApiKey = entryApiKey;
        _entryModel = entryModel;
        _siteGroupName = siteGroupName;
        _siteGroupApiKey = siteGroupApiKey;
        _siteGroupModel = siteGroupModel;
        _includeInBatchTest = includeInBatchTest;
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

    public bool IncludeInBatchTest
    {
        get => _includeInBatchTest;
        set
        {
            if (SetProperty(ref _includeInBatchTest, value))
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
            ? "\u7f3a URL"
            : LooksLikeUrl(BaseUrl)
                ? "\u6709\u6548"
                : "URL \u65e0\u6548";

    public string DisplayTitle
        => string.IsNullOrWhiteSpace(SiteGroupName) ? ResolvedEntryName : $"{SiteGroupName} / {ResolvedEntryName}";

    public string KeyDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(EntryApiKey))
            {
                return $"\u672c\u884c key\uff1a{MaskKey(EntryApiKey!)}";
            }

            if (!string.IsNullOrWhiteSpace(SiteGroupApiKey))
            {
                return $"\u7ad9\u5185\u5171\u7528 key\uff1a{MaskKey(SiteGroupApiKey!)}";
            }

            return "\u672a\u5355\u72ec\u586b\u5199 key";
        }
    }

    public string ModelDisplay
        => !string.IsNullOrWhiteSpace(EntryModel)
            ? $"\u672c\u884c\u6a21\u578b\uff1a{EntryModel}"
            : !string.IsNullOrWhiteSpace(SiteGroupModel)
                ? $"\u7ad9\u5185\u5171\u7528\u6a21\u578b\uff1a{SiteGroupModel}"
                : "\u672a\u5355\u72ec\u586b\u5199\u6a21\u578b";

    public string SiteGroupDisplay
        => string.IsNullOrWhiteSpace(SiteGroupName)
            ? "\u72ec\u7acb\u5165\u53e3"
            : $"\u7ad9\u70b9\u7ec4\uff1a{SiteGroupName}";

    public string BatchTestDisplay
        => IncludeInBatchTest ? "\u52a0\u5165\u6d4b\u8bd5" : "\u8df3\u8fc7\u6d4b\u8bd5";

    public string BatchTestShortDisplay
        => IncludeInBatchTest ? "\u52a0\u5165" : "\u8df3\u8fc7";

    public void ApplyFrom(ProxyBatchEditorItemViewModel other)
    {
        EntryName = other.EntryName;
        BaseUrl = other.BaseUrl;
        EntryApiKey = other.EntryApiKey;
        EntryModel = other.EntryModel;
        SiteGroupName = other.SiteGroupName;
        SiteGroupApiKey = other.SiteGroupApiKey;
        SiteGroupModel = other.SiteGroupModel;
        IncludeInBatchTest = other.IncludeInBatchTest;
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(ResolvedEntryName));
        OnPropertyChanged(nameof(TemplateStatus));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(KeyDisplay));
        OnPropertyChanged(nameof(ModelDisplay));
        OnPropertyChanged(nameof(SiteGroupDisplay));
        OnPropertyChanged(nameof(BatchTestDisplay));
        OnPropertyChanged(nameof(BatchTestShortDisplay));
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

        return "\u672a\u547d\u540d\u5165\u53e3";
    }
}
