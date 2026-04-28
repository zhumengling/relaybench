using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class ChatModelSelectionViewModel : ObservableObject
{
    private int _ordinal;

    public ChatModelSelectionViewModel(int ordinal, string modelName)
    {
        _ordinal = ordinal;
        ModelName = modelName;
    }

    public int Ordinal
    {
        get => _ordinal;
        set
        {
            if (SetProperty(ref _ordinal, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string ModelName { get; }

    public string DisplayName => $"{Ordinal}. {ModelName}";
}
