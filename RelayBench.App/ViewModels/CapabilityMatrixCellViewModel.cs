using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed class CapabilityMatrixCellViewModel
{
    public CapabilityMatrixCellViewModel(
        ProxyProbeScenarioKind scenario,
        string name,
        string stateText,
        string modelText,
        string statusCodeText,
        string latencyText,
        string summary,
        string detailText)
    {
        Scenario = scenario;
        Name = name;
        StateText = stateText;
        ModelText = modelText;
        StatusCodeText = statusCodeText;
        LatencyText = latencyText;
        Summary = summary;
        DetailText = detailText;
    }

    public ProxyProbeScenarioKind Scenario { get; }

    public string Name { get; }

    public string StateText { get; }

    public string ModelText { get; }

    public string StatusCodeText { get; }

    public string LatencyText { get; }

    public string Summary { get; }

    public string DetailText { get; }
}
