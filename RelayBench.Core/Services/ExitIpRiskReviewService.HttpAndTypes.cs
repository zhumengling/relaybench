using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ExitIpRiskReviewService
{
    private static async Task<JsonDocument> GetJsonDocumentAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string BuildIpInfoUrl(string ip)
    {
        var baseUrl = $"https://ipinfo.io/{Uri.EscapeDataString(ip)}/json";
        var token = GetIpInfoToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return baseUrl;
        }

        return $"{baseUrl}?token={Uri.EscapeDataString(token)}";
    }

    private static string? GetIpInfoToken()
        => FirstNonEmpty(
            Environment.GetEnvironmentVariable(IpInfoTokenEnvironmentVariable),
            Environment.GetEnvironmentVariable(RelayBenchIpInfoTokenEnvironmentVariable));

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.15 (Windows desktop diagnostics)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private sealed record ResolvedOrigin(
        string? PublicIp,
        string DetectSource,
        string? Country,
        string? City,
        string? Asn,
        string? Organization,
        string? CloudflareColo,
        ExitIpRiskSourceResult? SourceResult,
        string? Error = null);
}
