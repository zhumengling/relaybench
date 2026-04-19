namespace NetTest.App.Services;

public sealed record GeoIpInsightResult(
    string Address,
    string? City,
    string? Region,
    string? Country,
    string? CountryCode,
    string? Continent,
    string? ContinentCode,
    int? Asn,
    string? Organization,
    string? Isp,
    string? Domain,
    string? NetworkRole,
    double Latitude,
    double Longitude)
{
    public string LocationLabel
    {
        get
        {
            var location = string.Join(
                ", ",
                new[] { City, Region, Country }
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));

            return string.IsNullOrWhiteSpace(location) ? "--" : location;
        }
    }

    public string NetworkLabel
    {
        get
        {
            var parts = new List<string>();
            if (Asn is not null)
            {
                parts.Add($"AS{Asn.Value}");
            }

            foreach (var value in new[] { Organization, Isp, Domain, NetworkRole })
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (parts.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                parts.Add(value);
            }

            return parts.Count == 0 ? "--" : string.Join(" | ", parts);
        }
    }

    public string Summary
        => $"{Address}  位置={LocationLabel}  网络归属={NetworkLabel}  纬度={Latitude:F4}  经度={Longitude:F4}";

    public RouteGeoHopResult ToRouteGeoHopResult(int hopNumber)
        => new(
            hopNumber,
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
            Latitude,
            Longitude);
}
