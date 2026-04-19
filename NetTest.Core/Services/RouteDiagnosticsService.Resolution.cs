using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NetTest.Core.Services;

public sealed partial class RouteDiagnosticsService
{
    private static readonly HttpClient PublicDnsHttpClient = CreatePublicDnsHttpClient();

    private static async Task<RouteTracePlan> ResolveTracePlanAsync(string target, string resolverMode, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out var targetAddress))
        {
            var addressText = targetAddress.ToString();
            var summary = IsPublicRoutableAddress(targetAddress)
                ? $"目标本身就是可公网路由 IP，将直接使用 {addressText} 执行探测。"
                : $"目标本身是 IP 地址 {addressText}，但它不属于公网可路由地址；仍将直接执行探测，结果可能受本地网络、代理或 Fake-IP 影响。";

            return new RouteTracePlan(addressText, [addressText], [addressText], summary);
        }

        var systemResolvedAddresses = await ResolveAddressesAsync(target);
        var normalizedResolverMode = NormalizeResolverMode(resolverMode);
        if (string.Equals(normalizedResolverMode, "system", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSystemOnlyTracePlan(target, systemResolvedAddresses);
        }

        var preferredTracePlan = await TryBuildPreferredResolverTracePlanAsync(target, systemResolvedAddresses, normalizedResolverMode, cancellationToken);
        if (preferredTracePlan is not null)
        {
            return preferredTracePlan;
        }

        var fallbackTarget = systemResolvedAddresses.FirstOrDefault() ?? target;
        var fallbackAddresses = systemResolvedAddresses.Count == 0 ? Array.Empty<string>() : systemResolvedAddresses;
        var fallbackSummary = systemResolvedAddresses.Count == 0
            ? "系统 DNS 与公共 DNS 都没有返回可用地址，将回退为直接对原始目标执行探测；结果可能受本地 DNS、代理或 Fake-IP 影响。"
            : $"系统解析得到 {DescribeAddresses(systemResolvedAddresses)}，但仍无法确认公网可路由地址，将回退为直接对 {fallbackTarget} 执行探测；结果可能受本地 DNS、代理或 Fake-IP 影响。";

        return new RouteTracePlan(
            fallbackTarget,
            fallbackAddresses,
            systemResolvedAddresses,
            fallbackSummary);
    }

    private static async Task<RouteTracePlan?> TryBuildPreferredResolverTracePlanAsync(
        string target,
        IReadOnlyList<string> systemResolvedAddresses,
        string resolverMode,
        CancellationToken cancellationToken)
    {
        if (string.Equals(resolverMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var routableSystemAddresses = FilterRoutableAddresses(systemResolvedAddresses);
            if (routableSystemAddresses.Count > 0)
            {
                var summary = systemResolvedAddresses.Count == routableSystemAddresses.Count
                    ? $"系统解析得到 {DescribeAddresses(routableSystemAddresses)}，将使用 {routableSystemAddresses[0]} 执行探测。"
                    : $"系统解析同时返回了公网与保留地址，已过滤后使用 {DescribeAddresses(routableSystemAddresses)} 执行探测。";

                return new RouteTracePlan(
                    routableSystemAddresses[0],
                    routableSystemAddresses,
                    systemResolvedAddresses,
                    summary);
            }
        }

        var preferredAddresses = await ResolveAddressesWithResolverModeAsync(target, resolverMode, cancellationToken);
        var routablePreferredAddresses = FilterRoutableAddresses(preferredAddresses);
        if (routablePreferredAddresses.Count > 0)
        {
            var summary = resolverMode switch
            {
                "google-doh" => $"已按 Google DoH 解析到 {DescribeAddresses(routablePreferredAddresses)}，将使用 {routablePreferredAddresses[0]} 执行探测。",
                "cloudflare-doh" => $"已按 Cloudflare DoH 解析到 {DescribeAddresses(routablePreferredAddresses)}，将使用 {routablePreferredAddresses[0]} 执行探测。",
                _ => BuildAutoResolverSummary(systemResolvedAddresses, routablePreferredAddresses)
            };

            return new RouteTracePlan(
                routablePreferredAddresses[0],
                routablePreferredAddresses,
                systemResolvedAddresses,
                summary);
        }

        return null;
    }

    private static RouteTracePlan BuildSystemOnlyTracePlan(string target, IReadOnlyList<string> systemResolvedAddresses)
    {
        var routableSystemAddresses = FilterRoutableAddresses(systemResolvedAddresses);
        if (routableSystemAddresses.Count > 0)
        {
            var matchedSummary = systemResolvedAddresses.Count == routableSystemAddresses.Count
                ? $"已按“系统 DNS”模式解析到 {DescribeAddresses(routableSystemAddresses)}，将使用 {routableSystemAddresses[0]} 执行探测。"
                : $"已按“系统 DNS”模式过滤系统解析结果，并使用 {DescribeAddresses(routableSystemAddresses)} 执行探测。";

            return new RouteTracePlan(
                routableSystemAddresses[0],
                routableSystemAddresses,
                systemResolvedAddresses,
                matchedSummary);
        }

        var fallbackTarget = systemResolvedAddresses.FirstOrDefault() ?? target;
        var fallbackSummary = systemResolvedAddresses.Count == 0
            ? "已按“系统 DNS”模式执行，但系统解析没有返回任何地址，将直接对原始目标继续探测。"
            : $"已按“系统 DNS”模式执行，但系统解析得到 {DescribeAddresses(systemResolvedAddresses)}，其中没有可公网路由地址；将继续对 {fallbackTarget} 探测，结果可能受本地 DNS 或 Fake-IP 影响。";

        return new RouteTracePlan(
            fallbackTarget,
            systemResolvedAddresses,
            systemResolvedAddresses,
            fallbackSummary);
    }

    private static string BuildAutoResolverSummary(
        IReadOnlyList<string> systemResolvedAddresses,
        IReadOnlyList<string> routablePreferredAddresses)
    {
        var systemAddressText = systemResolvedAddresses.Count == 0
            ? "系统未返回地址"
            : DescribeAddresses(systemResolvedAddresses);
        var reason = ContainsSyntheticBenchmarkAddress(systemResolvedAddresses)
            ? "系统解析命中了保留的 Fake-IP / Benchmark 地址"
            : "系统解析没有拿到可公网路由地址";

        return $"{reason}（{systemAddressText}），已改用公共 DNS 解析到 {DescribeAddresses(routablePreferredAddresses)}，并选择 {routablePreferredAddresses[0]} 继续探测。";
    }

    private static Task<IReadOnlyList<string>> ResolveAddressesWithResolverModeAsync(string host, string resolverMode, CancellationToken cancellationToken)
        => resolverMode switch
        {
            "google-doh" => ResolveGoogleDnsAddressesAsync(host, cancellationToken),
            "cloudflare-doh" => ResolveCloudflareDnsAddressesAsync(host, cancellationToken),
            _ => ResolvePublicDnsAddressesAsync(host, cancellationToken)
        };

    private static async Task<IReadOnlyList<string>> ResolvePublicDnsAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var resolverTasks = new[]
        {
            ResolveGoogleDnsAddressesAsync(host, cancellationToken),
            ResolveCloudflareDnsAddressesAsync(host, cancellationToken)
        };

        await Task.WhenAll(resolverTasks);

        List<string> addresses = [];
        foreach (var resolverTask in resolverTasks)
        {
            addresses.AddRange(resolverTask.Result);
        }

        return addresses
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> ResolveGoogleDnsAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var lookupTasks = new[]
        {
            QueryDnsJsonAsync($"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A", acceptsDnsJson: false, cancellationToken),
            QueryDnsJsonAsync($"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=AAAA", acceptsDnsJson: false, cancellationToken)
        };

        await Task.WhenAll(lookupTasks);

        List<string> addresses = [];
        foreach (var lookupTask in lookupTasks)
        {
            addresses.AddRange(lookupTask.Result);
        }

        return addresses
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> ResolveCloudflareDnsAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var lookupTasks = new[]
        {
            QueryDnsJsonAsync($"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A", acceptsDnsJson: true, cancellationToken),
            QueryDnsJsonAsync($"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=AAAA", acceptsDnsJson: true, cancellationToken)
        };

        await Task.WhenAll(lookupTasks);

        List<string> addresses = [];
        foreach (var lookupTask in lookupTasks)
        {
            addresses.AddRange(lookupTask.Result);
        }

        return addresses
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> QueryDnsJsonAsync(string url, bool acceptsDnsJson, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (acceptsDnsJson)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
            }

            using var response = await PublicDnsHttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseDnsJsonAddresses(json);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ParseDnsJsonAddresses(string json)
    {
        try
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

                    var recordType = typeElement.ValueKind == JsonValueKind.Number ? typeElement.GetInt32() : -1;
                    var value = dataElement.GetString();
                    return recordType is 1 or 28 && !string.IsNullOrWhiteSpace(value) ? value : null;
                })
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> FilterRoutableAddresses(IEnumerable<string> addresses)
        => addresses
            .Where(static value => IPAddress.TryParse(value, out _))
            .Where(value => IPAddress.TryParse(value, out var address) && IsPublicRoutableAddress(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ContainsSyntheticBenchmarkAddress(IEnumerable<string> addresses)
        => addresses.Any(value => IPAddress.TryParse(value, out var address) && IsSyntheticBenchmarkAddress(address));

    private static bool IsPublicRoutableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => IsPublicIpv4Address(address),
            System.Net.Sockets.AddressFamily.InterNetworkV6 => IsPublicIpv6Address(address),
            _ => false
        };
    }

    private static bool IsPublicIpv4Address(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return !(
            bytes[0] == 10 ||
            bytes[0] == 0 ||
            bytes[0] == 127 ||
            (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
            (bytes[0] == 198 && bytes[1] == 18) ||
            (bytes[0] == 198 && bytes[1] == 19) ||
            (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
            (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
            bytes[0] >= 224);
    }

    private static bool IsPublicIpv6Address(IPAddress address)
    {
        if (address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal ||
            address.Equals(IPAddress.IPv6None) ||
            address.Equals(IPAddress.IPv6Loopback))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return false;
        }

        return !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
    }

    private static bool IsSyntheticBenchmarkAddress(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    private static string DescribeAddresses(IReadOnlyList<string> addresses)
    {
        if (addresses.Count == 0)
        {
            return "无";
        }

        return addresses.Count <= 4
            ? string.Join(", ", addresses)
            : string.Join(", ", addresses.Take(4)) + $" 等 {addresses.Count} 个地址";
    }

    private static HttpClient CreatePublicDnsHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.9 (Windows desktop diagnostics)");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private sealed record RouteTracePlan(
        string TraceTarget,
        IReadOnlyList<string> ResolvedAddresses,
        IReadOnlyList<string> SystemResolvedAddresses,
        string ResolutionSummary);
}
