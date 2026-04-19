using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetTest.App.Infrastructure;
using NetTest.Core.Support;

namespace NetTest.App.Services;

public sealed partial class GeoIpLookupService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _cacheFilePath;
    private readonly Dictionary<string, GeoIpCacheEntry> _cache;
    private DateTimeOffset _lastRemoteLookupAt = DateTimeOffset.MinValue;

    public GeoIpLookupService()
    {
        var dataDirectory = NetTestPaths.DataDirectory;
        Directory.CreateDirectory(dataDirectory);
        _cacheFilePath = NetTestPaths.GeoIpCachePath;
        _cache = LoadCache();
    }

    public async Task<IReadOnlyList<RouteGeoHopResult>> LookupRouteHopsAsync(
        IReadOnlyList<string> addresses,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = await LookupAddressesAsync(addresses, progress, cancellationToken);
        return results
            .Select(result => result.ToRouteGeoHopResult(0))
            .ToArray();
    }

    public async Task<RouteGeoHopResult?> LookupCurrentPublicOriginAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report("正在获取当前公网出口位置...");
            using var response = await HttpClient.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", cancellationToken);
            response.EnsureSuccessStatusCode();
            var rawText = await response.Content.ReadAsStringAsync(cancellationToken);
            var values = TraceDocumentParser.Parse(rawText);
            if (!values.TryGetValue("ip", out var publicIp) || !IsPublicAddress(publicIp))
            {
                return null;
            }

            var insight = await LookupAsync(publicIp, cancellationToken);
            if (insight is null)
            {
                return null;
            }

            var role = string.IsNullOrWhiteSpace(insight.NetworkRole)
                ? "当前公网出口"
                : $"{insight.NetworkRole} / 当前公网出口";

            return insight.ToRouteGeoHopResult(0) with
            {
                NetworkRole = role
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<GeoIpInsightResult>> LookupAddressesAsync(
        IReadOnlyList<string> addresses,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = addresses
            .Where(IsPublicAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<GeoIpInsightResult> results = new(candidates.Length);
        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var address = candidates[index];
            progress?.Report($"正在查询 IP 归属 {index + 1}/{candidates.Length}：{address}");
            var lookupResult = await LookupAsync(address, cancellationToken);
            if (lookupResult is not null)
            {
                results.Add(lookupResult);
            }
        }

        return results;
    }

    private async Task<GeoIpInsightResult?> LookupAsync(string address, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(address, out var cachedEntry) && cachedEntry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cachedEntry.ToResult();
        }

        await RespectRateLimitAsync(cancellationToken);

        try
        {
            var response = await HttpClient.GetFromJsonAsync<IpWhoIsResponse>($"https://ipwho.is/{address}", cancellationToken);
            _lastRemoteLookupAt = DateTimeOffset.UtcNow;
            if (response?.Success != true || response.Latitude is null || response.Longitude is null)
            {
                CacheFailure(address, response?.Message ?? "地理定位查询失败。");
                return null;
            }

            var result = new GeoIpInsightResult(
                response.Ip ?? address,
                response.City,
                response.Region,
                response.Country,
                response.CountryCode,
                response.Continent,
                response.ContinentCode,
                response.Connection?.Asn,
                response.Connection?.Organization,
                response.Connection?.Isp,
                response.Connection?.Domain,
                InferNetworkRole(response.Connection?.Organization, response.Connection?.Isp, response.Connection?.Domain),
                response.Latitude.Value,
                response.Longitude.Value);

            _cache[address] = GeoIpCacheEntry.FromResult(result, DateTimeOffset.UtcNow.AddDays(30));
            SaveCache();
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            CacheFailure(address, ex.Message);
            return null;
        }
    }

    private void CacheFailure(string address, string? error)
    {
        _cache[address] = new GeoIpCacheEntry
        {
            Address = address,
            Error = error,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(6)
        };

        SaveCache();
    }

    private async Task RespectRateLimitAsync(CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - _lastRemoteLookupAt);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }
}
