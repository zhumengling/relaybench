using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed class ClientApplyTargetItemViewModel : ObservableObject
{
    private bool _isSelected;

    public ClientApplyTargetItemViewModel(ClientApplyTarget target)
    {
        TargetId = target.Id;
        DisplayName = target.DisplayName;
        Protocol = target.Protocol;
        IsInstalled = target.IsInstalled;
        IsSelectable = target.IsSelectable;
        IsProtocolSupported = target.IsProtocolSupported;
        ConfigSummary = target.ConfigSummary;
        DisabledReason = target.DisabledReason ?? string.Empty;
        _isSelected = target.IsSelectable && target.IsProtocolSupported && target.IsDefaultSelected;
    }

    public string TargetId { get; }

    public string DisplayName { get; }

    public ClientApplyProtocolKind Protocol { get; }

    public bool IsInstalled { get; }

    public bool IsSelectable { get; }

    public bool IsProtocolSupported { get; }

    public bool RequiresCompatibilityConfirmation
        => IsSelectable && !IsProtocolSupported;

    public string ConfigSummary { get; }

    public string DisabledReason { get; }

    public bool HasDisabledReason => !string.IsNullOrWhiteSpace(DisabledReason);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsSelectable)
            {
                value = false;
            }

            SetProperty(ref _isSelected, value);
        }
    }

    public void RefreshSelectionState()
        => OnPropertyChanged(nameof(IsSelected));

    public string ProtocolText
        => Protocol switch
        {
            ClientApplyProtocolKind.Responses => "Responses",
            ClientApplyProtocolKind.OpenAiCompatible => "OpenAI 兼容",
            ClientApplyProtocolKind.Anthropic => "Anthropic Messages",
            _ => Protocol.ToString()
        };
}
