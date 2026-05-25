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
    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken serverCancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var method = context.Request.HttpMethod;
        var pathAndQuery = context.Request.RawUrl ?? "/";
        var requestId = _gateway.CreateRequestId();
        CurrentIngressContext.Value = ClassifyTransparentProxyIngress(context.Request, pathAndQuery);
        var config = _config;

        AddCorsHeaders(context.Response);

        if (_gateway.IsCorsPreflight(method))
        {
            await WriteTextResponseAsync(context, 204, string.Empty, "text/plain", serverCancellationToken);
            return;
        }

        var isManagementEndpoint = _gateway.TryResolveManagementEndpoint(pathAndQuery, out var managementEndpoint);
        if (isManagementEndpoint &&
            !IsManagementRequestAllowed(
                config,
                context.Request.RemoteEndPoint?.Address,
                context.Request.Headers["X-Management-Key"] ?? context.Request.Headers["X-RelayBench-Management-Key"]))
        {
            await WriteJsonErrorAsync(
                context,
                IsLoopbackAddress(context.Request.RemoteEndPoint?.Address) ? 401 : 403,
                "management_api_forbidden",
                "Transparent proxy management endpoint requires local access or a configured management key.",
                serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Health)
        {
            await WriteJsonResponseAsync(context, 200, BuildHealthPayload(), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Metrics)
        {
            await WriteJsonResponseAsync(context, 200, CreateMetricsSnapshot(), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Usage)
        {
            await WriteJsonResponseAsync(context, 200, BuildUsagePayload(), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Ingress)
        {
            await WriteJsonResponseAsync(context, 200, BuildIngressPayload(), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.CaptureApps)
        {
            await WriteJsonResponseAsync(context, 200, BuildCaptureAppsPayload(), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.CaptureDiagnostics)
        {
            await WriteJsonResponseAsync(context, 200, BuildCaptureDiagnosticsPayload(), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.CaptureRecovery)
        {
            var execute = ShouldExecuteCaptureRecovery(method);
            await WriteJsonResponseAsync(context, 200, BuildCaptureRecoveryPayload(execute), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Logs)
        {
            await WriteJsonResponseAsync(context, 200, BuildLogsPayload(pathAndQuery), serverCancellationToken);
            return;
        }

        var client = _httpClient;
        if (config is null || client is null || config.Routes.Count == 0)
        {
            await WriteJsonErrorAsync(context, 503, "transparent_proxy_not_ready", "Transparent proxy has no configured upstream routes.", serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Models)
        {
            var metrics = CreateMetricsSnapshot();
            await WriteJsonResponseAsync(
                context,
                200,
                _modelRegistry.BuildSnapshot(config.Routes, metrics.Routes),
                serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Cache)
        {
            await WriteJsonResponseAsync(context, 200, BuildCachePayload(), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Routes)
        {
            await WriteJsonResponseAsync(context, 200, BuildRoutesPayload(), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Scheduler)
        {
            await WriteJsonResponseAsync(context, 200, BuildSchedulerPayload(pathAndQuery), serverCancellationToken);
            return;
        }

        if (isManagementEndpoint && managementEndpoint == TransparentProxyManagementEndpoint.Protocols)
        {
            await WriteJsonResponseAsync(context, 200, BuildProtocolsPayload(), serverCancellationToken);
            return;
        }

        if (!TryAcquireRateSlot(config))
        {
            Interlocked.Increment(ref _rateLimitedRequests);
            Interlocked.Increment(ref _totalRequests);
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", method, pathAndQuery, "-", 429, 0, "Local rate limit exceeded."));
            PublishMetrics();
            await WriteJsonErrorAsync(context, 429, "relaybench_rate_limited", "RelayBench transparent proxy local rate limit exceeded.", serverCancellationToken);
            return;
        }

        var concurrencyGate = _concurrencyGate;
        if (concurrencyGate is null || !await concurrencyGate.WaitAsync(0, serverCancellationToken))
        {
            Interlocked.Increment(ref _rateLimitedRequests);
            Interlocked.Increment(ref _totalRequests);
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", method, pathAndQuery, "-", 429, 0, "Concurrency guard rejected the request."));
            PublishMetrics();
            await WriteJsonErrorAsync(context, 429, "relaybench_concurrency_limited", "RelayBench transparent proxy concurrency limit reached.", serverCancellationToken);
            return;
        }

        Interlocked.Increment(ref _activeRequests);
        Interlocked.Increment(ref _totalRequests);
        PublishMetrics();

        try
        {
            var requestBody = await ReadRequestBodyAsync(context.Request, serverCancellationToken);
            requestBody = _securityFilter.FilterPrivateFields(requestBody, out var removedPrivateFields);
            if (removedPrivateFields > 0)
            {
                EmitLog(new TransparentProxyLogEntry(
                    DateTimeOffset.Now,
                    "INFO",
                    method,
                    pathAndQuery,
                    "-",
                    0,
                    GetElapsedMilliseconds(startedAt),
                    $"Removed {removedPrivateFields} private request field(s) before upstream forwarding.",
                    TryReadRequestModel(requestBody) ?? string.Empty,
                    requestId,
                    "security",
                    "private-field-filter"));
            }

            var requestClassification = _requestClassifier.Classify(context.Request, method, pathAndQuery, requestBody);
            var requestedModel = requestClassification.ModelName;
            var streamRequested = requestClassification.StreamRequested;
            _responseCache.PruneExpiredResponses(config.CacheTtlSeconds);
            var cacheKey = string.Empty;

            if (requestClassification.IsModelsList)
            {
                if (TryGetCachedModelsList(pathAndQuery, config, out var cachedModelsPayload))
                {
                    Interlocked.Increment(ref _cacheHits);
                    Interlocked.Increment(ref _successRequests);
                    await WriteJsonBytesResponseAsync(context, 200, cachedModelsPayload, serverCancellationToken);
                    var cacheElapsed = GetElapsedMilliseconds(startedAt);
                    TrackLatency(cacheElapsed);
                    EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "CACHE", method, pathAndQuery, "models", 200, cacheElapsed, "Aggregated models list cache hit."));
                    PublishMetrics();
                    return;
                }

                Interlocked.Increment(ref _successRequests);
                var modelCatalog = await _modelCatalog.BuildModelsListPayloadAsync(
                    context.Request,
                    client,
                    route => ResolveHttpClientForRoute(route, config),
                    config,
                    pathAndQuery,
                    serverCancellationToken);
                UpdateRoutesFromModelsList(config, modelCatalog.UpdatedRoutes);
                CacheModelsListPayload(pathAndQuery, config, modelCatalog.Payload);
                await WriteJsonResponseAsync(context, 200, modelCatalog.Payload, serverCancellationToken);
                var elapsed = GetElapsedMilliseconds(startedAt);
                TrackLatency(elapsed);
                EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "INFO", method, pathAndQuery, "models", 200, elapsed, "Returned transparent proxy aggregated models list."));
                PublishMetrics();
                return;
            }

            TransparentProxyUpstreamAttempt? lastAttempt = null;
            List<string> requestAttemptSummaries = [];
            var sessionKey = _sessionAffinityKeyService.Build(context.Request, requestBody);
            var candidateRoutes = BuildCandidateRoutes(config, requestedModel, sessionKey);
            if (candidateRoutes.Count == 0)
            {
                Interlocked.Increment(ref _failedRequests);
                var elapsed = GetElapsedMilliseconds(startedAt);
                TrackLatency(elapsed);
                if (!string.IsNullOrWhiteSpace(requestedModel) &&
                    config.Routes.All(route => !CanRouteServeModel(route, requestedModel)))
                {
                    EmitLog(new TransparentProxyLogEntry(
                        DateTimeOffset.Now,
                        "WARN",
                        method,
                        pathAndQuery,
                        "-",
                        404,
                        elapsed,
                        $"Requested model is not available on any route: {ProbeTraceRedactor.RedactText(requestedModel)}",
                        requestedModel));
                    PublishMetrics();
                    await WriteJsonErrorAsync(
                        context,
                        404,
                        "model_not_found",
                        $"Model '{ProbeTraceRedactor.RedactText(requestedModel)}' is not available in the current RelayBench route set.",
                        serverCancellationToken);
                    return;
                }

                EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", method, pathAndQuery, "-", 503, elapsed, "All upstream routes are temporarily open-circuit; waiting for half-open probe window.", requestedModel ?? string.Empty));
                PublishMetrics();
                await WriteJsonErrorAsync(context, 503, "relaybench_all_routes_circuit_open", "All upstream routes are temporarily unavailable; RelayBench will probe again automatically.", serverCancellationToken);
                return;
            }

            var attempts = config.EnableFallback ? candidateRoutes.Count : Math.Min(1, candidateRoutes.Count);
            var bypassCircuitBreaker = !config.EnableFallback || config.Routes.Count <= 1;
            var attemptedRoutes = 0;
            async Task WriteCacheHitAsync(TransparentProxyRoute route, TransparentProxyCachedResponse cachedResponse)
            {
                Interlocked.Increment(ref _cacheHits);
                Interlocked.Increment(ref _successRequests);
                await WriteCachedResponseAsync(context, cachedResponse, serverCancellationToken);
                using (PushTransparentProxyUsageContext(cachedResponse.ModelName, route.Name, "cache", "hit"))
                {
                    _responseForwarder.TrackResponseBodyTokens(cachedResponse.Body);
                }
                var elapsed = GetElapsedMilliseconds(startedAt);
                TrackLatency(elapsed);
                BindSessionRoute(config, sessionKey, route.Id);
                EmitLog(new TransparentProxyLogEntry(
                    DateTimeOffset.Now,
                    "CACHE",
                    method,
                    pathAndQuery,
                    route.Name,
                    cachedResponse.StatusCode,
                    elapsed,
                    "Hit local response cache.",
                    cachedResponse.ModelName,
                    requestId,
                    "cache",
                    "local response cache"));
                PublishMetrics();
            }

            for (var index = 0; index < attempts; index++)
            {
                var route = candidateRoutes[index];
                if (!TryAcquireRoutePermit(route, bypassCircuitBreaker, out var routePermit))
                {
                    continue;
                }

                attemptedRoutes++;
                TransparentProxyCacheLease cacheLease = default;
                var cacheDecision = _responseCachePolicy.CreateDecision(
                    config,
                    route,
                    streamRequested,
                    method,
                    pathAndQuery,
                    requestBody,
                    requestedModel);
                cacheKey = cacheDecision.CacheKey;
                if (cacheDecision.CanUseCache)
                {
                    if (_responseCache.TryGetResponse(cacheKey, config.CacheTtlSeconds, out var cachedRouteResponse))
                    {
                        await WriteCacheHitAsync(route, cachedRouteResponse);
                        return;
                    }

                    cacheLease = await _responseCache.AcquireResponseLeaseAsync(cacheKey, serverCancellationToken);
                    if (_responseCache.TryGetResponse(cacheKey, config.CacheTtlSeconds, out cachedRouteResponse))
                    {
                        cacheLease.Dispose();
                        await WriteCacheHitAsync(route, cachedRouteResponse);
                        return;
                    }

                    EmitLog(new TransparentProxyLogEntry(
                        DateTimeOffset.Now,
                        "CACHE",
                        method,
                        pathAndQuery,
                        route.Name,
                        0,
                        GetElapsedMilliseconds(startedAt),
                        $"Local response cache miss; key {cacheDecision.KeyPreview}, scope {cacheDecision.ScopeId}.",
                        requestedModel ?? string.Empty,
                        requestId,
                        "cache",
                        $"miss:{cacheDecision.KeyPreview}"));
                }
                else if (config.EnableCache)
                {
                    EmitLog(new TransparentProxyLogEntry(
                        DateTimeOffset.Now,
                        "CACHE",
                        method,
                        pathAndQuery,
                        route.Name,
                        0,
                        GetElapsedMilliseconds(startedAt),
                        $"Local response cache bypass: {cacheDecision.BypassReasonLabel}.",
                        requestedModel ?? string.Empty,
                        requestId,
                        "cache",
                        cacheDecision.BypassReason));
                }

                TransparentProxyUpstreamAttempt attempt;
                try
                {
                    attempt = await TryProxyToRouteAsync(
                        context,
                        config,
                        route,
                        routePermit,
                        method,
                        pathAndQuery,
                        requestBody,
                        cacheKey,
                        streamRequested,
                        config.EnableFallback && index < attempts - 1,
                        requestId,
                        requestAttemptSummaries,
                        sessionKey,
                        serverCancellationToken);
                }
                finally
                {
                    cacheLease.Dispose();
                }

                lastAttempt = attempt;
                if (attempt.DeliveredToClient)
                {
                    BindSessionRoute(config, sessionKey, route.Id);
                    var elapsed = GetElapsedMilliseconds(startedAt);
                    TrackLatency(elapsed);
                    if (attempt.StatusCode >= 200 && attempt.StatusCode < 500)
                    {
                        Interlocked.Increment(ref _successRequests);
                    }
                    else
                    {
                        Interlocked.Increment(ref _failedRequests);
                    }

                    PublishMetrics();
                    return;
                }

                if (!config.EnableFallback)
                {
                    break;
                }

                Interlocked.Increment(ref _fallbackRequests);
            }

            Interlocked.Increment(ref _failedRequests);
            var status = lastAttempt?.StatusCode ?? 502;
            var message = attemptedRoutes == 0
                ? "All upstream routes are currently limited by half-open probes."
                : lastAttempt?.Message ?? "All upstream routes are unavailable.";
            var totalElapsed = GetElapsedMilliseconds(startedAt);
            TrackLatency(totalElapsed);
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "ERROR", method, pathAndQuery, lastAttempt?.RouteName ?? "-", status, totalElapsed, message, requestedModel ?? string.Empty, requestId, string.Empty, string.Join(" > ", requestAttemptSummaries)));
            PublishMetrics();
            await WriteJsonErrorAsync(context, status, "relaybench_upstream_unavailable", message, serverCancellationToken);
        }
        finally
        {
            concurrencyGate.Release();
            Interlocked.Decrement(ref _activeRequests);
            PublishMetrics();
        }
    }

}
