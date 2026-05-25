namespace RelayBench.Core.Models;

public sealed record GeoIpResult(
    string Country,
    string City,
    string Asn,
    string Organization,
    double Latitude,
    double Longitude);
