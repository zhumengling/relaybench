using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using RelayBench.Core.Models;
using RelayBench.Core.Support;

namespace RelayBench.Core.Services;

public sealed partial class SplitRoutingDiagnosticsService
{
    private static async Task<SplitRoutingDnsView> RunDnsViewAsync(string host, CancellationToken cancellationToken)
    {
        var systemTask = ResolveSystemAsync(host);
        var cloudflareTask = ResolveDohAsync(
            $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A",
            acceptsDnsJson: true,
            cancellationToken);
        var googleTask = ResolveDohAsync(
            $"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A",
            acceptsDnsJson: false,
            cancellationToken);

        await Task.WhenAll(systemTask, cloudflareTask, googleTask);

        var system = await systemTask;
        var cloudflare = await cloudflareTask;
        var google = await googleTask;

        var comparisonSummary = BuildDnsComparisonSummary(host, system.Addresses, cloudflare.Addresses, google.Addresses);
        var errors = new[] { system.Error, cloudflare.Error, google.Error }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return new SplitRoutingDnsView(
            host,
            system.Addresses,
            cloudflare.Addresses,
            google.Addresses,
            system.Latency,
            cloudflare.Latency,
            google.Latency,
            comparisonSummary,
            errors.Length == 0 ? null : string.Join(" | ", errors));
    }

    internal static IReadOnlyList<string> NormalizeHosts(IEnumerable<string>? hosts)
    {
        var values = (hosts ?? DefaultHosts)
            .SelectMany(host => host.Split(['\r', '\n', ',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(host => host.Trim().Trim('/'))
            .Where(static host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(host).Host
                : host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        return values.Length == 0 ? DefaultHosts : values;
    }

    private static async Task<(IReadOnlyList<string> Addresses, TimeSpan? Latency, string? Error)> ResolveSystemAsync(string host)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            stopwatch.Stop();
            return (
                addresses
                    .Select(address => address.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static value => value)
                    .ToArray(),
                stopwatch.Elapsed,
                null);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            stopwatch.Stop();
            return (Array.Empty<string>(), stopwatch.Elapsed, ex.Message);
        }
    }

    private static async Task<(IReadOnlyList<string> Addresses, TimeSpan? Latency, string? Error)> ResolveDohAsync(
        string url,
        bool acceptsDnsJson,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (acceptsDnsJson)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
            }

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            return (ParseDohAddresses(json), stopwatch.Elapsed, null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return (Array.Empty<string>(), stopwatch.Elapsed, ex.Message);
        }
    }

    internal static IReadOnlyList<string> ParseDohAddresses(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return answers.EnumerateArray()
            .Select(answer =>
            {
                if (!answer.TryGetProperty("type", out var typeElement) ||
                    !answer.TryGetProperty("data", out var dataElement) ||
                    dataElement.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var type = typeElement.ValueKind == JsonValueKind.Number ? typeElement.GetInt32() : -1;
                var value = dataElement.GetString();
                return type is 1 or 28 &&
                       !string.IsNullOrWhiteSpace(value) &&
                       IPAddress.TryParse(value, out _)
                    ? value
                    : null;
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value)
            .Cast<string>()
            .ToArray();
    }

    internal static string BuildDnsComparisonSummary(
        string host,
        IReadOnlyList<string> systemAddresses,
        IReadOnlyList<string> cloudflareAddresses,
        IReadOnlyList<string> googleAddresses)
    {
        var systemVsCloudflare = CompareAddressSets(systemAddresses, cloudflareAddresses);
        var systemVsGoogle = CompareAddressSets(systemAddresses, googleAddresses);

        return
            $"{host}：系统 DNS 与 Cloudflare DoH 解析结果 {systemVsCloudflare}；" +
            $"系统 DNS 与 Google DoH 解析结果 {systemVsGoogle}。";
    }

    internal static bool IndicatesDnsSplit(SplitRoutingDnsView view)
        => !SetEquals(view.SystemAddresses, view.CloudflareAddresses) ||
           !SetEquals(view.SystemAddresses, view.GoogleAddresses);

    private static string CompareAddressSets(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (first.Count == 0 && second.Count == 0)
        {
            return "两侧都为空";
        }

        if (SetEquals(first, second))
        {
            return "一致";
        }

        if (first.Count == 0 || second.Count == 0)
        {
            return "其中一侧为空";
        }

        return "不一致";
    }

    private static bool SetEquals(IReadOnlyList<string> first, IReadOnlyList<string> second)
        => first.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(second.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
}
