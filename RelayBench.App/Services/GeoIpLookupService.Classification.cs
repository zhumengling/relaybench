using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

public sealed partial class GeoIpLookupService
{
    private static string? InferNetworkRole(string? organization, string? isp, string? domain)
    {
        var normalized = string.Join(
            " ",
            new[] { organization, isp, domain }
                .Where(static value => !string.IsNullOrWhiteSpace(value)))
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (ContainsAny(normalized, "cloudflare", "akamai", "fastly", "edge", "cdn", "cache", "bunny", "cloudfront"))
        {
            return "CDN / 边缘";
        }

        if (ContainsAny(normalized, "amazon", "aws", "google", "microsoft", "azure", "oracle", "digitalocean", "linode", "vultr", "aliyun", "tencent"))
        {
            return "云服务 / 托管";
        }

        if (ContainsAny(normalized, "telecom", "unicom", "mobile", "broadband", "fiber", "internet", "communications"))
        {
            return "运营商 / 出口";
        }

        return "网络";
    }

    private static bool ContainsAny(string value, params string[] tokens)
        => tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
}
