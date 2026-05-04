using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyModelCatalogService
{
    public async Task<TransparentProxyModelCatalogResult> BuildModelsListPayloadAsync(
        HttpListenerRequest source,
        HttpClient client,
        Func<TransparentProxyRoute, HttpClient>? clientResolver,
        TransparentProxyServerConfig config,
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        List<TransparentProxyRouteModels> routeModels = [];
        List<TransparentProxyRoute> updatedRoutes = [];
        foreach (var route in config.Routes)
        {
            var models = await FetchRouteModelIdsForListAsync(
                source,
                clientResolver?.Invoke(route) ?? client,
                config,
                route,
                pathAndQuery,
                cancellationToken);
            var hydratedRoute = models.Count > 0
                ? route.WithModels(models)
                : route;
            updatedRoutes.Add(hydratedRoute);
            routeModels.Add(new TransparentProxyRouteModels(
                hydratedRoute,
                hydratedRoute.Models));
        }

        return new TransparentProxyModelCatalogResult(
            BuildModelsListPayload(routeModels),
            updatedRoutes);
    }

    private static async Task<IReadOnlyList<string>> FetchRouteModelIdsForListAsync(
        HttpListenerRequest source,
        HttpClient client,
        TransparentProxyServerConfig config,
        TransparentProxyRoute route,
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource requestCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellationSource.CancelAfter(
                TimeSpan.FromSeconds(Math.Clamp(config.UpstreamTimeoutSeconds, 5, 30)));

            using var request = TransparentProxyUpstreamRequestFactory.Create(
                source,
                "GET",
                TransparentProxyUpstreamRequestFactory.BuildUpstreamUrl(route.BaseUrl, pathAndQuery),
                route,
                Array.Empty<byte>(),
                ProxyWireApiProbeService.ChatCompletionsWireApi,
                route.Headers);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellationSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(requestCancellationSource.Token);
            return ParseModelIds(bytes);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ParseModelIds(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                return data.EnumerateArray()
                    .Select(static item => TryReadStringProperty(item, "id"))
                    .Where(static id => !string.IsNullOrWhiteSpace(id))
                    .Select(static id => id!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement.EnumerateArray()
                    .Select(static item =>
                        item.ValueKind == JsonValueKind.String
                            ? item.GetString()
                            : TryReadStringProperty(item, "id"))
                    .Where(static id => !string.IsNullOrWhiteSpace(id))
                    .Select(static id => id!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
        catch
        {
        }

        return Array.Empty<string>();
    }

    private static object BuildModelsListPayload(IReadOnlyList<TransparentProxyRouteModels> routeModels)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var models = routeModels
            .SelectMany(routeModel => BuildRouteVisibleModels(routeModel.Route, routeModel.Models)
                .Select(model => new
                {
                    id = model,
                    @object = "model",
                    created = now,
                    owned_by = string.IsNullOrWhiteSpace(routeModel.Route.Name) ? "relaybench" : routeModel.Route.Name
                }))
            .GroupBy(static item => item.id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static item => item.id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            @object = "list",
            data = models
        };
    }

    private static IEnumerable<string> BuildRouteVisibleModels(TransparentProxyRoute route, IReadOnlyList<string> models)
    {
        var mappings = route.ModelMappings.Count > 0
            ? route.ModelMappings
            : models.Select(static model => new TransparentProxyModelMapping(model, string.Empty)).ToArray();
        foreach (var mapping in mappings)
        {
            var upstreamModel = mapping.Name.Trim();
            var model = mapping.EffectiveAlias;
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            if (route.ExcludedModelPatterns.Any(pattern =>
                    WildcardMatch(upstreamModel, pattern) ||
                    WildcardMatch(model, pattern)))
            {
                continue;
            }

            yield return model.Trim();
            var prefix = route.Prefix.Trim().Trim('/');
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                yield return $"{prefix}/{model.Trim()}";
            }
        }
    }

    private static string? TryReadStringProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool WildcardMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = "^" + string.Concat(pattern.Trim().Select(static character => character switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(character.ToString())
        })) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

internal sealed record TransparentProxyModelCatalogResult(
    object Payload,
    IReadOnlyList<TransparentProxyRoute> UpdatedRoutes);

internal sealed record TransparentProxyRouteModels(
    TransparentProxyRoute Route,
    IReadOnlyList<string> Models);
