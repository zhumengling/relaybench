using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;
using RelayBenchPaths = RelayBench.Services.Infrastructure.RelayBenchPaths;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class NetworkReviewViewModel : ObservableObject
{
    private void NotifyReviewRunningChanged()
    {
        OnPropertyChanged(nameof(IsAnyReviewRunning));
        OnPropertyChanged(nameof(RuntimeStateText));
    }

    private async Task RecordNetworkHistoryAsync(string operation, string endpoint, string summary, double? score, object? payload)
    {
        try
        {
            await RunHistoryRecorder.RecordAsync(
                "Network",
                endpoint,
                $"{operation}: {summary}",
                score,
                null,
                BuildNetworkHistoryPayloadJson(operation, endpoint, summary, payload));
        }
        catch
        {
            // Diagnostics should not fail because the history store is unavailable.
        }
    }

    internal string BuildNetworkHistoryPayloadJson(string operation, string endpoint, string summary, object? payload)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Schema = "network-review-v2",
            Operation = operation,
            Endpoint = endpoint,
            Summary = summary,
            Proxy = new
            {
                Mode = ProxyModeText,
                Runtime = ProxyRuntimeText,
                ListenAddress = ProxyListenAddress,
                Connection = ProxyConnectionText,
                RouteSummary = ProxyRuleCountText,
                ModelPool = ProxyModelPoolText,
                Cache = ProxyCacheHitRateText,
                TokenSpeed = ProxyTokenSpeedText,
                CodexOAuth = ProxyCodexOAuthText,
                Management = ProxyManagementText,
                ProtocolSummary = ProxyProtocolSummaryText,
                RecentError = ProxyRecentErrorText
            },
            RouteMap = new
            {
                HasMap = HasRouteMapImage,
                Summary = RouteMapSummary,
                GeoSummary = RouteMapGeoSummary,
                ImagePath = RouteMapImagePath
            },
            Result = payload
        });

    private static string BuildCodexOAuthSummary(TransparentProxyViewModel proxy)
    {
        var oauthCount = proxy.ProviderAccounts.Count(static item => item.IsOAuthCredential);
        var readyCount = proxy.ProviderAccounts.Count(static item =>
            item.IsOAuthCredential &&
            string.Equals(item.StatusText, "\u53ef\u7528", StringComparison.Ordinal));
        return $"{readyCount}/{oauthCount} Codex OAuth";
    }

    private static string BuildProxyProtocolSummary(TransparentProxyViewModel proxy)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var protocol in proxy.RouteQueue
                     .Select(static item => item.Protocol)
                     .Concat(proxy.ProviderAccounts.Select(static item => item.ProtocolSummary))
                     .SelectMany(SplitProtocolTokens))
        {
            counts[protocol] = counts.TryGetValue(protocol, out var current) ? current + 1 : 1;
        }

        if (counts.Count == 0)
        {
            return "\u672a\u63a2\u6d4b";
        }

        return string.Join(" / ", counts.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static item => $"{item.Key}:{item.Value}"));
    }

    private static IEnumerable<string> SplitProtocolTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var part in value.Split(['/', ',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part) && part != "--")
            {
                yield return part.Trim();
            }
        }
    }

    private static string BuildProxyRecentErrorSummary(TransparentProxyViewModel proxy)
    {
        var routeError = proxy.RouteQueue
            .Select(static item => item.LatestError)
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item));
        if (!string.IsNullOrWhiteSpace(routeError))
        {
            return routeError.Trim();
        }

        var activityError = proxy.RecentActivityEvents
            .FirstOrDefault(static item =>
                string.Equals(item.Level, "WARN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Level, "ERROR", StringComparison.OrdinalIgnoreCase));
        return activityError is null
            ? "0 \u6700\u8fd1\u5f02\u5e38"
            : $"{activityError.BadgeText} · {activityError.Detail}";
    }

    private static int CountTransparentProxyModels(TransparentProxyViewModel proxy)
    {
        HashSet<string> models = new(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in proxy.ModelPool)
        {
            AddModelName(models, pool.Name);
        }

        foreach (var route in proxy.Routes)
        {
            if (!route.Enabled)
            {
                continue;
            }

            foreach (var model in SplitModels(route.ModelFilter))
            {
                AddModelName(models, model);
            }
        }

        return models.Count;
    }

    private static IEnumerable<string> SplitModels(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddModelName(HashSet<string> models, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !value.Equals("*", StringComparison.Ordinal))
        {
            models.Add(value.Trim());
        }
    }

    private void TouchSnapshot(DateTimeOffset? timestamp = null)
    {
        SnapshotTimeText = (timestamp ?? DateTimeOffset.Now).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private void ApplyPrimaryAdapter(NetworkAdapterInfo? adapter)
    {
        if (adapter is null)
        {
            return;
        }

        LocalAdapterName = string.IsNullOrWhiteSpace(adapter.Description) ? adapter.Name : adapter.Description;
        LocalIpAddress = adapter.UnicastAddresses.FirstOrDefault(address => address.Contains('.', StringComparison.Ordinal)) ?? adapter.UnicastAddresses.FirstOrDefault() ?? "--";
        LocalMacAddress = string.IsNullOrWhiteSpace(adapter.MacAddress) ? "--" : adapter.MacAddress;
        LinkSpeedText = adapter.LinkSpeedBitsPerSecond is > 0
            ? $"{adapter.LinkSpeedBitsPerSecond.Value / 1_000_000d:F0} Mbps"
            : "--";
        DefaultGateway = adapter.GatewayAddresses?.FirstOrDefault() ?? "--";
        PreferredDns = adapter.DnsServers.ElementAtOrDefault(0) ?? "--";
        AlternateDns = adapter.DnsServers.ElementAtOrDefault(1) ?? "--";
    }

    private async Task ApplyDnsDiagnosticsAsync(NetworkAdapterInfo? adapter)
    {
        PreferredDnsLatency = await MeasurePingLatencyTextAsync(PreferredDns);
        AlternateDnsLatency = await MeasurePingLatencyTextAsync(AlternateDns);
        DnsLookupHost = "chatgpt.com";
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(DnsLookupHost);
            DnsLookupAddress = BuildAddressSummary(addresses.Select(static address => address.ToString()));
        }
        catch
        {
            DnsLookupAddress = "--";
        }

        DnsLeakStatus = adapter?.DnsServers.Count > 0 ? "已检测到系统 DNS" : "--";
    }

    private static async Task<string> MeasurePingLatencyTextAsync(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target == "--")
        {
            return "--";
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, 1500);
            return reply.Status == IPStatus.Success ? $"{reply.RoundtripTime} ms" : reply.Status.ToString();
        }
        catch
        {
            return "--";
        }
    }

    private void ApplyUnlockCatalogResult(UnlockCatalogResult result)
    {
        UnlockCapabilityRows.Clear();
        foreach (var row in BuildUnlockRows(result))
        {
            UnlockCapabilityRows.Add(row);
        }
    }

    public static IReadOnlyList<NetworkReviewUnlockRow> BuildUnlockRows(UnlockCatalogResult result)
        => result.Checks
            .OrderBy(static check => check.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static check => check.Name, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(BuildUnlockRow)
            .ToArray();

    private static NetworkReviewUnlockRow BuildUnlockRow(UnlockEndpointCheck check)
    {
        var latency = check.Latency.HasValue ? $"{check.Latency.Value.TotalMilliseconds:F0} ms" : "--";
        var summary = string.IsNullOrWhiteSpace(check.SemanticSummary)
            ? check.Summary
            : check.SemanticSummary;
        var status = BuildUnlockAvailabilityText(check);
        var endpointMeta = $"{check.Method} | HTTP {check.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "--"} | {latency}";
        var traceDetail = BuildUnlockTraceDetail(check, latency);

        return new NetworkReviewUnlockRow(
            check.Name,
            status,
            latency,
            check.Provider,
            endpointMeta,
            summary,
            check.SemanticCategory,
            check.SemanticVerdict,
            check.Evidence ?? string.Empty,
            traceDetail,
            check.Url,
            check.Method,
            check.FinalUrl ?? string.Empty,
            check.ResponseContentType ?? string.Empty,
            check.Error ?? string.Empty);
    }

    private static string BuildUnlockAvailabilityText(UnlockEndpointCheck check)
        => check.SemanticCategory switch
        {
            "Ready" => "Ready",
            "AuthRequired" => "Auth required",
            "RegionRestricted" => "Region limited",
            "ReviewRequired" => check.Reachable ? "Review" : "Unreachable",
            "Unreachable" => "Unreachable",
            _ => check.Reachable ? "Review" : "Unreachable"
        };

    private static string BuildUnlockTraceDetail(UnlockEndpointCheck check, string latency)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Name: {check.Name}");
        builder.AppendLine($"Provider: {check.Provider}");
        builder.AppendLine($"URL: {check.Url}");
        builder.AppendLine($"Method: {check.Method}");
        builder.AppendLine($"HTTP status: {check.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "--"}");
        builder.AppendLine($"Reachable: {(check.Reachable ? "yes" : "no")}");
        builder.AppendLine($"Latency: {latency}");
        builder.AppendLine($"Verdict: {check.Verdict}");
        builder.AppendLine($"Semantic category: {check.SemanticCategory}");
        builder.AppendLine($"Semantic verdict: {check.SemanticVerdict}");
        builder.AppendLine();
        builder.AppendLine("Summary:");
        builder.AppendLine(check.Summary);
        builder.AppendLine();
        builder.AppendLine("Semantic summary:");
        builder.AppendLine(check.SemanticSummary);
        builder.AppendLine();
        builder.AppendLine("Evidence:");
        builder.AppendLine(string.IsNullOrWhiteSpace(check.Evidence) ? "none" : check.Evidence);
        builder.AppendLine();
        builder.AppendLine($"Final URL: {check.FinalUrl ?? "none"}");
        builder.AppendLine($"Content type: {check.ResponseContentType ?? "none"}");
        builder.AppendLine($"Error: {check.Error ?? "none"}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildAddressSummary(IEnumerable<string> addresses)
    {
        var values = addresses
            .Select(static address => address?.Trim() ?? string.Empty)
            .Where(static address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        return values.Length == 0 ? "--" : string.Join(", ", values);
    }

    private static string BuildPortScanRawOutput(PortScanResult result)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.CommandLine))
        {
            builder.AppendLine(result.CommandLine);
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            builder.AppendLine(result.StandardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine(result.StandardError.Trim());
        }

        return builder.ToString().Trim();
    }

    private void ApplyRouteSummary(IReadOnlyList<RouteHopResult> hops, bool traceCompleted)
    {
        RouteTotalHops = hops.Count.ToString(CultureInfo.InvariantCulture);
        var finalAverage = hops.LastOrDefault(hop => hop.AverageRoundTripTime.HasValue)?.AverageRoundTripTime;
        RouteAverageLatency = finalAverage.HasValue ? $"{finalAverage.Value:F1} ms" : "--";
        var lossValues = hops.Where(static hop => hop.LossPercent.HasValue).Select(static hop => hop.LossPercent!.Value).ToArray();
        RouteLossRate = lossValues.Length == 0 ? "--" : $"{lossValues.Average():F1}%";
        RouteStatusText = traceCompleted ? "\u6B63\u5E38" : "\u5F85\u590D\u6838";
    }

    private void ApplyRiskSnapshot(ExitIpRiskReviewResult result)
    {
        var score = EstimateRiskScore(result);
        IpRiskScore = score.ToString(CultureInfo.InvariantCulture);
        IpRiskScoreLabel = score <= 30 ? "\u4F4E\u98CE\u9669" : score <= 65 ? "\u4E2D\u98CE\u9669" : "\u9AD8\u98CE\u9669";
        RiskMaliciousBehavior = result.RiskSignals.Count.ToString(CultureInfo.InvariantCulture);
        RiskAbuseComplaint = result.Sources.Any(static source => source.IsAbuse == true) ? "\u9700\u590D\u6838" : "\u4F4E";
        RiskProxyDetected = result.Sources.Any(static source => source.IsProxy == true || source.IsVpn == true || source.IsTor == true)
            ? "\u5DF2\u547D\u4E2D"
            : "\u672A\u68C0\u6D4B\u5230";
        RiskDatacenter = result.Sources.Any(static source => source.IsDatacenter == true) ? "\u662F" : "\u5426";
    }

    private void RebuildRoutePathNodes(IReadOnlyList<RouteHopResult> hops)
    {
        RoutePathNodes.Clear();

        if (hops.Count == 0)
        {
            return;
        }

        foreach (var hop in hops.Take(6))
        {
            var title = hop.HopNumber == 1 ? "\u672C\u673A\u7F51\u5173" : hop.HopNumber == hops.Count ? "\u76EE\u6807" : "\u8DEF\u7531\u8282\u70B9";
            var latency = hop.AverageRoundTripTime.HasValue ? $"{hop.AverageRoundTripTime.Value:F1} ms" : "--";
            RoutePathNodes.Add(new NetworkReviewRouteNode(
                hop.HopNumber.ToString(CultureInfo.InvariantCulture),
                title,
                hop.Address ?? "--",
                hop.NetworkLabel,
                latency));
        }
    }

    private async Task RenderRouteMapAsync(RouteDiagnosticsResult routeResult)
    {
        IsRouteMapRendering = true;
        try
        {
            var progress = new Progress<string>(message => RouteMapSummary = message);
            var result = await _routeMapService.RenderAsync(routeResult, progress);
            RouteMapSummary = result.Summary;
            RouteMapGeoSummary = result.GeoSummary;
            RouteMapImagePath = result.MapImagePath ?? string.Empty;
            HasRouteMapImage = result.HasMap && !string.IsNullOrWhiteSpace(result.MapImagePath);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                RouteTraceError = string.IsNullOrWhiteSpace(RouteTraceError)
                    ? result.Error
                    : $"{RouteTraceError}\n{result.Error}";
            }
        }
        catch (Exception ex)
        {
            RouteMapSummary = $"\u8def\u7531\u5730\u56fe\u751f\u6210\u5931\u8d25\uff1a{ex.Message}";
            RouteMapGeoSummary = string.Empty;
            RouteMapImagePath = string.Empty;
            HasRouteMapImage = false;
        }
        finally
        {
            IsRouteMapRendering = false;
        }
    }

    private void ResetRouteMap(string summary)
    {
        IsRouteMapRendering = false;
        HasRouteMapImage = false;
        RouteMapImagePath = string.Empty;
        RouteMapSummary = summary;
        RouteMapGeoSummary = string.Empty;
    }

    private async Task<IReadOnlyList<RouteHopResult>> EnrichHopsWithGeoIpAsync(IReadOnlyList<RouteHopResult> hops)
    {
        if (hops.Count == 0)
            return hops;

        var enriched = new List<RouteHopResult>(hops.Count);

        foreach (var hop in hops)
        {
            // Skip hops without an IP address or that already have geo data
            if (string.IsNullOrWhiteSpace(hop.Address) || hop.HasTraceMetadata)
            {
                enriched.Add(hop);
                continue;
            }

            try
            {
                var geoResult = await _geoIpService.LookupAsync(hop.Address).ConfigureAwait(false);

                if (geoResult is not null)
                {
                    // Enrich the hop with GeoIP data using 'with' expression
                    enriched.Add(hop with
                    {
                        Country = string.IsNullOrWhiteSpace(geoResult.Country) ? hop.Country : geoResult.Country,
                        City = string.IsNullOrWhiteSpace(geoResult.City) ? hop.City : geoResult.City,
                        AutonomousSystem = string.IsNullOrWhiteSpace(geoResult.Asn) ? hop.AutonomousSystem : geoResult.Asn,
                        Organization = string.IsNullOrWhiteSpace(geoResult.Organization) ? hop.Organization : geoResult.Organization,
                        Latitude = geoResult.Latitude != 0 ? geoResult.Latitude : hop.Latitude,
                        Longitude = geoResult.Longitude != 0 ? geoResult.Longitude : hop.Longitude,
                    });
                }
                else
                {
                    // Graceful degradation: show hop without geo data
                    enriched.Add(hop);
                }
            }
            catch
            {
                // Graceful degradation: if lookup fails, show hop without geo data
                enriched.Add(hop);
            }
        }

        return enriched;
    }

    private void UpdateRecentCheck(string name, string time)
    {
        for (var i = 0; i < RecentCheckRows.Count; i++)
        {
            if (string.Equals(RecentCheckRows[i].Name, name, StringComparison.Ordinal))
            {
                RecentCheckRows[i] = RecentCheckRows[i] with { Time = time };
                return;
            }
        }

        RecentCheckRows.Insert(0, new NetworkReviewRecentCheck(name, time));
        while (RecentCheckRows.Count > 5)
        {
            RecentCheckRows.RemoveAt(RecentCheckRows.Count - 1);
        }
    }

    private string BuildSnapshotMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# RelayBench Network Review");
        builder.AppendLine();
        builder.AppendLine($"Snapshot: {SnapshotTimeText}");
        builder.AppendLine($"Public IP: {PublicIp}");
        builder.AppendLine($"Cloudflare 节点: {CloudflareColo}");
        builder.AppendLine($"DNS: {PreferredDns}, {AlternateDns}");
        builder.AppendLine($"Download: {DownloadSpeed}");
        builder.AppendLine($"Upload: {UploadSpeed}");
        builder.AppendLine($"Jitter: {Jitter}");
        builder.AppendLine($"Route: {RouteTraceSummary}");
        builder.AppendLine($"NAT: {StunNatType}");
        builder.AppendLine($"Risk: {IpRiskScore}/100 {IpRiskScoreLabel}");
        builder.AppendLine($"Route map: {RouteMapSummary}");
        builder.AppendLine();
        builder.AppendLine("## Route Trace");
        builder.AppendLine(RouteTraceRawOutput);
        if (!string.IsNullOrWhiteSpace(RouteMapGeoSummary))
        {
            builder.AppendLine();
            builder.AppendLine("## Route Map Geo Points");
            builder.AppendLine(RouteMapGeoSummary);
        }
        return builder.ToString();
    }

    private static string BuildSpeedLocation(SpeedTestResult result)
    {
        var parts = new[] { result.EdgeCity, result.EdgeCountry }
            .Where(static value => !string.IsNullOrWhiteSpace(value));
        var location = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(location) ? "--" : location;
    }

    private static string BuildCountryCityText(string? country, string? city)
    {
        var text = string.Join(" / ", new[] { country, city }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(text) ? "--" : text;
    }

    private static string BuildRouteTerminalText(IReadOnlyList<RouteHopResult> hops)
    {
        var builder = new StringBuilder();
        builder.AppendLine("traceroute result");
        foreach (var hop in hops)
        {
            var average = hop.AverageRoundTripTime.HasValue ? $"{hop.AverageRoundTripTime.Value:F1} ms" : "--";
            builder.AppendLine($"{hop.HopNumber,2}  {hop.Address ?? "*",-18}  {average}");
        }

        return builder.ToString();
    }

    private static int EstimateRiskScore(ExitIpRiskReviewResult result)
    {
        var scoredSources = result.Sources.Where(static source => source.RiskScore.HasValue).Select(static source => source.RiskScore!.Value).ToArray();
        if (scoredSources.Length > 0)
        {
            return Math.Clamp((int)Math.Round(scoredSources.Average(), MidpointRounding.AwayFromZero), 0, 100);
        }

        var score = result.RiskSignals.Count * 15;
        if (result.Sources.Any(static source => source.IsDatacenter == true))
        {
            score += 15;
        }

        return Math.Clamp(score, 0, 100);
    }
}
