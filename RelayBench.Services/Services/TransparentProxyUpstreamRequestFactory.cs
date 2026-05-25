using System.Net;
using System.Net.Http;
using System.Text;
using System.Collections.Specialized;
using RelayBench.Core.Services;

namespace RelayBench.Services;

internal static class TransparentProxyUpstreamRequestFactory
{
    public static HttpRequestMessage Create(
        HttpListenerRequest source,
        string method,
        string upstreamUrl,
        TransparentProxyRoute route,
        byte[] body,
        string wireApi,
        IReadOnlyDictionary<string, string> extraHeaders,
        TransparentProxyRouteAuthMaterial? authMaterial = null)
        => Create(
            method,
            upstreamUrl,
            source.Headers,
            source.ContentType,
            route,
            body,
            wireApi,
            extraHeaders,
            authMaterial);

    internal static HttpRequestMessage Create(
        string method,
        string upstreamUrl,
        NameValueCollection sourceHeaders,
        string? sourceContentType,
        TransparentProxyRoute route,
        byte[] body,
        string wireApi,
        IReadOnlyDictionary<string, string> extraHeaders,
        TransparentProxyRouteAuthMaterial? authMaterial = null)
    {
        HttpRequestMessage request = new(new HttpMethod(method), upstreamUrl);
        var connectionScopedHeaders = TransparentProxyHeaderFilter.ResolveConnectionScopedHeaders(sourceHeaders);
        foreach (var headerName in sourceHeaders.AllKeys)
        {
            if (TransparentProxyHeaderFilter.ShouldSkipForwardedRequestHeader(headerName, connectionScopedHeaders))
            {
                continue;
            }

            var forwardedHeaderName = headerName!;
            var values = sourceHeaders.GetValues(forwardedHeaderName);
            if (values is null)
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(forwardedHeaderName, values))
            {
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.TryAddWithoutValidation(forwardedHeaderName, values);
            }
        }

        var effectiveApiKey = !string.IsNullOrWhiteSpace(route.ApiKey)
            ? route.ApiKey.Trim()
            : ExtractBearerToken(sourceHeaders["Authorization"]);
        var googleApiKey = !string.IsNullOrWhiteSpace(route.ApiKey)
            ? route.ApiKey.Trim()
            : sourceHeaders["x-goog-api-key"]?.Trim() ?? string.Empty;

        if (authMaterial?.IsCodexOAuth == true)
        {
            request.Headers.Remove("Authorization");
            request.Headers.Remove("x-api-key");
            request.Headers.Remove("x-goog-api-key");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authMaterial.BearerToken}");
            foreach (var header in authMaterial.Headers)
            {
                request.Headers.Remove(header.Key);
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        else if (string.Equals(wireApi, TransparentProxyNativeWireApis.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Remove("Authorization");
            request.Headers.Remove("x-api-key");
            request.Headers.Remove("x-goog-api-key");
            if (!string.IsNullOrWhiteSpace(googleApiKey))
            {
                request.Headers.TryAddWithoutValidation("x-goog-api-key", googleApiKey);
            }
        }
        else if (!string.IsNullOrWhiteSpace(route.ApiKey))
        {
            request.Headers.Remove("Authorization");
            request.Headers.Remove("x-api-key");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {effectiveApiKey}");
        }

        foreach (var header in extraHeaders)
        {
            if (TransparentProxyHeaderFilter.ShouldSkipForwardedRequestHeader(header.Key))
            {
                continue;
            }

            if (authMaterial?.IsCodexOAuth == true &&
                (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(header.Key, "x-api-key", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(header.Key, "x-goog-api-key", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (string.Equals(wireApi, TransparentProxyNativeWireApis.Gemini, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(header.Key, "x-api-key", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(header.Key, "x-goog-api-key", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (authMaterial?.IsCodexOAuth != true &&
            string.Equals(wireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal))
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
            if (!string.IsNullOrWhiteSpace(sourceContentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", sourceContentType);
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
        if (TransparentProxyGeminiUrlResolver.TryBuildNativeUrl(baseUrl, pathAndQuery, out var geminiNativeUrl))
        {
            return geminiNativeUrl;
        }

        var baseUri = new Uri(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        var path = pathAndQuery;
        var queryIndex = path.IndexOf('?');
        var query = queryIndex >= 0 ? path[queryIndex..] : string.Empty;
        var pathOnly = queryIndex >= 0 ? path[..queryIndex] : path;

        var normalizedBasePath = baseUri.AbsolutePath.TrimEnd('/');
        var normalizedPath = pathOnly.TrimStart('/');
        while (normalizedPath.StartsWith("v1/v1/", StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith("v1beta/v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase)
                ? normalizedPath["v1beta/".Length..]
                : normalizedPath[3..];
        }

        if (normalizedBasePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
            normalizedBasePath.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
        {
            var versionSegment = normalizedBasePath.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
                ? "v1beta/"
                : "v1/";
            while (normalizedPath.StartsWith(versionSegment, StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = normalizedPath[versionSegment.Length..];
            }
        }

        var relativeUriPath = normalizedPath.Contains(':', StringComparison.Ordinal) &&
                              !normalizedPath.StartsWith("./", StringComparison.Ordinal) &&
                              !normalizedPath.StartsWith("/", StringComparison.Ordinal)
            ? "./" + normalizedPath
            : normalizedPath;
        return new Uri(baseUri, relativeUriPath).ToString().TrimEnd('/') + query;
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
