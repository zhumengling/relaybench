using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class ProxySelectableModelItemViewModel : ObservableObject
{
    private bool _isSelected;

    public ProxySelectableModelItemViewModel(string name, bool isSelected = false)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
