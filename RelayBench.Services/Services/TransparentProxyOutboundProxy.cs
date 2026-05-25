using System.Net;

namespace RelayBench.Services;

internal enum TransparentProxyOutboundProxyMode
{
    Inherit,
    Direct,
    Proxy,
    Invalid
}

internal enum TransparentProxyOutboundProxyError
{
    None,
    MissingSchemeOrHost,
    UnsupportedScheme,
    HandlerRejected
}

internal sealed record TransparentProxyOutboundProxySetting(
    string Raw,
    TransparentProxyOutboundProxyMode Mode,
    Uri? Uri,
    TransparentProxyOutboundProxyError Error,
    string ErrorMessage)
{
    public bool IsConfigured => Mode is not TransparentProxyOutboundProxyMode.Inherit;

    public bool IsValid => Mode is not TransparentProxyOutboundProxyMode.Invalid;

    public string CacheKey => Mode switch
    {
        TransparentProxyOutboundProxyMode.Inherit => "inherit",
        TransparentProxyOutboundProxyMode.Direct => "direct",
        TransparentProxyOutboundProxyMode.Proxy => Uri?.AbsoluteUri ?? Raw,
        _ => $"invalid:{Raw}"
    };

    public string DisplayEndpoint
        => Uri is null
            ? Raw
            : Uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped);
}

internal static class TransparentProxyOutboundProxy
{
    public static TransparentProxyOutboundProxySetting Parse(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return new TransparentProxyOutboundProxySetting(
                value,
                TransparentProxyOutboundProxyMode.Inherit,
                null,
                TransparentProxyOutboundProxyError.None,
                string.Empty);
        }

        if (string.Equals(value, "direct", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new TransparentProxyOutboundProxySetting(
                value,
                TransparentProxyOutboundProxyMode.Direct,
                null,
                TransparentProxyOutboundProxyError.None,
                string.Empty);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Scheme) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return Invalid(
                value,
                TransparentProxyOutboundProxyError.MissingSchemeOrHost,
                "Outbound proxy must be direct/none or an absolute proxy URL.");
        }

        if (!IsSupportedScheme(uri.Scheme))
        {
            return Invalid(
                value,
                TransparentProxyOutboundProxyError.UnsupportedScheme,
                $"Unsupported outbound proxy scheme: {uri.Scheme}.");
        }

        return new TransparentProxyOutboundProxySetting(
            value,
            TransparentProxyOutboundProxyMode.Proxy,
            uri,
            TransparentProxyOutboundProxyError.None,
            string.Empty);
    }

    public static void ApplyTo(SocketsHttpHandler handler, TransparentProxyOutboundProxySetting setting)
    {
        if (setting.Mode is TransparentProxyOutboundProxyMode.Invalid)
        {
            throw new InvalidOperationException(setting.ErrorMessage);
        }

        if (setting.Mode is TransparentProxyOutboundProxyMode.Inherit)
        {
            return;
        }

        if (setting.Mode is TransparentProxyOutboundProxyMode.Direct)
        {
            handler.UseProxy = false;
            handler.Proxy = null;
            return;
        }

        handler.UseProxy = true;
        handler.Proxy = new WebProxy(setting.Uri ?? throw new InvalidOperationException("Proxy URL is missing."));
    }

    private static bool IsSupportedScheme(string scheme)
        => string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(scheme, "socks5", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(scheme, "socks5h", StringComparison.OrdinalIgnoreCase);

    private static TransparentProxyOutboundProxySetting Invalid(
        string raw,
        TransparentProxyOutboundProxyError error,
        string message)
        => new(
            raw,
            TransparentProxyOutboundProxyMode.Invalid,
            null,
            error,
            message);
}
