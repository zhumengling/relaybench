using System.Net;
using System.Net.Http;
using System.Text;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

internal static class TransparentProxyUpstreamRequestFactory
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailers",
        "transfer-encoding",
        "upgrade",
        "host",
        "content-length"
    };

    public static HttpRequestMessage Create(
        HttpListenerRequest source,
        string method,
        string upstreamUrl,
        TransparentProxyRoute route,
        byte[] body,
        string wireApi,
        IReadOnlyDictionary<string, string> extraHeaders)
    {
        HttpRequestMessage request = new(new HttpMethod(method), upstreamUrl);
        foreach (var headerName in source.Headers.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(headerName) || HopByHopHeaders.Contains(headerName))
            {
                continue;
            }

            var values = source.Headers.GetValues(headerName);
            if (values is null)
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(headerName, values))
            {
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.TryAddWithoutValidation(headerName, values);
            }
        }

        var effectiveApiKey = !string.IsNullOrWhiteSpace(route.ApiKey)
            ? route.ApiKey.Trim()
            : ExtractBearerToken(source.Headers["Authorization"]);

        if (!string.IsNullOrWhiteSpace(route.ApiKey))
        {
            request.Headers.Remove("Authorization");
            request.Headers.Remove("x-api-key");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {effectiveApiKey}");
        }

        foreach (var header in extraHeaders)
        {
            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (string.Equals(wireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal))
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            if (!string.IsNullOrWhiteSpace(effectiveApiKey))
            {
                request.Headers.Remove("Authorization");
                request.Headers.Remove("x-api-key");
                request.Headers.TryAddWithoutValidation("x-api-key", effectiveApiKey);
            }
        }

        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            if (!string.IsNullOrWhiteSpace(source.ContentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", source.ContentType);
            }
            else
            {
                request.Content.Headers.ContentType = new("application/json");
            }
        }

        return request;
    }

    public static string BuildUpstreamUrl(string baseUrl, string pathAndQuery)
    {
        var baseUri = new Uri(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        var path = pathAndQuery;
        var queryIndex = path.IndexOf('?');
        var query = queryIndex >= 0 ? path[queryIndex..] : string.Empty;
        var pathOnly = queryIndex >= 0 ? path[..queryIndex] : path;

        var normalizedBasePath = baseUri.AbsolutePath.TrimEnd('/');
        var normalizedPath = pathOnly.TrimStart('/');
        if (normalizedBasePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
            normalizedPath.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[3..];
        }

        return new Uri(baseUri, normalizedPath).ToString().TrimEnd('/') + query;
    }

    private static string ExtractBearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return string.Empty;
        }

        var value = authorizationHeader.Trim();
        const string prefix = "Bearer ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].Trim()
            : value;
    }
}
