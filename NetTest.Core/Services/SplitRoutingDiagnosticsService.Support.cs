using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using NetTest.Core.Models;
using NetTest.Core.Support;

namespace NetTest.Core.Services;

public sealed partial class SplitRoutingDiagnosticsService
{
    private static IReadOnlyList<SplitRoutingAdapterView> GetAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUsefulAdapter)
            .Select(adapter => new SplitRoutingAdapterView(
                adapter.Name,
                adapter.Description,
                adapter.NetworkInterfaceType.ToString(),
                adapter.GetIPProperties().UnicastAddresses
                    .Select(address => address.Address.ToString())
                    .Distinct()
                    .OrderBy(static value => value)
                    .ToArray(),
                adapter.GetIPProperties().DnsAddresses
                    .Select(address => address.ToString())
                    .Distinct()
                    .OrderBy(static value => value)
                    .ToArray()))
            .ToArray();
    }

    private static bool IsUsefulAdapter(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        return adapter.GetIPProperties().UnicastAddresses.Count > 0;
    }

    private static string? TryGetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var headerValues))
        {
            return headerValues.FirstOrDefault();
        }

        if (response.Content.Headers.TryGetValues(name, out headerValues))
        {
            return headerValues.FirstOrDefault();
        }

        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.4 (Windows desktop diagnostics)");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }
}
