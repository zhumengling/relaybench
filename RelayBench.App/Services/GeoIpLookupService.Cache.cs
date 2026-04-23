using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

public sealed partial class GeoIpLookupService
{
    private Dictionary<string, GeoIpCacheEntry> LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return [];
            }

            var json = File.ReadAllText(_cacheFilePath);
            var entries = JsonSerializer.Deserialize<List<GeoIpCacheEntry>>(json, SerializerOptions) ?? [];
            return entries
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.Address))
                .ToDictionary(entry => entry.Address, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    private void SaveCache()
    {
        var json = JsonSerializer.Serialize(_cache.Values.OrderBy(entry => entry.Address).ToList(), SerializerOptions);
        File.WriteAllText(_cacheFilePath, json);
    }

    private static bool IsPublicAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIpv4Address(address),
            AddressFamily.InterNetworkV6 => IsPublicIpv6Address(address),
            _ => false
        };
    }

    private static bool IsPublicIpv4Address(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return !(
            bytes[0] == 10 ||
            bytes[0] == 0 ||
            bytes[0] == 127 ||
            (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
            (bytes[0] == 198 && bytes[1] == 18) ||
            (bytes[0] == 198 && bytes[1] == 19) ||
            (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
            (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
            bytes[0] >= 224);
    }

    private static bool IsPublicIpv6Address(IPAddress address)
    {
        if (address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal ||
            address.Equals(IPAddress.IPv6None) ||
            address.Equals(IPAddress.IPv6Loopback))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return false;
        }

        return !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.2 (Windows desktop diagnostics)");
        return client;
    }
}
