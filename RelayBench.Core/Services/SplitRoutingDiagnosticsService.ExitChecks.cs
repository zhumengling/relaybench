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
    private static async Task<SplitRoutingExitCheck> RunTraceExitCheckAsync(
        string name,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await HttpClient.GetAsync(endpoint, cancellationToken);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            var values = TraceDocumentParser.Parse(raw);
            values.TryGetValue("ip", out var publicIp);
            values.TryGetValue("loc", out var locationCode);
            values.TryGetValue("colo", out var colo);

            var summary =
                $"{name}：IP {publicIp ?? "--"}，loc {locationCode ?? "--"}，colo {colo ?? "--"}，" +
                $"延迟 {stopwatch.Elapsed.TotalMilliseconds:F0} ms。";

            return new SplitRoutingExitCheck(
                name,
                endpoint,
                true,
                publicIp,
                locationCode,
                colo,
                null,
                null,
                stopwatch.Elapsed,
                summary,
                null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SplitRoutingExitCheck(
                name,
                endpoint,
                false,
                null,
                null,
                null,
                null,
                null,
                stopwatch.Elapsed,
                $"{name} 检查失败。",
                ex.Message);
        }
    }

    private static async Task<SplitRoutingExitCheck> RunSpeedExitCheckAsync(CancellationToken cancellationToken)
    {
        const string endpoint = "https://speed.cloudflare.com/__down?bytes=0";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await HttpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            stopwatch.Stop();

            var publicIp = TryGetHeader(response, "cf-meta-ip");
            var colo = TryGetHeader(response, "cf-meta-colo") ?? TryGetHeader(response, "colo");
            var country = TryGetHeader(response, "cf-meta-country") ?? TryGetHeader(response, "country");
            var city = TryGetHeader(response, "cf-meta-city") ?? TryGetHeader(response, "city");
            var summary =
                $"Cloudflare 速度端点：IP {publicIp ?? "--"}，colo {colo ?? "--"}，{city ?? "--"} / {country ?? "--"}，" +
                $"延迟 {stopwatch.Elapsed.TotalMilliseconds:F0} ms。";

            return new SplitRoutingExitCheck(
                "Cloudflare Speed 端点",
                endpoint,
                true,
                publicIp,
                null,
                colo,
                country,
                city,
                stopwatch.Elapsed,
                summary,
                null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SplitRoutingExitCheck(
                "Cloudflare Speed 端点",
                endpoint,
                false,
                null,
                null,
                null,
                null,
                null,
                stopwatch.Elapsed,
                "Cloudflare 速度端点检查失败。",
                ex.Message);
        }
    }

    private static async Task<SplitRoutingReachabilityCheck> RunReachabilityCheckAsync(string host, CancellationToken cancellationToken)
    {
        var url = $"https://{host}/";
        var resolvedAddress = string.Empty;

        try
        {
            var systemResolution = await ResolveSystemAsync(host);
            resolvedAddress = systemResolution.Addresses.FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            // Ignore resolution failures here and let HTTP capture the final result.
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;
            var summary =
                $"{host}：HTTP {statusCode}，延迟 {stopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
                $"解析结果 {(!string.IsNullOrWhiteSpace(resolvedAddress) ? resolvedAddress : "--")}。";

            return new SplitRoutingReachabilityCheck(
                host,
                url,
                true,
                statusCode,
                string.IsNullOrWhiteSpace(resolvedAddress) ? null : resolvedAddress,
                stopwatch.Elapsed,
                summary,
                null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SplitRoutingReachabilityCheck(
                host,
                url,
                false,
                null,
                string.IsNullOrWhiteSpace(resolvedAddress) ? null : resolvedAddress,
                stopwatch.Elapsed,
                $"{host}：HTTPS 请求失败。",
                ex.Message);
        }
    }
}
