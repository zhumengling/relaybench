using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetTest.App.Infrastructure;

namespace NetTest.App.Services;

public sealed partial class GeoIpLookupService
{
    private sealed class IpWhoIsResponse
    {
        [JsonPropertyName("ip")]
        public string? Ip { get; init; }

        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("city")]
        public string? City { get; init; }

        [JsonPropertyName("region")]
        public string? Region { get; init; }

        [JsonPropertyName("country")]
        public string? Country { get; init; }

        [JsonPropertyName("country_code")]
        public string? CountryCode { get; init; }

        [JsonPropertyName("continent")]
        public string? Continent { get; init; }

        [JsonPropertyName("continent_code")]
        public string? ContinentCode { get; init; }

        [JsonPropertyName("latitude")]
        public double? Latitude { get; init; }

        [JsonPropertyName("longitude")]
        public double? Longitude { get; init; }

        [JsonPropertyName("connection")]
        public ConnectionInfo? Connection { get; init; }
    }

    private sealed class ConnectionInfo
    {
        [JsonPropertyName("asn")]
        public int? Asn { get; init; }

        [JsonPropertyName("org")]
        public string? Organization { get; init; }

        [JsonPropertyName("isp")]
        public string? Isp { get; init; }

        [JsonPropertyName("domain")]
        public string? Domain { get; init; }
    }

    private sealed class GeoIpCacheEntry
    {
        public string Address { get; init; } = string.Empty;

        public string? City { get; init; }

        public string? Region { get; init; }

        public string? Country { get; init; }

        public string? CountryCode { get; init; }

        public string? Continent { get; init; }

        public string? ContinentCode { get; init; }

        public int? Asn { get; init; }

        public string? Organization { get; init; }

        public string? Isp { get; init; }

        public string? Domain { get; init; }

        public string? NetworkRole { get; init; }

        public double? Latitude { get; init; }

        public double? Longitude { get; init; }

        public string? Error { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }

        public GeoIpInsightResult? ToResult()
        {
            if (Latitude is null || Longitude is null)
            {
                return null;
            }

            return new GeoIpInsightResult(
                Address,
                City,
                Region,
                Country,
                CountryCode,
                Continent,
                ContinentCode,
                Asn,
                Organization,
                Isp,
                Domain,
                NetworkRole,
                Latitude.Value,
                Longitude.Value);
        }

        public static GeoIpCacheEntry FromResult(GeoIpInsightResult result, DateTimeOffset expiresAt)
            => new()
            {
                Address = result.Address,
                City = result.City,
                Region = result.Region,
                Country = result.Country,
                CountryCode = result.CountryCode,
                Continent = result.Continent,
                ContinentCode = result.ContinentCode,
                Asn = result.Asn,
                Organization = result.Organization,
                Isp = result.Isp,
                Domain = result.Domain,
                NetworkRole = result.NetworkRole,
                Latitude = result.Latitude,
                Longitude = result.Longitude,
                ExpiresAt = expiresAt
            };
    }
}
