namespace NetTest.App.Services;

public sealed record RouteGeoHopResult(
    int HopNumber,
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
        => string.Join(
            ", ",
            new[] { City, Region, Country }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

    public string DisplayLabel
    {
        get
        {
            var prefix = HopNumber > 0 ? $"第 {HopNumber} 跳 - " : string.Empty;
            return string.IsNullOrWhiteSpace(LocationLabel)
                ? $"{prefix}{Address}"
                : $"{prefix}{LocationLabel}";
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
        => $"{DisplayLabel} [{Address}]  网络归属={NetworkLabel}  纬度={Latitude:F4}  经度={Longitude:F4}";
}
