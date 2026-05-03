using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class TransparentProxyRouteEditorItemViewModel : ObservableObject
{
    private bool _isEnabled = true;
    private string _name = string.Empty;
    private string _baseUrl = string.Empty;
    private string _model = string.Empty;
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

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value ?? string.Empty);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value ?? string.Empty);
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
}
