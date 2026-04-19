using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class CloudflareSpeedTestService
{
    private static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new()
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.3 (Windows desktop diagnostics)");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private sealed record SpeedTestProfileConfig(
        string Key,
        string DisplayName,
        string Description,
        int IdleLatencyProbeCount,
        int PacketLossProbeCount,
        IReadOnlyList<long> DownloadBytesPlan,
        IReadOnlyList<long> UploadBytesPlan)
    {
        public SpeedTestProfile ToPublicModel()
            => new(Key, DisplayName, Description);
    }

    private sealed record EdgeMetadata(
        string? Colo,
        string? City,
        string? Country,
        string? Ip)
    {
        public static EdgeMetadata FromResponse(HttpResponseMessage response)
            => new(
                TryGetHeader(response, "cf-meta-colo") ?? TryGetHeader(response, "colo"),
                TryGetHeader(response, "cf-meta-city") ?? TryGetHeader(response, "city"),
                TryGetHeader(response, "cf-meta-country") ?? TryGetHeader(response, "country"),
                TryGetHeader(response, "cf-meta-ip"));

        private static string? TryGetHeader(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                return values.FirstOrDefault();
            }

            if (response.Content.Headers.TryGetValues(name, out values))
            {
                return values.FirstOrDefault();
            }

            return null;
        }
    }

    private sealed record LatencyProbeResult(
        double LatencyMilliseconds,
        EdgeMetadata? Metadata);

    private sealed record LatencySeriesResult(
        IReadOnlyList<double> Points,
        double? ReducedLatency,
        double? Jitter,
        EdgeMetadata? Metadata);

    private sealed record BandwidthSeriesResult(
        IReadOnlyList<SpeedTransferMeasurement> Measurements,
        double? ReducedBandwidth,
        IReadOnlyList<double> LoadedLatencyPoints,
        double? ReducedLoadedLatency,
        double? LoadedJitter,
        EdgeMetadata? Metadata);

    private sealed record TransferMeasurementResult(
        double DurationMilliseconds,
        double BitsPerSecond,
        IReadOnlyList<double> LoadedLatencyPoints,
        EdgeMetadata? Metadata);

    private sealed class GeneratedBytesContent(long length) : HttpContent
    {
        private readonly long _length = length;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[64 * 1024];
            long remaining = _length;

            while (remaining > 0)
            {
                var chunkSize = (int)Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, chunkSize));
                remaining -= chunkSize;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }

}
