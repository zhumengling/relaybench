using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class TransparentProxyRouteEditorItemViewModel : ObservableObject
{
    private bool _isEnabled = true;
    private string _name = string.Empty;
    private string _priorityText = string.Empty;
    private string _prefix = string.Empty;
    private string _baseUrl = string.Empty;
    private string _modelsText = string.Empty;
    private string _headersText = string.Empty;
    private string _apiKey = string.Empty;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string PriorityText
    {
        get => _priorityText;
        set => SetProperty(ref _priorityText, value ?? string.Empty);
    }

    public string Prefix
    {
        get => _prefix;
        set => SetProperty(ref _prefix, value ?? string.Empty);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value ?? string.Empty);
    }

    public string Model
    {
        get => Models.FirstOrDefault() ?? string.Empty;
        set => ModelsText = value ?? string.Empty;
    }

    public string ModelsText
    {
        get => _modelsText;
        set
        {
            if (SetProperty(ref _modelsText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Model));
                OnPropertyChanged(nameof(Models));
                OnPropertyChanged(nameof(ModelCountText));
                OnPropertyChanged(nameof(ModelPreviewText));
            }
        }
    }

    public string HeadersText
    {
        get => _headersText;
        set
        {
            if (SetProperty(ref _headersText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Headers));
            }
        }
    }

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ApiKeyPreview));
            }
        }
    }

    public string ApiKeyPreview
    {
        get
        {
            var value = ApiKey.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return "继承请求";
            }

            return value.Length <= 8 ? "***" : $"{value[..Math.Min(3, value.Length)]}...{value[^Math.Min(4, value.Length)..]}";
        }
    }

    public IReadOnlyList<string> Models
        => SplitLines(ModelsText);

    public IReadOnlyDictionary<string, string> Headers
        => ParseHeaders(HeadersText);

    public int Priority
        => int.TryParse(PriorityText.Trim(), out var priority) ? Math.Max(0, priority) : 0;

    public string ModelCountText
        => Models.Count == 0 ? "自动" : $"{Models.Count} 个";

    public string ModelPreviewText
        => Models.Count == 0 ? "未拉取，按请求模型透传" : string.Join(", ", Models.Take(3)) + (Models.Count > 3 ? "..." : string.Empty);

    private static IReadOnlyList<string> SplitLines(string value)
        => (value ?? string.Empty)
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyDictionary<string, string> ParseHeaders(string value)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var headerValue = line[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                headers[name] = headerValue;
            }
        }

        return headers;
    }
}
