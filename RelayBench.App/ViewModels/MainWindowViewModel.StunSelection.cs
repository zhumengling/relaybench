using RelayBench.App.Services;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void RefreshStunServerOptions(bool syncCurrentHost)
    {
        var transport = GetSelectedStunTransportProtocol();
        var options = StunServerPresetCatalog.BuildServerOptions(transport);

        VisibleStunServerOptions.Clear();
        foreach (var option in options)
        {
            VisibleStunServerOptions.Add(option);
        }

        if (options.Count == 0)
        {
            return;
        }

        var normalizedHost = NormalizeStunServerHost(StunServer);
        var hasCurrentHost = options.Any(option => string.Equals(option.Key, normalizedHost, StringComparison.OrdinalIgnoreCase));
        if (!hasCurrentHost)
        {
            StunServer = StunServerPresetCatalog.ResolveDefaultHost(transport);
        }
        else if (syncCurrentHost)
        {
            StunServer = normalizedHost;
        }
    }

    private StunTransportProtocol GetSelectedStunTransportProtocol()
        => StunServerPresetCatalog.ParseTransportKey(_selectedStunTransportKey);
}
