namespace RelayBench.WinUI.ViewModels;

internal static class BatchEndpointText
{
    public static bool LooksLikeBaseUrl(string? value) => NormalizeBaseUrl(value) is not null;

    public static string? NormalizeBaseUrl(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var hasScheme = text.Contains("://", StringComparison.Ordinal);
        var candidate = hasScheme ? text : $"http://{text}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        if (!hasScheme && !LooksLikeHostEndpoint(text, uri.Host))
        {
            return null;
        }

        return uri.ToString().TrimEnd('/');
    }

    public static string? TryGetHost(string? value)
    {
        var normalized = NormalizeBaseUrl(value);
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
               !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
    }

    private static bool LooksLikeHostEndpoint(string originalText, string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           System.Net.IPAddress.TryParse(host, out _) ||
           host.Contains('.', StringComparison.Ordinal) ||
           originalText.Contains(':', StringComparison.Ordinal);
}
