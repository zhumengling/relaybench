namespace RelayBench.Core.Services;

public static class EndpointPathBuilder
{
    public static string BuildOpenAiCompatiblePath(string baseUrl, string endpoint)
        => BuildOpenAiCompatiblePath(new Uri(baseUrl.Trim(), UriKind.Absolute), endpoint);

    public static string BuildOpenAiCompatiblePath(Uri baseUri, string endpoint)
    {
        var normalizedPath = baseUri.AbsolutePath.TrimEnd('/');
        var normalizedEndpoint = endpoint.Trim().TrimStart('/');
        return normalizedPath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalizedEndpoint
            : $"v1/{normalizedEndpoint}";
    }

    public static string CombineOpenAiCompatibleUrl(string baseUrl, string endpoint)
    {
        var baseUri = new Uri(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        var path = BuildOpenAiCompatiblePath(baseUri, endpoint);
        return new Uri(baseUri, path).ToString().TrimEnd('/');
    }
}
