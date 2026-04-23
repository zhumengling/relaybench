using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using RelayBench.Core.Models;
using RelayBench.Core.Support;

namespace RelayBench.Core.Services;

public sealed partial class RouteDiagnosticsService
{
    private static async Task<RouteHopResult> SampleHopAsync(RouteHopResult hop, int samplesPerHop, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        List<long> replies = new(samplesPerHop);

        using Ping ping = new();
        for (var index = 0; index < samplesPerHop; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(hop.Address!, timeoutMilliseconds);
                if (reply.Status == IPStatus.Success)
                {
                    replies.Add(reply.RoundtripTime);
                }
            }
            catch
            {
                // Keep the hop sample partial and let loss reporting reflect the failure.
            }
        }

        var receivedResponses = replies.Count;
        return hop with
        {
            SentProbes = samplesPerHop,
            ReceivedResponses = receivedResponses,
            LossPercent = samplesPerHop == 0 ? null : (samplesPerHop - receivedResponses) * 100d / samplesPerHop,
            BestRoundTripTime = receivedResponses == 0 ? null : replies.Min(),
            AverageRoundTripTime = receivedResponses == 0 ? null : replies.Average(),
            WorstRoundTripTime = receivedResponses == 0 ? null : replies.Max()
        };
    }

    private static List<RouteHopResult> ParseTraceHops(string standardOutput)
    {
        List<RouteHopResult> hops = [];
        foreach (var rawLine in standardOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = HopLineRegex.Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            var hopNumber = int.Parse(match.Groups[1].Value);
            var traceSamples = new[]
            {
                ParseTraceLatency(match.Groups[2].Value),
                ParseTraceLatency(match.Groups[3].Value),
                ParseTraceLatency(match.Groups[4].Value)
            };
            var remainder = match.Groups[5].Value.Trim();
            var address = ExtractAddress(remainder);

            hops.Add(new RouteHopResult(
                hopNumber,
                address,
                traceSamples,
                address is null,
                0,
                0,
                null,
                null,
                null,
                null,
                rawLine.TrimEnd()));
        }

        return hops;
    }

    private static long? ParseTraceLatency(string token)
    {
        var value = token.Trim();
        if (value == "*")
        {
            return null;
        }

        value = value.Replace("ms", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<", string.Empty, StringComparison.Ordinal)
            .Trim();

        return long.TryParse(value, out var milliseconds) ? milliseconds : null;
    }

    private static string? ExtractAddress(string remainder)
    {
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return null;
        }

        foreach (var token in remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            var candidate = token.Trim('[', ']');
            if (IPAddress.TryParse(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> ResolveAddressesAsync(string target)
    {
        try
        {
            return (await Dns.GetHostAddressesAsync(target))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
