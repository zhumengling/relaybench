using NetTest.App.Infrastructure;

namespace NetTest.App.ViewModels;

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
        Func<Task> openRawTraceAsync)
    {
        Provider = provider;
        Name = name;
        AvailabilityText = availabilityText;
        Summary = summary;
        EndpointMetaText = endpointMetaText;
        StatusBackground = statusBackground;
        StatusForeground = statusForeground;
        OpenRawTraceCommand = new AsyncRelayCommand(openRawTraceAsync);
    }

    public string Provider { get; }

    public string Name { get; }

    public string AvailabilityText { get; }

    public string Summary { get; }

    public string EndpointMetaText { get; }

    public string StatusBackground { get; }

    public string StatusForeground { get; }

    public AsyncRelayCommand OpenRawTraceCommand { get; }
}
