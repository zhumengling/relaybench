using System.Collections.Specialized;

namespace RelayBench.Services;

internal static class TransparentProxyHeaderFilter
{
    private static readonly HashSet<string> RequestHopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "trailers",
        "transfer-encoding",
        "upgrade",
        "host",
        "content-length"
    };

    private static readonly HashSet<string> ResponseBlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "trailers",
        "transfer-encoding",
        "upgrade",
        "set-cookie",
        "content-length",
        "content-encoding"
    };

    private static readonly string[] GatewayHeaderPrefixes =
    [
        "x-litellm-",
        "helicone-",
        "x-portkey-",
        "cf-aig-",
        "x-kong-",
        "x-bt-"
    ];

    public static ISet<string> ResolveConnectionScopedHeaders(NameValueCollection headers)
        => ResolveConnectionScopedHeaders(headers.GetValues("Connection"));

    public static ISet<string> ResolveConnectionScopedHeaders(IEnumerable<string>? connectionValues)
    {
        HashSet<string> scoped = new(StringComparer.OrdinalIgnoreCase);
        if (connectionValues is null)
        {
            return scoped;
        }

        foreach (var rawValue in connectionValues)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            foreach (var token in rawValue.Split(','))
            {
                var headerName = token.Trim();
                if (!string.IsNullOrWhiteSpace(headerName))
                {
                    scoped.Add(headerName);
                }
            }
        }

        return scoped;
    }

    public static bool ShouldSkipForwardedRequestHeader(string? headerName, ISet<string>? connectionScopedHeaders = null)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return true;
        }

        return RequestHopByHopHeaders.Contains(headerName) ||
               connectionScopedHeaders?.Contains(headerName) == true;
    }

    public static bool ShouldSkipForwardedResponseHeader(string? headerName, ISet<string>? connectionScopedHeaders = null)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return true;
        }

        if (ResponseBlockedHeaders.Contains(headerName) ||
            connectionScopedHeaders?.Contains(headerName) == true)
        {
            return true;
        }

        var lowerName = headerName.Trim().ToLowerInvariant();
        return GatewayHeaderPrefixes.Any(prefix => lowerName.StartsWith(prefix, StringComparison.Ordinal));
    }
}
