using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class TransparentProxyModelMappingViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _alias = string.Empty;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(EffectiveAlias));
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string Alias
    {
        get => _alias;
        set
        {
            if (SetProperty(ref _alias, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(EffectiveAlias));
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string EffectiveAlias
    {
        get
        {
            var alias = Alias.Trim();
            return string.IsNullOrWhiteSpace(alias) ? Name.Trim() : alias;
        }
    }

    public string DisplayText
        => string.IsNullOrWhiteSpace(EffectiveAlias) || string.Equals(Name.Trim(), EffectiveAlias, StringComparison.Ordinal)
            ? Name.Trim()
            : $"{Name.Trim()} -> {EffectiveAlias}";
}
