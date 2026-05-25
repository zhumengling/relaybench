using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class PortScanDiagnosticsService
{
    private static async Task<AddressResolutionInfo> ResolveTargetAddressesAsync(string target, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out var parsedAddress))
        {
            var addressText = parsedAddress.ToString();
            return new AddressResolutionInfo(
                [parsedAddress],
                [addressText],
                "literal-ip",
                $"目标本身就是 IP 地址 {addressText}。");
        }

        IReadOnlyList<IPAddress> systemAddresses = Array.Empty<IPAddress>();
        Exception? systemException = null;
        try
        {
            systemAddresses = await ResolveSystemTargetAddressesAsync(target);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            systemException = ex;
        }

        var systemAddressTexts = systemAddresses.Select(static address => address.ToString()).ToArray();
        if (!IsLikelyDnsName(target))
        {
            if (systemException is not null)
            {
                throw new InvalidOperationException($"系统 DNS 解析失败：{systemException.Message}", systemException);
            }

            return new AddressResolutionInfo(
                systemAddresses,
                systemAddressTexts,
                "system-dns",
                systemAddresses.Count == 0
                    ? "系统 DNS 未返回可用地址。"
                    : $"使用系统 DNS 解析结果：{DescribeAddresses(systemAddressTexts)}。");
        }

        var routableSystemAddresses = FilterPublicRoutableAddresses(systemAddresses);
        if (routableSystemAddresses.Count > 0)
        {
            return new AddressResolutionInfo(
                routableSystemAddresses,
                systemAddressTexts,
                systemAddresses.Count == routableSystemAddresses.Count ? "system-dns" : "system-dns-filtered",
                systemAddresses.Count == routableSystemAddresses.Count
                    ? $"使用系统 DNS 解析结果：{DescribeIpAddresses(routableSystemAddresses)}。"
                    : $"系统 DNS 同时返回了公网与保留地址，已自动过滤并保留公网可路由地址：{DescribeIpAddresses(routableSystemAddresses)}。");
        }

        if (IsKnownLocalOnlyHost(target) && systemAddresses.Count > 0)
        {
            return new AddressResolutionInfo(
                systemAddresses,
                systemAddressTexts,
                "system-dns-fallback",
                $"目标看起来是局域网 / 本地域名，系统 DNS 返回 {DescribeAddresses(systemAddressTexts)}；将继续使用系统解析结果。");
        }

        var publicDnsLookupHost = NormalizeDnsHostForNetworkApis(target);
        var publicDnsAddresses = await ResolvePublicDnsAddressesAsync(publicDnsLookupHost, cancellationToken);
        var routablePublicDnsAddresses = FilterPublicRoutableAddresses(publicDnsAddresses);
        if (routablePublicDnsAddresses.Count > 0)
        {
            var systemAddressText = systemAddressTexts.Length == 0
                ? systemException is null
                    ? "系统 DNS 未返回地址"
                    : $"系统 DNS 解析失败：{systemException.Message}"
                : $"系统 DNS 返回 {DescribeAddresses(systemAddressTexts)}";
            var reason = systemAddressTexts.Length > 0 && ContainsSyntheticBenchmarkAddress(systemAddresses)
                ? "系统 DNS 返回了保留的 Fake-IP / Benchmark 地址"
                : "系统 DNS 没有返回可公网路由地址";

            return new AddressResolutionInfo(
                routablePublicDnsAddresses,
                systemAddressTexts,
                "public-doh",
                $"{reason}（{systemAddressText}），已自动回退公共 DoH：{DescribeIpAddresses(routablePublicDnsAddresses)}。");
        }

        if (systemAddresses.Count > 0 && ContainsSyntheticBenchmarkAddress(systemAddresses))
        {
            var lookupNote = string.Equals(publicDnsLookupHost, target, StringComparison.OrdinalIgnoreCase)
                ? "公共 DoH 未返回任何 A / AAAA 记录"
                : $"公共 DoH（查询 {publicDnsLookupHost}）未返回任何 A / AAAA 记录";

            return new AddressResolutionInfo(
                Array.Empty<IPAddress>(),
                systemAddressTexts,
                "blocked-fake-ip",
                $"系统 DNS 返回了保留的 Fake-IP / Benchmark 地址 {DescribeAddresses(systemAddressTexts)}，且{lookupNote}；为避免误扫 198.18/15 假地址，已停止本次端口扫描。");
        }

        if (systemAddresses.Count > 0)
        {
            return new AddressResolutionInfo(
                systemAddresses,
                systemAddressTexts,
                "system-dns-fallback",
                $"系统 DNS 仅返回不可公网路由地址 {DescribeAddresses(systemAddressTexts)}，且公共 DoH 也未拿到可用结果；已继续使用系统解析结果，结果可能受本地 DNS 或 Fake-IP 影响。");
        }

        if (systemException is not null)
        {
            throw new InvalidOperationException($"系统 DNS 解析失败：{systemException.Message}；公共 DoH 也未返回可用地址。", systemException);
        }

        return new AddressResolutionInfo(
            Array.Empty<IPAddress>(),
            Array.Empty<string>(),
            "unresolved",
            "系统 DNS 与公共 DoH 都未返回可用的 IPv4 / IPv6 地址。");
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveSystemTargetAddressesAsync(string target)
        => NormalizeIpAddresses(await Dns.GetHostAddressesAsync(target));

    private static async Task<IReadOnlyList<IPAddress>> ResolvePublicDnsAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var resolverTasks = new[]
        {
            ResolveGoogleDnsAddressesAsync(host, cancellationToken),
            ResolveCloudflareDnsAddressesAsync(host, cancellationToken)
        };

        await Task.WhenAll(resolverTasks);

        List<IPAddress> addresses = [];
        foreach (var resolverTask in resolverTasks)
        {
            addresses.AddRange(resolverTask.Result);
        }

        return NormalizeIpAddresses(addresses);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveGoogleDnsAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var lookupTasks = new[]
        {
            QueryDnsJsonAsync($"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A", acceptsDnsJson: false, cancellationToken),
            QueryDnsJsonAsync($"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=AAAA", acceptsDnsJson: false, cancellationToken)
        };

        await Task.WhenAll(lookupTasks);

        List<IPAddress> addresses = [];
        foreach (var lookupTask in lookupTasks)
        {
            addresses.AddRange(lookupTask.Result);
        }

        return NormalizeIpAddresses(addresses);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveCloudflareDnsAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var lookupTasks = new[]
        {
            QueryDnsJsonAsync($"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A", acceptsDnsJson: true, cancellationToken),
            QueryDnsJsonAsync($"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=AAAA", acceptsDnsJson: true, cancellationToken)
        };

        await Task.WhenAll(lookupTasks);

        List<IPAddress> addresses = [];
        foreach (var lookupTask in lookupTasks)
        {
            addresses.AddRange(lookupTask.Result);
        }

        return NormalizeIpAddresses(addresses);
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryDnsJsonAsync(string url, bool acceptsDnsJson, CancellationToken cancellationToken)
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
            return Array.Empty<IPAddress>();
        }
    }

    private static IReadOnlyList<IPAddress> ParseDnsJsonAddresses(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<IPAddress>();
            }

            return NormalizeIpAddresses(
                answers.EnumerateArray()
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
                        if (recordType is not 1 and not 28 || string.IsNullOrWhiteSpace(value))
                        {
                            return null;
                        }

                        return IPAddress.TryParse(value, out var address) ? address : null;
                    })
                    .Where(static address => address is not null)
                    .Cast<IPAddress>());
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static IReadOnlyList<IPAddress> NormalizeIpAddresses(IEnumerable<IPAddress> addresses)
        => addresses
            .Where(static address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .DistinctBy(static address => address.ToString())
            .OrderBy(static address => address.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0)
            .ThenBy(static address => address.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<IPAddress> FilterPublicRoutableAddresses(IEnumerable<IPAddress> addresses)
        => NormalizeIpAddresses(addresses.Where(IsPublicRoutableAddress));

    private static bool ContainsSyntheticBenchmarkAddress(IEnumerable<IPAddress> addresses)
        => addresses.Any(IsSyntheticBenchmarkAddress);

    internal static bool IsPublicRoutableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIpv4Address(address),
            AddressFamily.InterNetworkV6 => IsPublicIpv6Address(address),
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
            (bytes[0] == 198 && bytes[1] is 18 or 19) ||
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
        => address.AddressFamily == AddressFamily.InterNetwork &&
           address.GetAddressBytes() is [198, 18 or 19, ..];

    internal static string NormalizeDnsHostForNetworkApis(string target)
    {
        var normalizedTarget = target.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalizedTarget) ||
            IPAddress.TryParse(normalizedTarget, out _) ||
            !IsLikelyDnsName(normalizedTarget))
        {
            return normalizedTarget;
        }

        if (normalizedTarget.All(static ch => ch <= 0x7F))
        {
            return normalizedTarget;
        }

        try
        {
            return new System.Globalization.IdnMapping().GetAscii(normalizedTarget);
        }
        catch (ArgumentException)
        {
            return normalizedTarget;
        }
    }

    private static bool IsKnownLocalOnlyHost(string target)
    {
        var normalizedTarget = target.Trim().TrimEnd('.');
        return normalizedTarget.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".home", StringComparison.OrdinalIgnoreCase) ||
               normalizedTarget.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeIpAddresses(IReadOnlyList<IPAddress> addresses)
        => DescribeAddresses(addresses.Select(static address => address.ToString()).ToArray());

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
}
