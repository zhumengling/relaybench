using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.Core.Services;

namespace RelayBench.Services;

public sealed partial class TransparentProxyService
{
    private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
        {
            return Array.Empty<byte>();
        }

        using MemoryStream stream = new();
        await request.InputStream.CopyToAsync(stream, cancellationToken);
        return stream.ToArray();
    }

    private static string? TryReadRequestModel(byte[] requestBody)
    {
        if (requestBody.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return TryReadStringProperty(document.RootElement, "model");
        }
        catch
        {
            return null;
        }
    }

    private static string FormatLogModel(string? clientModel, string? upstreamModel)
    {
        var client = clientModel?.Trim() ?? string.Empty;
        var upstream = upstreamModel?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(client) &&
            !string.IsNullOrWhiteSpace(upstream) &&
            !string.Equals(client, upstream, StringComparison.OrdinalIgnoreCase))
        {
            return $"{client} -> {upstream}";
        }

        return NormalizeLogModel(!string.IsNullOrWhiteSpace(upstream) ? upstream : client);
    }

    private static string NormalizeLogModel(string? modelName)
        => string.IsNullOrWhiteSpace(modelName) ? "-" : modelName.Trim();

    private static string ExtractRelativePath(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        var pathOnly = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
        return pathOnly.Trim().TrimStart('/');
    }

    private void UpdateRoutesFromModelsList(
        TransparentProxyServerConfig config,
        IReadOnlyList<TransparentProxyRoute> routes)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_config, config))
            {
                return;
            }

            _config = config with { Routes = routes.ToArray() };
        }
    }

    private static string? TryReadStringProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? TryReadStringProperty(byte[] requestBody, string propertyName)
    {
        if (requestBody.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return TryReadStringProperty(document.RootElement, propertyName);
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetCachedModelsList(
        string pathAndQuery,
        TransparentProxyServerConfig config,
        out byte[] payload)
        => _responseCache.TryGetModelsList(pathAndQuery, config, out payload);

    private void CacheModelsListPayload(string pathAndQuery, TransparentProxyServerConfig config, object payload)
        => _responseCache.StoreModelsList(pathAndQuery, config, payload);

    private static string DisplayWireApi(string wireApi)
        => wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic Messages",
            TransparentProxyNativeWireApis.Gemini => "Gemini Native",
            _ => "OpenAI Chat"
        };

    private static long GetElapsedMilliseconds(long startedAt)
        => (long)(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

    private void TrackLatency(long latencyMs)
        => _metrics.TrackLatency(latencyMs);

    private TransparentProxyMetricsSnapshot CreateMetricsSnapshot()
    {
        TransparentProxyRouteMetrics[] routes;
        TransparentProxyServerConfig? config;
        lock (_syncRoot)
        {
            config = _config;
            routes = _routeStates.Values
                .Select(static state => state.ToSnapshot())
                .ToArray();
        }

        var counters = new TransparentProxyMetricsCounters(
            _activeRequests,
            _totalRequests,
            _successRequests,
            _failedRequests,
            _fallbackRequests,
            _cacheHits,
            _rateLimitedRequests);
        var tokenSnapshot = _tokenTelemetry.CreateSnapshot();
        var cacheStats = _responseCache.Stats;
        var promptSessionStats = _protocolTranslator.PromptSessionCacheStats;
        var modelPools = _modelRegistry.BuildSnapshot(config?.Routes ?? Array.Empty<TransparentProxyRoute>(), routes).Pools;
        var ingressMetrics = SnapshotIngressMetrics();
        return _metrics.CreateSnapshot(
            IsRunning,
            config,
            counters,
            routes,
            tokenSnapshot,
            cacheStats,
            promptSessionStats,
            modelPools,
            _tokenTelemetry.UsageEvents.Snapshot(32),
            ingressMetrics);
    }

}
