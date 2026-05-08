using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class CodexConfigTemplateRowViewModel : ObservableObject
{
    private string _value;

    public CodexConfigTemplateRowViewModel(
        string parameter,
        string value,
        string description,
        bool isEditable,
        string valueKind)
    {
        Parameter = parameter;
        _value = value;
        Description = description;
        IsEditable = isEditable;
        ValueKind = valueKind;
    }

    public string Parameter { get; }

    public string Description { get; }

    public bool IsEditable { get; }

    public bool IsReadOnly => !IsEditable;

    public string ValueKind { get; }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }
}
