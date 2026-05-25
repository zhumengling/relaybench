using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelayBench.Services;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

internal static class TransparentProxyRouteTextCodec
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    public static string BuildRoutesText(IEnumerable<RouteDefinition> routes)
        => string.Join(
            Environment.NewLine,
            routes.Select(static route =>
            {
                var prefix = route.Enabled ? string.Empty : "# ";
                var options = new TransparentProxyRouteTextOptions
                {
                    OutboundProxy = route.OutboundProxy ?? string.Empty,
                    RequestRetryText = route.RequestRetry?.ToString() ?? string.Empty,
                    MaxRetryIntervalSecondsText = route.MaxRetryIntervalSeconds?.ToString() ?? string.Empty,
                    ModelCooldownSecondsText = route.ModelCooldownSeconds?.ToString() ?? string.Empty,
                    PayloadRulesText = route.PayloadRulesText ?? string.Empty,
                    PreferredWireApi = route.PreferredWireApi ?? string.Empty,
                    AuthMode = TransparentProxyRouteAuthModes.Normalize(route.AuthMode),
                    OAuthProvider = string.Equals(route.AuthMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase)
                        ? "codex"
                        : string.Empty,
                    OAuthCredentialId = route.OAuthCredentialId ?? string.Empty,
                    CodexBackendBaseUrl = route.CodexBackendBaseUrl ?? string.Empty,
                    CodexOAuthFastMode = route.CodexOAuthFastMode
                };

                return string.Join(
                    " | ",
                    prefix + "v4",
                    EscapeRouteField(route.Name),
                    EscapeRouteField(route.UpstreamUrl),
                    EscapeRouteField(route.ApiKeyProtected ?? string.Empty),
                    EscapeRouteField(route.ModelFilter ?? string.Empty),
                    EscapeRouteField(route.Priority.ToString()),
                    EscapeRouteField(route.Prefix ?? string.Empty),
                    EscapeRouteField(SerializeHeadersForRouteText(route.HeadersText)),
                    EscapeRouteField(route.ExcludedModelPatterns ?? string.Empty),
                    EncodeOptions(options));
            }));

    public static IReadOnlyList<RouteDefinition> ParseRouteDefinitions(string? text, DateTime? updatedAtUtc = null)
    {
        List<RouteDefinition> routes = [];
        foreach (var parsed in ParseRows(text, includeDisabled: true))
        {
            if (!Uri.TryCreate(parsed.BaseUrl, UriKind.Absolute, out _))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(parsed.Name) ? $"Route {routes.Count + 1}" : parsed.Name.Trim();
            var authMode = TransparentProxyRouteAuthModes.Normalize(parsed.Options.AuthMode);
            var isCodexOAuth = string.Equals(authMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase);
            routes.Add(new RouteDefinition(
                Id: BuildRouteId(name, parsed.BaseUrl, parsed.RoutePrefix),
                Name: name,
                UpstreamUrl: parsed.BaseUrl.Trim(),
                ApiKeyProtected: string.IsNullOrWhiteSpace(parsed.ApiKey) ? null : parsed.ApiKey.Trim(),
                Priority: ParseOptionalInt(parsed.PriorityText, min: 0, max: 100) ?? 0,
                ModelFilter: NormalizeDelimitedText(parsed.Model),
                Enabled: parsed.IsEnabled,
                UpdatedAtUtc: updatedAtUtc ?? DateTime.UtcNow,
                Prefix: NullIfWhiteSpace(parsed.RoutePrefix),
                OutboundProxy: NullIfWhiteSpace(parsed.Options.OutboundProxy),
                RequestRetry: ParseOptionalInt(parsed.Options.RequestRetryText, min: 0, max: 8),
                MaxRetryIntervalSeconds: ParseOptionalInt(parsed.Options.MaxRetryIntervalSecondsText, min: 1, max: 60),
                ModelCooldownSeconds: ParseOptionalInt(parsed.Options.ModelCooldownSecondsText, min: 15, max: 1800),
                ExcludedModelPatterns: NormalizeDelimitedText(parsed.ExcludedModelsText),
                PayloadRulesText: NullIfWhiteSpace(parsed.Options.PayloadRulesText),
                PreferredWireApi: NullIfWhiteSpace(parsed.Options.PreferredWireApi),
                HeadersText: NormalizeHeadersText(parsed.HeadersText),
                AuthMode: authMode,
                OAuthProvider: isCodexOAuth ? "codex" : null,
                OAuthCredentialId: isCodexOAuth ? NullIfWhiteSpace(parsed.Options.OAuthCredentialId) : null,
                CodexBackendBaseUrl: isCodexOAuth ? NullIfWhiteSpace(parsed.Options.CodexBackendBaseUrl) : null,
                CodexOAuthFastMode: isCodexOAuth && parsed.Options.CodexOAuthFastMode));
        }

        return routes;
    }

    public static string BuildRouteId(string name, string baseUrl, string routeKey)
    {
        var input = $"{name.Trim()}|{baseUrl.Trim()}|{routeKey.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash[..8]);
    }

    private static IReadOnlyList<ParsedRouteRow> ParseRows(string? text, bool includeDisabled)
    {
        List<ParsedRouteRow> rows = [];
        var lines = (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var isEnabled = true;
            if (line.StartsWith('#'))
            {
                isEnabled = false;
                line = line[1..].Trim();
            }

            if (!includeDisabled && !isEnabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('|').Select(static item => item.Trim()).ToArray();
            string name;
            string baseUrl;
            string model;
            string apiKey;
            string priorityText = string.Empty;
            string routePrefix = string.Empty;
            string headersText = string.Empty;
            string excludedModelsText = string.Empty;
            TransparentProxyRouteTextOptions options = new();

            if (parts.Length >= 10 && string.Equals(parts[0], "v4", StringComparison.OrdinalIgnoreCase))
            {
                name = parts[1];
                baseUrl = parts[2];
                apiKey = parts[3];
                model = parts[4];
                priorityText = parts[5];
                routePrefix = parts[6];
                headersText = parts[7];
                excludedModelsText = parts[8];
                options = DecodeOptions(parts[9]);
            }
            else if (parts.Length >= 9 && string.Equals(parts[0], "v3", StringComparison.OrdinalIgnoreCase))
            {
                name = parts[1];
                baseUrl = parts[2];
                apiKey = parts[3];
                model = parts[4];
                priorityText = parts[5];
                routePrefix = parts[6];
                headersText = parts[7];
                excludedModelsText = parts[8];
            }
            else if (parts.Length >= 8 && string.Equals(parts[0], "v2", StringComparison.OrdinalIgnoreCase))
            {
                name = parts[1];
                baseUrl = parts[2];
                apiKey = parts[3];
                model = parts[4];
                priorityText = parts[5];
                routePrefix = parts[6];
                headersText = parts[7];
            }
            else if (parts.Length >= 4)
            {
                name = parts[0];
                baseUrl = parts[1];
                model = parts[2];
                apiKey = parts[3];
            }
            else if (parts.Length == 3)
            {
                name = parts[0];
                baseUrl = parts[1];
                model = parts[2];
                apiKey = string.Empty;
            }
            else if (parts.Length == 2)
            {
                name = $"Route {rows.Count + 1}";
                baseUrl = parts[0];
                model = parts[1];
                apiKey = string.Empty;
            }
            else
            {
                continue;
            }

            rows.Add(new ParsedRouteRow(
                isEnabled,
                name,
                baseUrl,
                model,
                apiKey,
                priorityText,
                routePrefix,
                headersText,
                excludedModelsText,
                options));
        }

        return rows;
    }

    private static string EscapeRouteField(string? value)
        => (value ?? string.Empty)
            .Replace("|", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static string EncodeOptions(TransparentProxyRouteTextOptions options)
    {
        var json = JsonSerializer.Serialize(options, CompactJsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
    }

    private static TransparentProxyRouteTextOptions DecodeOptions(string? value)
    {
        try
        {
            var normalized = (value ?? string.Empty)
                .Trim()
                .Replace("-", "+", StringComparison.Ordinal)
                .Replace("_", "/", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new TransparentProxyRouteTextOptions();
            }

            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return JsonSerializer.Deserialize<TransparentProxyRouteTextOptions>(json, CompactJsonOptions) ??
                   new TransparentProxyRouteTextOptions();
        }
        catch
        {
            return new TransparentProxyRouteTextOptions();
        }
    }

    private static string? NormalizeHeadersText(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TryNormalizeJsonObject(text, out var json))
        {
            return json;
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var headerValue = line[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                headers[name] = headerValue;
            }
        }

        return headers.Count == 0
            ? null
            : JsonSerializer.Serialize(headers, CompactJsonOptions);
    }

    private static string SerializeHeadersForRouteText(string? headersText)
    {
        if (string.IsNullOrWhiteSpace(headersText))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(headersText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return EscapeRouteField(headersText);
            }

            return string.Join(
                ";",
                document.RootElement.EnumerateObject()
                    .Select(static property => $"{property.Name.Trim()}: {ReadHeaderValue(property.Value)}")
                    .Where(static item => !string.IsNullOrWhiteSpace(item)));
        }
        catch (JsonException)
        {
            return EscapeRouteField(headersText);
        }
    }

    private static string ReadHeaderValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : value.ToString().Trim();

    private static bool TryNormalizeJsonObject(string text, out string json)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                json = JsonSerializer.Serialize(document.RootElement, CompactJsonOptions);
                return true;
            }
        }
        catch (JsonException)
        {
        }

        json = string.Empty;
        return false;
    }

    private static string? NormalizeDelimitedText(string? value)
    {
        var normalized = string.Join(
            ",",
            (value ?? string.Empty)
                .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static item => item.Trim())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static int? ParseOptionalInt(string? value, int min, int max)
        => int.TryParse((value ?? string.Empty).Trim(), out var parsed)
            ? Math.Clamp(parsed, min, max)
            : null;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ParsedRouteRow(
        bool IsEnabled,
        string Name,
        string BaseUrl,
        string Model,
        string ApiKey,
        string PriorityText,
        string RoutePrefix,
        string HeadersText,
        string ExcludedModelsText,
        TransparentProxyRouteTextOptions Options);
}

internal sealed class TransparentProxyRouteTextOptions
{
    public string OutboundProxy { get; set; } = string.Empty;

    public string RequestRetryText { get; set; } = string.Empty;

    public string MaxRetryIntervalSecondsText { get; set; } = string.Empty;

    public string ModelCooldownSecondsText { get; set; } = string.Empty;

    public string PayloadRulesText { get; set; } = string.Empty;

    public string PreferredWireApi { get; set; } = string.Empty;

    public string AuthMode { get; set; } = TransparentProxyRouteAuthModes.ApiKey;

    public string OAuthProvider { get; set; } = string.Empty;

    public string OAuthCredentialId { get; set; } = string.Empty;

    public string CodexBackendBaseUrl { get; set; } = string.Empty;

    public bool CodexOAuthFastMode { get; set; }
}
