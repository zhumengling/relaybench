namespace RelayBench.Services;

internal static class TransparentProxyGeminiUrlResolver
{
    private static readonly string[] TerminalSuffixes =
    [
        "/v1beta/openai/chat/completions",
        "/v1/openai/chat/completions",
        "/openai/chat/completions",
        "/v1beta/openai/responses",
        "/v1/openai/responses",
        "/openai/responses",
        "/v1beta/openai",
        "/v1/openai",
        "/openai",
        "/v1beta/models",
        "/v1/models",
        "/models",
        "/v1beta",
        "/v1",
    ];

    public static bool TryBuildNativeUrl(string baseUrl, string pathAndQuery, out string upstreamUrl)
    {
        upstreamUrl = string.Empty;
        if (!TrySplitPathAndQuery(pathAndQuery, out var endpointPath, out var endpointQuery) ||
            !IsGeminiNativeEndpointPath(endpointPath))
        {
            return false;
        }

        var normalizedBase = StripFragment(baseUrl).Trim().TrimEnd('/');
        if (!Uri.TryCreate(normalizedBase, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var host = baseUri.Host;
        var normalizedEndpointPath = "/" + NormalizeDuplicateVersionSegments(endpointPath.TrimStart('/'));
        var baseQuery = string.IsNullOrWhiteSpace(baseUri.Query)
            ? null
            : baseUri.Query.TrimStart('?');
        var query = MergeQueries(baseQuery, endpointQuery);

        if (IsVertexPublisherModelPath(basePath))
        {
            upstreamUrl = BuildTerminalUrl(baseUri, basePath, query);
            return true;
        }

        if (!ShouldNormalizeBasePath(host, basePath))
        {
            return false;
        }

        var prefix = NormalizeBasePrefix(basePath);
        upstreamUrl = BuildUrl(baseUri, prefix, normalizedEndpointPath, query);
        return true;
    }

    private static bool TrySplitPathAndQuery(string pathAndQuery, out string path, out string? query)
    {
        path = string.Empty;
        query = null;
        if (string.IsNullOrWhiteSpace(pathAndQuery))
        {
            return false;
        }

        var withoutFragment = StripFragment(pathAndQuery.Trim());
        var queryIndex = withoutFragment.IndexOf('?');
        path = queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;
        query = queryIndex >= 0 ? withoutFragment[(queryIndex + 1)..] : null;
        path = NormalizeDuplicateVersionSegments(path.TrimStart('/'));
        return !string.IsNullOrWhiteSpace(path);
    }

    private static string StripFragment(string value)
    {
        var fragmentIndex = value.IndexOf('#');
        return fragmentIndex >= 0 ? value[..fragmentIndex] : value;
    }

    private static bool IsGeminiNativeEndpointPath(string path)
    {
        var normalized = NormalizeDuplicateVersionSegments(path.TrimStart('/'));
        return normalized.StartsWith("v1beta/models/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("v1/models/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldNormalizeBasePath(string host, string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || basePath == "/")
        {
            return true;
        }

        if (IsStructuredGeminiModelsPath(basePath))
        {
            return true;
        }

        if (TerminalSuffixes.Any(suffix => EndsWithPath(basePath, suffix)))
        {
            return true;
        }

        return IsGoogleGeminiHost(host) &&
               (EndsWithPath(basePath, "/models") ||
                EndsWithPath(basePath, "/v1/models") ||
                EndsWithPath(basePath, "/v1beta/models") ||
                EndsWithPath(basePath, "/openai") ||
                EndsWithPath(basePath, "/v1/openai") ||
                EndsWithPath(basePath, "/v1beta/openai"));
    }

    private static string NormalizeBasePrefix(string basePath)
    {
        var path = basePath.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return string.Empty;
        }

        foreach (var marker in new[] { "/v1beta/models/", "/v1/models/", "/models/" })
        {
            var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return NormalizePrefix(path[..index]);
            }
        }

        foreach (var suffix in TerminalSuffixes)
        {
            if (string.Equals(path, suffix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (EndsWithPath(path, suffix))
            {
                return NormalizePrefix(path[..^suffix.Length]);
            }
        }

        return path;
    }

    private static string NormalizePrefix(string prefix)
    {
        var normalized = prefix.TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) || normalized == "/"
            ? string.Empty
            : normalized;
    }

    private static bool IsStructuredGeminiModelsPath(string path)
    {
        var cursor = path;
        while (true)
        {
            var index = cursor.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var after = cursor[(index + "/models/".Length)..];
            if (after.Contains(":generateContent", StringComparison.OrdinalIgnoreCase) ||
                after.Contains(":streamGenerateContent", StringComparison.OrdinalIgnoreCase) ||
                after.Contains(":countTokens", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            cursor = after;
        }
    }

    private static bool IsVertexPublisherModelPath(string path)
    {
        var projectsIndex = path.IndexOf("/projects/", StringComparison.OrdinalIgnoreCase);
        var publisherModelsIndex = path.IndexOf("/publishers/google/models/", StringComparison.OrdinalIgnoreCase);
        if (projectsIndex < 0 || publisherModelsIndex < 0 || projectsIndex >= publisherModelsIndex)
        {
            return false;
        }

        if (!path[projectsIndex..publisherModelsIndex].Contains("/locations/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var afterModel = path[(publisherModelsIndex + "/publishers/google/models/".Length)..];
        return afterModel.Contains(":generateContent", StringComparison.OrdinalIgnoreCase) ||
               afterModel.Contains(":streamGenerateContent", StringComparison.OrdinalIgnoreCase) ||
               afterModel.Contains(":countTokens", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGoogleGeminiHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return string.Equals(host, "generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "aiplatform.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("-aiplatform.googleapis.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EndsWithPath(string path, string suffix)
        => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDuplicateVersionSegments(string path)
    {
        var normalized = path;
        while (normalized.StartsWith("v1/v1/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("v1beta/v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase)
                ? normalized["v1beta/".Length..]
                : normalized["v1/".Length..];
        }

        return normalized;
    }

    private static string BuildUrl(Uri baseUri, string prefix, string endpointPath, string? query)
    {
        var origin = baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var normalizedPrefix = NormalizePrefix(prefix);
        var path = string.IsNullOrEmpty(normalizedPrefix)
            ? endpointPath
            : $"{normalizedPrefix}{endpointPath}";
        return AppendQuery($"{origin}{path}", query);
    }

    private static string BuildTerminalUrl(Uri baseUri, string basePath, string? query)
    {
        var origin = baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return AppendQuery($"{origin}{basePath}", query);
    }

    private static string? MergeQueries(string? baseQuery, string? endpointQuery)
    {
        var parts = new[] { baseQuery, endpointQuery }
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .SelectMany(static query => query!.Split('&', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        return parts.Length == 0 ? null : string.Join("&", parts);
    }

    private static string AppendQuery(string url, string? query)
        => string.IsNullOrWhiteSpace(query) ? url : $"{url}?{query}";
}
