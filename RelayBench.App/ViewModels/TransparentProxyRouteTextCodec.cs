using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

internal static class TransparentProxyRouteTextCodec
{
    public static string BuildRoutesTextFromSeeds(IEnumerable<TransparentProxyRouteTextSeed> seeds)
        => string.Join(
            Environment.NewLine,
            seeds.Select(static item =>
                $"{EscapeRouteField(item.Name)} | {EscapeRouteField(item.BaseUrl)} |  | {EscapeRouteField(item.ApiKey)}"));

    public static string BuildRoutesTextFromEditor(IEnumerable<TransparentProxyRouteEditorItemViewModel> items)
        => string.Join(
            Environment.NewLine,
            items.Select(static item =>
            {
                var prefix = item.IsEnabled ? string.Empty : "# ";
                var options = new TransparentProxyRouteTextOptions
                {
                    OutboundProxy = item.OutboundProxy,
                    RequestRetryText = item.RequestRetryText,
                    MaxRetryIntervalSecondsText = item.MaxRetryIntervalSecondsText,
                    ModelCooldownSecondsText = item.ModelCooldownSecondsText,
                    PayloadRulesText = item.PayloadRulesText,
                    AuthMode = item.AuthMode,
                    OAuthProvider = item.OAuthProvider,
                    OAuthCredentialId = item.OAuthCredentialId,
                    CodexBackendBaseUrl = item.CodexBackendBaseUrl
                };
                return $"{prefix}v4 | {EscapeRouteField(item.Name)} | {EscapeRouteField(item.BaseUrl)} | {EscapeRouteField(item.ApiKey)} | {EscapeRouteField(JoinRouteModels(item.ModelMappings))} | {EscapeRouteField(item.PriorityText)} | {EscapeRouteField(item.Prefix)} | {EscapeRouteField(SerializeHeaders(item.Headers))} | {EscapeRouteField(item.ExcludedModelsText)} | {EncodeOptions(options)}";
            }));

    public static IReadOnlyList<TransparentProxyRouteEditorItemViewModel> ParseEditorItems(string text)
    {
        List<TransparentProxyRouteEditorItemViewModel> items = [];
        foreach (var parsed in ParseRows(text, includeDisabled: true))
        {
            items.Add(new TransparentProxyRouteEditorItemViewModel
            {
                IsEnabled = parsed.IsEnabled,
                Name = string.IsNullOrWhiteSpace(parsed.Name) ? $"路由 {items.Count + 1}" : parsed.Name,
                BaseUrl = parsed.BaseUrl,
                ModelsText = parsed.Model.Replace(",", Environment.NewLine, StringComparison.Ordinal),
                PriorityText = parsed.PriorityText,
                Prefix = parsed.RoutePrefix,
                HeadersText = parsed.HeadersText.Replace(";", Environment.NewLine, StringComparison.Ordinal),
                ExcludedModelsText = parsed.ExcludedModelsText,
                OutboundProxy = parsed.Options.OutboundProxy,
                RequestRetryText = parsed.Options.RequestRetryText,
                MaxRetryIntervalSecondsText = parsed.Options.MaxRetryIntervalSecondsText,
                ModelCooldownSecondsText = parsed.Options.ModelCooldownSecondsText,
                PayloadRulesText = parsed.Options.PayloadRulesText,
                AuthMode = parsed.Options.AuthMode,
                OAuthProvider = parsed.Options.OAuthProvider,
                OAuthCredentialId = parsed.Options.OAuthCredentialId,
                CodexBackendBaseUrl = parsed.Options.CodexBackendBaseUrl,
                ApiKey = parsed.ApiKey
            });
        }

        return items;
    }

    public static IReadOnlyList<TransparentProxyRoute> ParseRoutes(string text)
    {
        List<TransparentProxyRoute> routes = [];
        foreach (var parsed in ParseRows(text, includeDisabled: false))
        {
            if (!Uri.TryCreate(parsed.BaseUrl, UriKind.Absolute, out _))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(parsed.Name) ? $"路由 {routes.Count + 1}" : parsed.Name;
            var modelMappings = SplitRouteModelMappings(parsed.Model);
            var models = modelMappings
                .Select(static mapping => mapping.Name)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            routes.Add(new TransparentProxyRoute(
                BuildRouteId(name, parsed.BaseUrl, parsed.RoutePrefix),
                name,
                parsed.BaseUrl.Trim(),
                parsed.ApiKey.Trim(),
                models.FirstOrDefault() ?? string.Empty,
                models: models,
                priority: ParsePriority(parsed.PriorityText),
                prefix: parsed.RoutePrefix,
                headers: ParseHeaders(parsed.HeadersText),
                modelMappings: modelMappings,
                excludedModelPatterns: SplitRouteModels(parsed.ExcludedModelsText),
                outboundProxy: parsed.Options.OutboundProxy,
                requestRetry: ParseOptionalInt(parsed.Options.RequestRetryText, min: 0, max: 5),
                maxRetryIntervalSeconds: ParseOptionalInt(parsed.Options.MaxRetryIntervalSecondsText, min: 1, max: 60),
                modelCooldownSeconds: ParseOptionalInt(parsed.Options.ModelCooldownSecondsText, min: 15, max: 1800),
                payloadRulesText: parsed.Options.PayloadRulesText,
                authMode: parsed.Options.AuthMode,
                oauthProvider: parsed.Options.OAuthProvider,
                oauthCredentialId: parsed.Options.OAuthCredentialId,
                codexBackendBaseUrl: parsed.Options.CodexBackendBaseUrl));
        }

        return routes;
    }

    public static string BuildRouteId(string name, string baseUrl, string routeKey)
    {
        var input = $"{name}|{baseUrl}|{routeKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash[..8]);
    }

    private static IReadOnlyList<ParsedRouteRow> ParseRows(string text, bool includeDisabled)
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
                name = $"路由 {rows.Count + 1}";
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

    private static string EscapeRouteField(string value)
        => (value ?? string.Empty)
            .Replace("|", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static string JoinRouteModels(IEnumerable<TransparentProxyModelMappingViewModel> models)
        => string.Join(",", models
            .Where(static model => !string.IsNullOrWhiteSpace(model.Name))
            .Select(static model =>
            {
                var name = EscapeModelMappingToken(model.Name);
                var alias = EscapeModelMappingToken(model.Alias);
                return string.IsNullOrWhiteSpace(alias)
                    ? name
                    : $"{name}=>{alias}";
            })
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<string> SplitRouteModels(string value)
        => (value ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<TransparentProxyModelMapping> SplitRouteModelMappings(string value)
    {
        List<TransparentProxyModelMapping> mappings = [];
        foreach (var token in (value ?? string.Empty).Split([',', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = token;
            var alias = string.Empty;
            var separator = token.IndexOf("=>", StringComparison.Ordinal);
            if (separator < 0)
            {
                separator = token.IndexOf("->", StringComparison.Ordinal);
            }

            if (separator >= 0)
            {
                name = token[..separator];
                alias = token[(separator + 2)..];
            }

            name = EscapeModelMappingToken(name);
            alias = EscapeModelMappingToken(alias);
            if (!string.IsNullOrWhiteSpace(name) &&
                mappings.All(item => !string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                mappings.Add(new TransparentProxyModelMapping(name, alias));
            }
        }

        return mappings;
    }

    private static string EscapeModelMappingToken(string value)
        => (value ?? string.Empty)
            .Replace("|", " ", StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace(";", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static int ParsePriority(string value)
        => int.TryParse((value ?? string.Empty).Trim(), out var priority) ? Math.Max(0, priority) : 0;

    private static int? ParseOptionalInt(string value, int min, int max)
        => int.TryParse((value ?? string.Empty).Trim(), out var parsed)
            ? Math.Clamp(parsed, min, max)
            : null;

    private static string EncodeOptions(TransparentProxyRouteTextOptions options)
    {
        var json = JsonSerializer.Serialize(options);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
    }

    private static TransparentProxyRouteTextOptions DecodeOptions(string value)
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
            return JsonSerializer.Deserialize<TransparentProxyRouteTextOptions>(json) ?? new TransparentProxyRouteTextOptions();
        }
        catch
        {
            return new TransparentProxyRouteTextOptions();
        }
    }

    private static string SerializeHeaders(IReadOnlyDictionary<string, string> headers)
        => string.Join(";", headers
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(static pair => $"{pair.Key.Trim()}: {pair.Value?.Trim()}"));

    private static IReadOnlyDictionary<string, string> ParseHeaders(string value)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (value ?? string.Empty).Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

        return headers;
    }

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

    public string AuthMode { get; set; } = TransparentProxyRouteAuthModes.ApiKey;

    public string OAuthProvider { get; set; } = string.Empty;

    public string OAuthCredentialId { get; set; } = string.Empty;

    public string CodexBackendBaseUrl { get; set; } = string.Empty;
}

internal sealed record TransparentProxyRouteTextSeed(
    string Name,
    string BaseUrl,
    string Model,
    string ApiKey);
