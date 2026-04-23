using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class OfficialApiStatusRowViewModel
{
    public OfficialApiStatusRowViewModel(
        string provider,
        string name,
        string availabilityText,
        string summary,
        string endpointMetaText,
        string statusBackground,
        string statusForeground,
        Func<Task> openRawTraceAsync,
        Func<Task>? restoreDefaultConfigAsync = null,
        string? stateText = null,
        string? accessDetailText = null,
        string? endpointText = null,
        string? configSourceText = null,
        string? restoreHintText = null)
    {
        Provider = provider;
        Name = name;
        AvailabilityText = availabilityText;
        Summary = summary;
        EndpointMetaText = endpointMetaText;
        StatusBackground = statusBackground;
        StatusForeground = statusForeground;
        StateText = stateText ?? string.Empty;
        AccessDetailText = accessDetailText ?? string.Empty;
        EndpointText = endpointText ?? string.Empty;
        ConfigSourceText = configSourceText ?? string.Empty;
        RestoreHintText = restoreHintText ?? string.Empty;
        OpenRawTraceCommand = new AsyncRelayCommand(openRawTraceAsync);
        RestoreDefaultConfigCommand = restoreDefaultConfigAsync is null
            ? null
            : new AsyncRelayCommand(restoreDefaultConfigAsync);
    }

    public string Provider { get; }

    public string Name { get; }

    public string AvailabilityText { get; }

    public string Summary { get; }

    public string EndpointMetaText { get; }

    public string StatusBackground { get; }

    public string StatusForeground { get; }

    public string StateText { get; }

    public string AccessDetailText { get; }

    public string EndpointText { get; }

    public string ConfigSourceText { get; }

    public string RestoreHintText { get; }

    public AsyncRelayCommand OpenRawTraceCommand { get; }

    public AsyncRelayCommand? RestoreDefaultConfigCommand { get; }

    public bool HasRestoreDefaultConfigAction => RestoreDefaultConfigCommand is not null;
}
