using System.Net;
using System.Net.Http;
using System.Text.Json;
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
                TryGetModelArray(document.RootElement, out var data))
            {
                return ParseModelArray(data);
            }

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return ParseModelArray(document.RootElement);
            }
        }
        catch
        {
        }

        return Array.Empty<string>();
    }

    private static bool TryGetModelArray(JsonElement root, out JsonElement array)
    {
        foreach (var propertyName in new[] { "data", "models", "items", "result" })
        {
            if (root.TryGetProperty(propertyName, out var candidate) &&
                candidate.ValueKind == JsonValueKind.Array)
            {
                array = candidate;
                return true;
            }
        }

        array = default;
        return false;
    }

    private static IReadOnlyList<string> ParseModelArray(JsonElement array)
        => array.EnumerateArray()
            .Select(static item =>
                item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : TryReadStringProperty(item, "id") ??
                      TryReadStringProperty(item, "name") ??
                      TryReadStringProperty(item, "model"))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static object BuildModelsListPayload(IReadOnlyList<TransparentProxyRouteModels> routeModels)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var registry = new TransparentProxyModelRegistryService().BuildSnapshot(
            routeModels.Select(static routeModel => routeModel.Route).ToArray());
        var models = registry.Pools
            .Where(static pool => !pool.IsPassThrough)
            .Select(pool => new
            {
                id = pool.ModelName,
                @object = "model",
                created = now,
                owned_by = pool.RouteCount > 1
                    ? $"relaybench-pool-{pool.RouteCount}"
                    : string.IsNullOrWhiteSpace(pool.Members.FirstOrDefault()?.RouteName)
                        ? "relaybench"
                        : pool.Members.First().RouteName
            })
            .OrderBy(static item => item.id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            @object = "list",
            data = models
        };
    }

    private static string? TryReadStringProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

}

internal sealed record TransparentProxyModelCatalogResult(
    object Payload,
    IReadOnlyList<TransparentProxyRoute> UpdatedRoutes);

internal sealed record TransparentProxyRouteModels(
    TransparentProxyRoute Route,
    IReadOnlyList<string> Models);
