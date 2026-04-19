using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static async Task<IReadOnlyList<string>> ResolveAddressesAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(baseUri.DnsSafeHost, cancellationToken);
            return addresses
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static EdgeObservation BuildEdgeObservation(
        Uri baseUri,
        IReadOnlyList<ProxyProbeScenarioResult> scenarioResults,
        IReadOnlyList<string> resolvedAddresses)
    {
        var headerValues = scenarioResults
            .SelectMany(result => result.ResponseHeaders ?? Array.Empty<string>())
            .ToArray();
        var provider = GuessCdnProvider(headerValues);
        var cfColos = GetHeaderValues(headerValues, "cf-ray")
            .Select(ParseCfColo)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var serverHints = GetHeaderValues(headerValues, "server")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        var viaHints = GetHeaderValues(headerValues, "via")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

        List<string> signatureParts = [];
        if (!string.IsNullOrWhiteSpace(provider))
        {
            signatureParts.Add(provider!);
        }

        if (cfColos.Length > 0)
        {
            signatureParts.Add($"colo {string.Join("/", cfColos)}");
        }

        if (serverHints.Length > 0)
        {
            signatureParts.Add($"server {string.Join(" / ", serverHints)}");
        }
        else if (viaHints.Length > 0)
        {
            signatureParts.Add($"via {string.Join(" / ", viaHints)}");
        }

        if (resolvedAddresses.Count > 0)
        {
            signatureParts.Add($"解析 IP {resolvedAddresses.Count} 个");
        }

        var edgeSignature = signatureParts.Count == 0
            ? $"主机 {baseUri.DnsSafeHost}"
            : string.Join(" | ", signatureParts);
        var cdnSummary = resolvedAddresses.Count == 0 && string.IsNullOrWhiteSpace(provider)
            ? "未识别到明显 CDN 或边缘节点特征。"
            : $"疑似边缘特征：{edgeSignature}。";

        return new EdgeObservation(provider, edgeSignature, cdnSummary);
    }

    private static string BuildCdnStabilitySummary(
        IReadOnlyList<ProxyDiagnosticsResult> rounds,
        int distinctResolvedAddressCount,
        int distinctEdgeSignatureCount,
        int edgeSwitchCount)
    {
        if (rounds.Count == 0)
        {
            return "暂无 CDN / 边缘样本。";
        }

        var providers = rounds
            .Select(round => round.CdnProvider)
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (providers.Length == 0 && distinctResolvedAddressCount == 0 && distinctEdgeSignatureCount == 0)
        {
            return "未识别到明显 CDN 包裹或边缘切换迹象。";
        }

        var providerText = providers.Length == 0 ? "未识别供应商" : string.Join(" / ", providers);
        return $"供应商 {providerText}，解析 IP {distinctResolvedAddressCount} 个，边缘签名 {distinctEdgeSignatureCount} 个，切换 {edgeSwitchCount} 次。";
    }

    private static IReadOnlyList<string> GetHeaderValues(IEnumerable<string> headers, string headerName)
        => headers
            .Where(line => line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
            .Select(line =>
            {
                var separatorIndex = line.IndexOf(':');
                return separatorIndex < 0 ? string.Empty : line[(separatorIndex + 1)..].Trim();
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

    private static string? ParseCfColo(string value)
    {
        var trimmed = value.Trim();
        var lastDashIndex = trimmed.LastIndexOf('-');
        if (lastDashIndex < 0 || lastDashIndex >= trimmed.Length - 1)
        {
            return null;
        }

        return trimmed[(lastDashIndex + 1)..].Trim();
    }

    private static string? GuessCdnProvider(IEnumerable<string> headers)
    {
        var joined = string.Join("\n", headers);
        if (joined.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("cf-ray", StringComparison.OrdinalIgnoreCase))
        {
            return "Cloudflare";
        }

        if (joined.Contains("fastly", StringComparison.OrdinalIgnoreCase))
        {
            return "Fastly";
        }

        if (joined.Contains("akamai", StringComparison.OrdinalIgnoreCase))
        {
            return "Akamai";
        }

        if (joined.Contains("edgeone", StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("tencent", StringComparison.OrdinalIgnoreCase))
        {
            return "Tencent EdgeOne";
        }

        if (joined.Contains("aliyun", StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("alibaba", StringComparison.OrdinalIgnoreCase))
        {
            return "Alibaba Cloud CDN";
        }

        return null;
    }

}
