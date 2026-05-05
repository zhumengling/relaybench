using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

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
    private string _excludedModelsText = string.Empty;
    private string _outboundProxy = string.Empty;
    private string _requestRetryText = string.Empty;
    private string _maxRetryIntervalSecondsText = string.Empty;
    private string _modelCooldownSecondsText = string.Empty;
    private string _payloadRulesText = string.Empty;
    private string _apiKey = string.Empty;
    private bool _isSyncingModelMappings;

    public TransparentProxyRouteEditorItemViewModel()
    {
        ModelMappings.CollectionChanged += ModelMappings_OnCollectionChanged;
    }

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
                if (!_isSyncingModelMappings)
                {
                    LoadModelMappingsFromText(_modelsText);
                }

                NotifyModelPropertiesChanged();
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

    public string ExcludedModelsText
    {
        get => _excludedModelsText;
        set => SetProperty(ref _excludedModelsText, value ?? string.Empty);
    }

    public string OutboundProxy
    {
        get => _outboundProxy;
        set => SetProperty(ref _outboundProxy, value ?? string.Empty);
    }

    public string RequestRetryText
    {
        get => _requestRetryText;
        set => SetProperty(ref _requestRetryText, value ?? string.Empty);
    }

    public string MaxRetryIntervalSecondsText
    {
        get => _maxRetryIntervalSecondsText;
        set => SetProperty(ref _maxRetryIntervalSecondsText, value ?? string.Empty);
    }

    public string ModelCooldownSecondsText
    {
        get => _modelCooldownSecondsText;
        set => SetProperty(ref _modelCooldownSecondsText, value ?? string.Empty);
    }

    public string PayloadRulesText
    {
        get => _payloadRulesText;
        set
        {
            if (SetProperty(ref _payloadRulesText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(PayloadRulePreview));
                OnPropertyChanged(nameof(PayloadRuleSummary));
                OnPropertyChanged(nameof(PayloadRuleStateBrush));
            }
        }
    }

    public TransparentProxyPayloadRuleViewModel PayloadRulePreview
        => TransparentProxyPayloadRuleViewModel.FromText(PayloadRulesText);

    public string PayloadRuleSummary
        => PayloadRulePreview.Summary;

    public string PayloadRuleStateBrush
        => PayloadRulePreview.StateBrush;

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

    public ObservableCollection<TransparentProxyModelMappingViewModel> ModelMappings { get; } = [];

    public IReadOnlyList<string> Models
        => ModelMappings
            .Select(static item => item.Name.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public string ModelMappingsText
        => SerializeModelMappings(ModelMappings);

    public IReadOnlyDictionary<string, string> Headers
        => ParseHeaders(HeadersText);

    public int Priority
        => int.TryParse(PriorityText.Trim(), out var priority) ? Math.Max(0, priority) : 0;

    public string ModelCountText
        => Models.Count == 0 ? "自动" : $"{Models.Count} 个";

    public string ModelPreviewText
    {
        get
        {
            if (Models.Count == 0)
            {
                return "未拉取，按请求模型透传";
            }

            var preview = ModelMappings
                .Select(static item => item.DisplayText)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Take(3);
            return string.Join(", ", preview) + (Models.Count > 3 ? "..." : string.Empty);
        }
    }

    public TransparentProxyModelMappingViewModel AddModelMapping(string? name = null, string? alias = null)
    {
        var item = new TransparentProxyModelMappingViewModel
        {
            Name = name ?? string.Empty,
            Alias = alias ?? string.Empty
        };
        ModelMappings.Add(item);
        return item;
    }

    public void RemoveModelMapping(TransparentProxyModelMappingViewModel? item)
    {
        if (item is not null)
        {
            ModelMappings.Remove(item);
        }
    }

    public void ReplaceModelMappings(IEnumerable<string> names)
    {
        var aliasByName = ModelMappings
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(static item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Alias, StringComparer.OrdinalIgnoreCase);
        var mappings = names
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(model => new TransparentProxyModelMappingViewModel
            {
                Name = model,
                Alias = aliasByName.TryGetValue(model, out var alias) ? alias : model
            })
            .ToArray();

        _isSyncingModelMappings = true;
        try
        {
            foreach (var existing in ModelMappings)
            {
                existing.PropertyChanged -= ModelMapping_OnPropertyChanged;
            }

            ModelMappings.Clear();
            foreach (var mapping in mappings)
            {
                ModelMappings.Add(mapping);
            }
        }
        finally
        {
            _isSyncingModelMappings = false;
        }

        SyncModelsTextFromMappings();
    }

    public void ReplaceModelMappings(IEnumerable<TransparentProxyModelMapping> mappings)
    {
        var normalized = mappings
            .Where(static mapping => !string.IsNullOrWhiteSpace(mapping.Name))
            .GroupBy(static mapping => mapping.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(static mapping => new TransparentProxyModelMappingViewModel
            {
                Name = mapping.Name.Trim(),
                Alias = mapping.Alias.Trim()
            })
            .ToArray();

        _isSyncingModelMappings = true;
        try
        {
            foreach (var existing in ModelMappings)
            {
                existing.PropertyChanged -= ModelMapping_OnPropertyChanged;
            }

            ModelMappings.Clear();
            foreach (var mapping in normalized)
            {
                ModelMappings.Add(mapping);
            }
        }
        finally
        {
            _isSyncingModelMappings = false;
        }

        SyncModelsTextFromMappings();
    }

    private void LoadModelMappingsFromText(string value)
    {
        var mappings = ParseModelMappings(value)
            .Select(static mapping => new TransparentProxyModelMappingViewModel
            {
                Name = mapping.Name,
                Alias = mapping.Alias
            })
            .ToArray();

        _isSyncingModelMappings = true;
        try
        {
            foreach (var existing in ModelMappings)
            {
                existing.PropertyChanged -= ModelMapping_OnPropertyChanged;
            }

            ModelMappings.Clear();
            foreach (var mapping in mappings)
            {
                ModelMappings.Add(mapping);
            }
        }
        finally
        {
            _isSyncingModelMappings = false;
        }
    }

    private void ModelMappings_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (TransparentProxyModelMappingViewModel item in e.OldItems)
            {
                item.PropertyChanged -= ModelMapping_OnPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (TransparentProxyModelMappingViewModel item in e.NewItems)
            {
                item.PropertyChanged += ModelMapping_OnPropertyChanged;
            }
        }

        SyncModelsTextFromMappings();
    }

    private void ModelMapping_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => SyncModelsTextFromMappings();

    private void SyncModelsTextFromMappings()
    {
        if (_isSyncingModelMappings)
        {
            return;
        }

        var text = SerializeModelMappings(ModelMappings);
        if (SetProperty(ref _modelsText, text, nameof(ModelsText)))
        {
            NotifyModelPropertiesChanged();
        }
    }

    private void NotifyModelPropertiesChanged()
    {
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(Models));
        OnPropertyChanged(nameof(ModelMappings));
        OnPropertyChanged(nameof(ModelMappingsText));
        OnPropertyChanged(nameof(ModelCountText));
        OnPropertyChanged(nameof(ModelPreviewText));
    }

    private static IReadOnlyList<(string Name, string Alias)> ParseModelMappings(string value)
    {
        List<(string Name, string Alias)> mappings = [];
        foreach (var token in (value ?? string.Empty).Split([',', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = token;
            var alias = string.Empty;
            var separator = token.IndexOf("=>", StringComparison.Ordinal);
            if (separator < 0)
            {
                separator = token.IndexOf("->", StringComparison.Ordinal);
            }

            if (separator >= 0)
            {
                name = token[..separator];
                alias = token[(separator + 2)..];
            }

            name = CleanModelToken(name);
            alias = CleanModelToken(alias);
            if (!string.IsNullOrWhiteSpace(name) &&
                mappings.All(item => !string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                mappings.Add((name, alias));
            }
        }

        return mappings;
    }

    private static string SerializeModelMappings(IEnumerable<TransparentProxyModelMappingViewModel> mappings)
        => string.Join(",", mappings
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(static item =>
            {
                var name = CleanModelToken(item.Name);
                var alias = CleanModelToken(item.Alias);
                return string.IsNullOrWhiteSpace(alias)
                    ? name
                    : $"{name}=>{alias}";
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string CleanModelToken(string value)
        => (value ?? string.Empty)
            .Replace("|", " ", StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace(";", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

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
