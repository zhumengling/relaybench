namespace NetTest.Core.Models;

public sealed record RouteHopResult(
    int HopNumber,
    string? Address,
    IReadOnlyList<long?> TraceRoundTripTimes,
    bool TimedOut,
    int SentProbes,
    int ReceivedResponses,
    double? LossPercent,
    long? BestRoundTripTime,
    double? AverageRoundTripTime,
    long? WorstRoundTripTime,
    string RawLine,
    string? Hostname = null,
    string? AutonomousSystem = null,
    string? Country = null,
    string? Region = null,
    string? City = null,
    string? District = null,
    string? Organization = null,
    double? Latitude = null,
    double? Longitude = null)
{
    public bool HasCoordinates => Latitude.HasValue && Longitude.HasValue;

    public bool HasTraceMetadata =>
        !string.IsNullOrWhiteSpace(Hostname) ||
        !string.IsNullOrWhiteSpace(AutonomousSystem) ||
        !string.IsNullOrWhiteSpace(Country) ||
        !string.IsNullOrWhiteSpace(Region) ||
        !string.IsNullOrWhiteSpace(City) ||
        !string.IsNullOrWhiteSpace(District) ||
        !string.IsNullOrWhiteSpace(Organization) ||
        HasCoordinates;

    public string LocationLabel
    {
        get
        {
            var location = string.Join(
                " / ",
                new[] { Country, Region, City, District }
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));

            return string.IsNullOrWhiteSpace(location) ? "--" : location;
        }
    }

    public string NetworkLabel
    {
        get
        {
            var parts = new List<string>();

            foreach (var value in new[] { AutonomousSystem, Organization, Hostname })
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
}
