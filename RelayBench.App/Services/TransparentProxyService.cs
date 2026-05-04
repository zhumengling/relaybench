using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

public sealed class TransparentProxyService : IAsyncDisposable
{
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(3);

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailers",
        "transfer-encoding",
        "upgrade",
        "host",
        "content-length"
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly object _syncRoot = new();
    private readonly TransparentProxyResponseCacheService _responseCache = new();
    private readonly TransparentProxyProtocolTranslatorService _protocolTranslator = new();
    private readonly TransparentProxyRoutePolicyService _routePolicy = new();
    private readonly TransparentProxyTokenTelemetryService _tokenTelemetry = new();
    private readonly TransparentProxyModelCatalogService _modelCatalog = new();
    private readonly TransparentProxyCircuitBreakerService _circuitBreaker = new();
    private readonly TransparentProxyRouteHealthStore _routeHealthStore = new();
    private readonly List<long> _latencies = [];
    private readonly Dictionary<string, TransparentProxyRouteRuntimeState> _routeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransparentProxySessionRouteBinding> _sessionRouteBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HttpClient> _routeHttpClients = new(StringComparer.OrdinalIgnoreCase);
    private int _roundRobinCursor;

    private HttpListener? _listener;
    private HttpClient? _httpClient;
    private CancellationTokenSource? _serverCancellationSource;
    private CancellationTokenSource? _acceptCancellationSource;
    private Task? _acceptLoopTask;
    private SemaphoreSlim? _concurrencyGate;
    private TransparentProxyServerConfig? _config;
    private DateTimeOffset _rateLimitWindowStart = DateTimeOffset.UtcNow;
    private int _rateLimitWindowCount;
    private int _activeRequests;
    private int _totalRequests;
    private int _successRequests;
    private int _failedRequests;
    private int _fallbackRequests;
    private int _cacheHits;
    private int _rateLimitedRequests;

    public event EventHandler<TransparentProxyLogEntry>? LogEmitted;

    public event EventHandler<TransparentProxyMetricsSnapshot>? MetricsChanged;

    public bool IsRunning => _listener?.IsListening == true;

    public int ClearCache()
    {
        var count = _responseCache.Clear();
        count += _protocolTranslator.PromptSessionCacheStats.Entries;
        _protocolTranslator.ClearPromptSessionCache();
        PublishMetrics();
        return count;
    }

    public void UpdateRouteProtocols(IReadOnlyList<TransparentProxyRoute> routes)
    {
        if (routes.Count == 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            var config = _config;
            if (config is null || config.Routes.Count == 0)
            {
                return;
            }

            var byId = routes.ToDictionary(static route => route.Id, StringComparer.OrdinalIgnoreCase);
            var updatedRoutes = config.Routes
                .Select(route => byId.TryGetValue(route.Id, out var hydrated) ? hydrated : route)
                .ToArray();
            _config = config with { Routes = updatedRoutes };
            _responseCache.ClearModelsList();
            foreach (var route in updatedRoutes)
            {
                if (_routeStates.TryGetValue(route.Id, out var state))
                {
                    state.ApplyProtocol(route);
                }
            }
        }

        PublishMetrics();
    }

    public void ResetTokenTelemetry()
    {
        _tokenTelemetry.Reset();
        PublishMetrics();
    }

    public bool ResetRouteCircuit(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId))
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (!_routeStates.TryGetValue(routeId, out var state))
            {
                return false;
            }

            _circuitBreaker.Reset(state);
            _routeHealthStore.Reset(routeId);
        }

        PublishMetrics();
        return true;
    }

    public async Task StartAsync(TransparentProxyServerConfig config)
    {
        if (config.Routes.Count == 0)
        {
            throw new InvalidOperationException("At least one upstream route is required.");
        }

        await StopAsync();

        _config = config;
        _responseCache.ClearMemory();
        _concurrencyGate = new SemaphoreSlim(Math.Max(1, config.MaxConcurrency), Math.Max(1, config.MaxConcurrency));

        lock (_syncRoot)
        {
            _latencies.Clear();
            _routeStates.Clear();
            var persistedHealth = _routeHealthStore.Load(config.Routes.Select(static route => route.Id));
            foreach (var route in config.Routes)
            {
                var state = new TransparentProxyRouteRuntimeState(route);
                if (persistedHealth.TryGetValue(route.Id, out var snapshot))
                {
                    state.ApplyHealthSnapshot(snapshot);
                    route.CircuitOpenUntil = state.CircuitOpenUntil;
                }

                _routeStates[route.Id] = state;
            }

            _rateLimitWindowStart = DateTimeOffset.UtcNow;
            _rateLimitWindowCount = 0;
            _activeRequests = 0;
            _totalRequests = 0;
            _successRequests = 0;
            _failedRequests = 0;
            _fallbackRequests = 0;
            _cacheHits = 0;
            _rateLimitedRequests = 0;
            _roundRobinCursor = 0;
            _sessionRouteBindings.Clear();
        }

        _tokenTelemetry.Reset();

        _httpClient = CreateHttpClient(config, outboundProxy: string.Empty);

        _serverCancellationSource = new CancellationTokenSource();
        _acceptCancellationSource = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{config.Port}/");
        _listener.Start();

        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_acceptCancellationSource.Token));
        EmitLog(new TransparentProxyLogEntry(
            DateTimeOffset.Now,
            "INFO",
            "-",
            "/",
            "-",
            200,
            0,
            $"Transparent proxy started, listening on http://127.0.0.1:{config.Port}/v1"));
        PublishMetrics();
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        var cancellationSource = _serverCancellationSource;
        var acceptCancellationSource = _acceptCancellationSource;

        _acceptCancellationSource = null;
        _serverCancellationSource = null;
        _listener = null;

        if (cancellationSource is not null)
        {
            var drained = await WaitForActiveRequestsToDrainAsync(GracefulShutdownTimeout);
            if (!drained)
            {
                EmitLog(new TransparentProxyLogEntry(
                    DateTimeOffset.Now,
                    "WARN",
                    "-",
                    "/",
                    "-",
                    499,
                    0,
                    $"Transparent proxy shutdown waited more than {GracefulShutdownTimeout.TotalSeconds:0.#} seconds; cancelling remaining requests."));
                await cancellationSource.CancelAsync();
                await WaitForActiveRequestsToDrainAsync(TimeSpan.FromMilliseconds(300));
            }
        }

        if (listener is not null)
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch
            {
                // Listener shutdown races with GetContextAsync during normal stop.
            }
        }

        if (acceptCancellationSource is not null)
        {
            await acceptCancellationSource.CancelAsync();
        }

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask;
            }
            catch
            {
                // Stop should be best-effort and not surface background loop cancellation.
            }
        }

        _acceptLoopTask = null;

        if (listener is not null)
        {
            try
            {
                listener.Close();
            }
            catch
            {
                // Listener shutdown races with GetContextAsync during normal stop.
            }
        }

        _httpClient?.Dispose();
        _httpClient = null;
        lock (_syncRoot)
        {
            foreach (var routeClient in _routeHttpClients.Values)
            {
                routeClient.Dispose();
            }

            _routeHttpClients.Clear();
        }

        _concurrencyGate?.Dispose();
        _concurrencyGate = null;
        PersistRouteHealthSnapshot();

        if (cancellationSource is not null)
        {
            cancellationSource.Dispose();
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "INFO", "-", "/", "-", 200, 0, "Transparent proxy stopped."));
            PublishMetrics();
        }

        acceptCancellationSource?.Dispose();
    }

    private async Task<bool> WaitForActiveRequestsToDrainAsync(TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Interlocked.CompareExchange(ref _activeRequests, 0, 0) > 0)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            await Task.Delay(50);
        }

        return true;
    }

    private HttpClient ResolveHttpClientForRoute(TransparentProxyRoute route, TransparentProxyServerConfig config)
    {
        var outboundProxy = route.OutboundProxy.Trim();
        if (string.IsNullOrWhiteSpace(outboundProxy))
        {
            return _httpClient ?? throw new InvalidOperationException("Transparent proxy HTTP client is not ready.");
        }

        var key = $"{outboundProxy}|tls:{config.IgnoreTlsErrors}|timeout:{config.UpstreamTimeoutSeconds}|concurrency:{config.MaxConcurrency}";
        lock (_syncRoot)
        {
            if (_routeHttpClients.TryGetValue(key, out var client))
            {
                return client;
            }

            client = CreateHttpClient(config, outboundProxy);
            _routeHttpClients[key] = client;
            return client;
        }
    }

    private static HttpClient CreateHttpClient(TransparentProxyServerConfig config, string outboundProxy)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(15, Math.Max(5, config.UpstreamTimeoutSeconds))),
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = Math.Max(16, config.MaxConcurrency * 2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        ConfigureOutboundProxy(handler, outboundProxy);

        if (config.IgnoreTlsErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static void ConfigureOutboundProxy(SocketsHttpHandler handler, string outboundProxy)
    {
        var value = outboundProxy.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (string.Equals(value, "direct", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            handler.UseProxy = false;
            handler.Proxy = null;
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var proxyUri) ||
            string.IsNullOrWhiteSpace(proxyUri.Host))
        {
            return;
        }

        if (!string.Equals(proxyUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(proxyUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(proxyUri.Scheme, "socks5", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(proxyUri.Scheme, "socks5h", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        handler.UseProxy = true;
        handler.Proxy = new WebProxy(proxyUri);
    }

    public async ValueTask DisposeAsync()
        => await StopAsync();

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                var listener = _listener;
                if (listener is null || !listener.IsListening)
                {
                    return;
                }

                context = await listener.GetContextAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (Exception ex)
            {
                EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "ERROR", "-", "/", "-", 500, 0, $"Listener failed: {ex.Message}"));
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken serverCancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var method = context.Request.HttpMethod;
        var pathAndQuery = context.Request.RawUrl ?? "/";
        var requestId = $"rb-{Guid.NewGuid():N}";

        AddCorsHeaders(context.Response);

        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(context, 204, string.Empty, "text/plain", serverCancellationToken);
            return;
        }

        if (pathAndQuery.StartsWith("/relaybench/health", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonResponseAsync(context, 200, BuildHealthPayload(), serverCancellationToken);
            return;
        }

        if (pathAndQuery.StartsWith("/relaybench/metrics", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonResponseAsync(context, 200, CreateMetricsSnapshot(), serverCancellationToken);
            return;
        }

        var config = _config;
        var client = _httpClient;
        if (config is null || client is null || config.Routes.Count == 0)
        {
            await WriteJsonErrorAsync(context, 503, "transparent_proxy_not_ready", "Transparent proxy has no configured upstream routes.", serverCancellationToken);
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
            var requestedModel = TryReadRequestModel(requestBody);
            var streamRequested = IsStreamingRequest(context.Request, requestBody);
            _responseCache.PruneExpiredResponses(config.CacheTtlSeconds);
            var cacheKey = string.Empty;

            if (IsModelsListRequest(method, pathAndQuery))
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
            var sessionKey = BuildSessionAffinityKey(context.Request, requestBody);
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
                TryTrackResponseBodyTokens(cachedResponse.Body);
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
                if (config.EnableCache &&
                    !streamRequested &&
                    TransparentProxyResponseCacheService.TryBuildResponseCacheKey(
                        method,
                        pathAndQuery,
                        requestBody,
                        route.CacheScopeId,
                        requestedModel,
                        out cacheKey,
                        out _))
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

    private async Task<TransparentProxyUpstreamAttempt> TryProxyToRouteAsync(
        HttpListenerContext context,
        TransparentProxyServerConfig config,
        TransparentProxyRoute route,
        TransparentProxyRoutePermit routePermit,
        string method,
        string pathAndQuery,
        byte[] requestBody,
        string cacheKey,
        bool streamRequested,
        bool allowRouteFallback,
        string requestId,
        List<string> requestAttemptSummaries,
        CancellationToken serverCancellationToken)
    {
        var routeStopwatch = Stopwatch.StartNew();
        using CancellationTokenSource attemptCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        attemptCancellationSource.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.UpstreamTimeoutSeconds)));

        return await TryProxyToRouteWithRetryAsync(
            context,
            config,
            route,
            routePermit,
            method,
            pathAndQuery,
            requestBody,
            cacheKey,
            streamRequested,
            allowRouteFallback,
            requestId,
            requestAttemptSummaries,
            routeStopwatch,
            attemptCancellationSource);
    }

    private async Task<TransparentProxyUpstreamAttempt> TryProxyToRouteWithRetryAsync(
        HttpListenerContext context,
        TransparentProxyServerConfig config,
        TransparentProxyRoute route,
        TransparentProxyRoutePermit routePermit,
        string method,
        string pathAndQuery,
        byte[] requestBody,
        string cacheKey,
        bool streamRequested,
        bool allowRouteFallback,
        string requestId,
        List<string> requestAttemptSummaries,
        Stopwatch routeStopwatch,
        CancellationTokenSource attemptCancellationSource)
    {
        var routeClient = ResolveHttpClientForRoute(route, config);
        var maxRequestRetries = ResolveRouteRequestRetry(route, config);
        var lastLogModel = string.Empty;
        TransparentProxyUpstreamAttempt? lastProtocolAttempt = null;
        var preparedRequests = _protocolTranslator.BuildPreparedUpstreamRequests(
            method,
            pathAndQuery,
            requestBody,
            route,
            streamRequested);

        for (var protocolIndex = 0; protocolIndex < preparedRequests.Count; protocolIndex++)
        {
            var prepared = preparedRequests[protocolIndex];
            var logModel = FormatLogModel(prepared.ResponseModel, prepared.UpstreamModel);
            lastLogModel = logModel;

            if (IsRouteModelCooling(route.Id, prepared.UpstreamModel, out var modelCoolingUntil))
            {
                lastProtocolAttempt = new TransparentProxyUpstreamAttempt(false, route.Name, 429, "Model is cooling down.");
                EmitLog(new TransparentProxyLogEntry(
                    DateTimeOffset.Now,
                    "WARN",
                    method,
                    pathAndQuery,
                    route.Name,
                    429,
                    routeStopwatch.ElapsedMilliseconds,
                    $"Model cooldown until {modelCoolingUntil.ToLocalTime():HH:mm:ss}, trying next candidate.",
                    logModel,
                    requestId,
                    prepared.WireApi,
                    string.Join(" > ", requestAttemptSummaries)));
                continue;
            }

            for (var sendAttempt = 0; ; sendAttempt++)
            {
                using var upstreamRequest = TransparentProxyUpstreamRequestFactory.Create(
                    context.Request,
                    method,
                    prepared.UpstreamUrl,
                    route,
                    prepared.Body,
                    prepared.WireApi,
                    prepared.ExtraHeaders);

                HttpResponseMessage upstreamResponse;
                try
                {
                    upstreamResponse = await routeClient.SendAsync(
                        upstreamRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        attemptCancellationSource.Token);
                }
                catch (OperationCanceledException)
                {
                    MarkRouteFailure(route, 504, routeStopwatch.ElapsedMilliseconds, routePermit, modelName: lastLogModel, config: config);
                    EmitLog(new TransparentProxyLogEntry(
                        DateTimeOffset.Now,
                        "WARN",
                        method,
                        pathAndQuery,
                        route.Name,
                        504,
                        routeStopwatch.ElapsedMilliseconds,
                        "Upstream timed out, trying fallback.",
                        lastLogModel,
                        requestId,
                        prepared.WireApi,
                        string.Join(" > ", requestAttemptSummaries)));
                    return new TransparentProxyUpstreamAttempt(false, route.Name, 504, "Upstream timed out.");
                }
                catch (Exception ex) when (sendAttempt < maxRequestRetries)
                {
                    var wait = ResolveRetryDelay(null, sendAttempt, config, route);
                    requestAttemptSummaries.Add($"{route.Name}/{DisplayWireApi(prepared.WireApi)}:connect-retry");
                    EmitLog(new TransparentProxyLogEntry(
                        DateTimeOffset.Now,
                        "WARN",
                        method,
                        pathAndQuery,
                        route.Name,
                        502,
                        routeStopwatch.ElapsedMilliseconds,
                        $"Connection failed: {ProbeTraceRedactor.RedactText(ex.Message)}. Retrying in {wait.TotalMilliseconds:0} ms.",
                        logModel,
                        requestId,
                        prepared.WireApi,
                        string.Join(" > ", requestAttemptSummaries)));
                    await Task.Delay(wait, attemptCancellationSource.Token);
                    continue;
                }
                catch (Exception ex)
                {
                    MarkRouteFailure(route, 502, routeStopwatch.ElapsedMilliseconds, routePermit, modelName: lastLogModel, config: config);
                    var safeMessage = ProbeTraceRedactor.RedactText(ex.Message);
                    EmitLog(new TransparentProxyLogEntry(
                        DateTimeOffset.Now,
                        "WARN",
                        method,
                        pathAndQuery,
                        route.Name,
                        502,
                        routeStopwatch.ElapsedMilliseconds,
                        $"Connection failed: {safeMessage}",
                        lastLogModel,
                        requestId,
                        prepared.WireApi,
                        string.Join(" > ", requestAttemptSummaries)));
                    return new TransparentProxyUpstreamAttempt(false, route.Name, 502, $"Connection failed: {safeMessage}");
                }

                using (upstreamResponse)
                {
                    var statusCode = (int)upstreamResponse.StatusCode;
                    requestAttemptSummaries.Add($"{route.Name}/{DisplayWireApi(prepared.WireApi)}:{statusCode}");
                    var hasNextProtocol = protocolIndex < preparedRequests.Count - 1;
                    if (ShouldTryNextWireApi(statusCode, prepared.WireApi, hasNextProtocol))
                    {
                        lastProtocolAttempt = new TransparentProxyUpstreamAttempt(false, route.Name, statusCode, $"Upstream {DisplayWireApi(prepared.WireApi)} is unavailable.");
                        EmitLog(new TransparentProxyLogEntry(
                            DateTimeOffset.Now,
                            "WARN",
                            method,
                            pathAndQuery,
                            route.Name,
                            statusCode,
                            routeStopwatch.ElapsedMilliseconds,
                            $"{DisplayWireApi(prepared.WireApi)} returned {statusCode}, trying next protocol.",
                            logModel,
                            requestId,
                            prepared.WireApi,
                            string.Join(" > ", requestAttemptSummaries)));
                        break;
                    }

                    if (ShouldFallback(statusCode))
                    {
                        var retryAfter = ResolveRetryAfter(upstreamResponse.Headers.RetryAfter);
                        if (ShouldTryNextModelCandidate(statusCode) &&
                            HasLaterPreparedModelCandidate(preparedRequests, protocolIndex, prepared.UpstreamModel))
                        {
                            MarkRouteModelFailureOnly(
                                route,
                                statusCode,
                                retryAfter,
                                prepared.UpstreamModel,
                                config);
                            EmitLog(new TransparentProxyLogEntry(
                                DateTimeOffset.Now,
                                "WARN",
                                method,
                                pathAndQuery,
                                route.Name,
                                statusCode,
                                routeStopwatch.ElapsedMilliseconds,
                                "Upstream model is cooling, trying next alias-pool candidate.",
                                logModel,
                                requestId,
                                prepared.WireApi,
                                string.Join(" > ", requestAttemptSummaries)));
                            break;
                        }

                        if (sendAttempt < maxRequestRetries && ShouldRetryStatus(statusCode))
                        {
                            var wait = ResolveRetryDelay(retryAfter, sendAttempt, config, route);
                            EmitLog(new TransparentProxyLogEntry(
                                DateTimeOffset.Now,
                                "WARN",
                                method,
                                pathAndQuery,
                                route.Name,
                                statusCode,
                                routeStopwatch.ElapsedMilliseconds,
                                $"Upstream returned {statusCode}, retrying in {wait.TotalMilliseconds:0} ms.",
                                logModel,
                                requestId,
                                prepared.WireApi,
                                string.Join(" > ", requestAttemptSummaries)));
                            await Task.Delay(wait, attemptCancellationSource.Token);
                            continue;
                        }

                        MarkRouteFailure(
                            route,
                            statusCode,
                            routeStopwatch.ElapsedMilliseconds,
                            routePermit,
                            retryAfter,
                            prepared.UpstreamModel,
                            config);
                        if (allowRouteFallback)
                        {
                            EmitLog(new TransparentProxyLogEntry(
                                DateTimeOffset.Now,
                                "WARN",
                                method,
                                pathAndQuery,
                                route.Name,
                                statusCode,
                                routeStopwatch.ElapsedMilliseconds,
                                "Upstream returned fallback status, switching route.",
                                logModel,
                                requestId,
                                prepared.WireApi,
                                string.Join(" > ", requestAttemptSummaries)));
                            return new TransparentProxyUpstreamAttempt(false, route.Name, statusCode, "Upstream returned fallback status.");
                        }
                    }
                    else
                    {
                        MarkRouteSuccess(route, statusCode, routeStopwatch.ElapsedMilliseconds, routePermit, prepared.UpstreamModel);
                    }

                    await CopyResponseToClientAsync(
                        context,
                        upstreamResponse,
                        statusCode,
                        cacheKey,
                        streamRequested,
                        config,
                        prepared.WireApi,
                        prepared.ResponseModel,
                        logModel,
                        prepared.NormalizeToChatCompletions,
                        prepared.IsToolExchange,
                        prepared.PreferJsonStreamExtraction,
                        attemptCancellationSource.Token);
                    EmitLog(new TransparentProxyLogEntry(
                        DateTimeOffset.Now,
                        "INFO",
                        method,
                        pathAndQuery,
                        route.Name,
                        statusCode,
                        routeStopwatch.ElapsedMilliseconds,
                        $"Routed to {route.Name} using {DisplayWireApi(prepared.WireApi)}.",
                        logModel,
                        requestId,
                        prepared.WireApi,
                        string.Join(" > ", requestAttemptSummaries)));
                    return new TransparentProxyUpstreamAttempt(true, route.Name, statusCode, "Forwarded.");
                }
            }
        }

        MarkRouteFailure(route, lastProtocolAttempt?.StatusCode ?? 502, routeStopwatch.ElapsedMilliseconds, routePermit, modelName: lastLogModel, config: config);
        return lastProtocolAttempt ?? new TransparentProxyUpstreamAttempt(false, route.Name, 502, "No available upstream protocol.");
    }

    private async Task CopyResponseToClientAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        int statusCode,
        string cacheKey,
        bool streamRequested,
        TransparentProxyServerConfig config,
        string wireApi,
        string responseModel,
        string logModel,
        bool normalizeToChatCompletions,
        bool preserveToolStreamEvents,
        bool preferJsonStreamExtraction,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        AddCorsHeaders(context.Response);
        CopyResponseHeaders(upstreamResponse, context.Response);

        var contentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        if (normalizeToChatCompletions &&
            statusCode >= 200 &&
            statusCode < 300)
        {
            if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                ClearTransformedResponseHeaders(context.Response);
                if (preserveToolStreamEvents)
                {
                    context.Response.ContentType = contentType;
                    await CopyDirectResponseWithTokenTelemetryAsync(
                        upstreamResponse,
                        context.Response.OutputStream,
                        statusCode,
                        contentType,
                        streamRequested: true,
                        config,
                        cancellationToken);
                    context.Response.OutputStream.Close();
                    return;
                }

                await CopyNormalizedChatStreamAsync(
                    context,
                    upstreamResponse,
                    responseModel,
                    wireApi,
                    preferJsonStreamExtraction,
                    cancellationToken);
                return;
            }

            ClearTransformedResponseHeaders(context.Response);
            await CopyNormalizedChatJsonAsync(
                context,
                upstreamResponse,
                statusCode,
                cacheKey,
                config,
                responseModel,
                logModel,
                wireApi,
                cancellationToken);
            return;
        }

        context.Response.ContentType = contentType;

        var canCache = config.EnableCache &&
                       !streamRequested &&
                       !string.IsNullOrWhiteSpace(cacheKey) &&
                       statusCode >= 200 &&
                       statusCode < 300 &&
                       !contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (canCache)
        {
            var bytes = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            TryTrackResponseBodyTokens(bytes);
            if (bytes.Length <= config.CacheMaxBytes)
            {
                _responseCache.StoreResponse(
                    cacheKey,
                    statusCode,
                    contentType,
                    bytes,
                    NormalizeLogModel(logModel),
                    config.CacheMaxBytes);
            }
        }
        else
        {
            await CopyDirectResponseWithTokenTelemetryAsync(
                upstreamResponse,
                context.Response.OutputStream,
                statusCode,
                contentType,
                streamRequested,
                config,
                cancellationToken);
        }

        context.Response.OutputStream.Close();
    }

    private async Task CopyDirectResponseWithTokenTelemetryAsync(
        HttpResponseMessage upstreamResponse,
        Stream outputStream,
        int statusCode,
        string contentType,
        bool streamRequested,
        TransparentProxyServerConfig config,
        CancellationToken cancellationToken)
    {
        if (statusCode >= 200 &&
            statusCode < 300 &&
            IsEventStreamContentType(contentType))
        {
            await CopySseResponseWithTokenTelemetryAsync(
                upstreamResponse,
                outputStream,
                cancellationToken);
            return;
        }

        if (ShouldCaptureResponseBodyForTokenTelemetry(statusCode, contentType))
        {
            await CopyResponseBodyAndCaptureTokenTelemetryAsync(
                upstreamResponse,
                outputStream,
                Math.Max(256 * 1024, config.CacheMaxBytes),
                cancellationToken);
            return;
        }

        if (statusCode >= 200 && statusCode < 300 && streamRequested)
        {
            await CopySseResponseWithTokenTelemetryAsync(
                upstreamResponse,
                outputStream,
                cancellationToken);
            return;
        }

        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(outputStream, cancellationToken);
    }

    private static bool IsEventStreamContentType(string contentType)
        => contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldCaptureResponseBodyForTokenTelemetry(int statusCode, string contentType)
    {
        if (statusCode < 200 || statusCode >= 300)
        {
            return false;
        }

        return contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CopyResponseBodyAndCaptureTokenTelemetryAsync(
        HttpResponseMessage upstreamResponse,
        Stream outputStream,
        int maxTelemetryBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream telemetryBody = new();
        var canCaptureTelemetry = true;
        var buffer = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await outputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (!canCaptureTelemetry)
            {
                continue;
            }

            if (telemetryBody.Length + read > maxTelemetryBytes)
            {
                canCaptureTelemetry = false;
                telemetryBody.SetLength(0);
                continue;
            }

            telemetryBody.Write(buffer, 0, read);
        }

        if (canCaptureTelemetry && telemetryBody.Length > 0)
        {
            TryTrackResponseBodyTokens(telemetryBody.ToArray());
        }
    }

    private async Task CopySseResponseWithTokenTelemetryAsync(
        HttpResponseMessage upstreamResponse,
        Stream outputStream,
        CancellationToken cancellationToken)
    {
        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[8192];
        var charBuffer = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        StringBuilder lineBuilder = new();

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await outputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await outputStream.FlushAsync(cancellationToken);

            var charCount = decoder.GetChars(buffer, 0, read, charBuffer, 0, flush: false);
            TrackSseTokenTelemetry(charBuffer.AsSpan(0, charCount), lineBuilder);
        }

        var finalCharCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuffer, 0, flush: true);
        TrackSseTokenTelemetry(charBuffer.AsSpan(0, finalCharCount), lineBuilder);
        if (lineBuilder.Length > 0)
        {
            TrackSseTokenTelemetryLine(lineBuilder.ToString());
        }
    }

    private void TrackSseTokenTelemetry(ReadOnlySpan<char> text, StringBuilder lineBuilder)
    {
        foreach (var character in text)
        {
            if (character == '\n')
            {
                TrackSseTokenTelemetryLine(lineBuilder.ToString());
                lineBuilder.Clear();
                continue;
            }

            if (character != '\r')
            {
                lineBuilder.Append(character);
            }
        }
    }

    private void TrackSseTokenTelemetryLine(string line)
    {
        if (!ChatSseParser.TryReadDataLine(line, out var data) ||
            ChatSseParser.IsDone(data))
        {
            return;
        }

        TrackOutputTextTokens(ChatSseParser.TryExtractDelta(data));
        TrackPromptCacheTokens(data);
    }

    private async Task CopyNormalizedChatJsonAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        int statusCode,
        string cacheKey,
        TransparentProxyServerConfig config,
        string responseModel,
        string logModel,
        string wireApi,
        CancellationToken cancellationToken)
    {
        var upstreamBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var upstreamText = Encoding.UTF8.GetString(upstreamBytes);
        TrackPromptCacheTokens(upstreamText);
        var normalized = _protocolTranslator.TryBuildNormalizedChatJson(
            upstreamBytes,
            responseModel,
            wireApi);
        if (normalized is null)
        {
            context.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
            context.Response.ContentLength64 = upstreamBytes.LongLength;
            await context.Response.OutputStream.WriteAsync(upstreamBytes, cancellationToken);
            TryTrackResponseBodyTokens(upstreamBytes);
            context.Response.OutputStream.Close();
            return;
        }

        var normalizedBytes = normalized.Body;
        const string normalizedContentType = "application/json; charset=utf-8";
        context.Response.ContentType = normalizedContentType;
        context.Response.ContentLength64 = normalizedBytes.LongLength;
        await context.Response.OutputStream.WriteAsync(normalizedBytes, cancellationToken);
        TrackOutputTextTokens(normalized.AssistantText);
        if (config.EnableCache &&
            !string.IsNullOrWhiteSpace(cacheKey) &&
            statusCode >= 200 &&
            statusCode < 300 &&
            normalizedBytes.Length <= config.CacheMaxBytes)
        {
            _responseCache.StoreResponse(
                cacheKey,
                statusCode,
                normalizedContentType,
                normalizedBytes,
                NormalizeLogModel(logModel),
                config.CacheMaxBytes);
        }

        context.Response.OutputStream.Close();
    }

    private async Task CopyNormalizedChatStreamAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        string responseModel,
        string wireApi,
        bool preferJsonStreamExtraction,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;
        var model = string.IsNullOrWhiteSpace(responseModel) ? "relaybench-proxy" : responseModel.Trim();
        var streamId = $"chatcmpl-relaybench-{Guid.NewGuid():N}";
        var wroteDone = false;
        var wroteTerminalChunk = false;
        StringBuilder assistantText = new();

        async Task WriteBufferedJsonChunkIfNeededAsync()
        {
            if (!preferJsonStreamExtraction || assistantText.Length == 0)
            {
                return;
            }

            var original = assistantText.ToString();
            var extracted = TryExtractFirstJsonObject(original);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return;
            }

            assistantText.Clear();
            assistantText.Append(extracted);
            var chunk = _protocolTranslator.BuildOpenAiChatCompletionChunk(extracted, model, wireApi, streamId);
            await WriteSseDataAsync(context.Response.OutputStream, chunk, cancellationToken);
        }

        async Task WriteTerminalChunkAsync()
        {
            if (wroteTerminalChunk)
            {
                return;
            }

            await WriteBufferedJsonChunkIfNeededAsync();
            var terminalChunk = _protocolTranslator.BuildOpenAiChatCompletionTerminalChunk(
                model,
                wireApi,
                streamId,
                assistantText.ToString());
            await WriteSseDataAsync(context.Response.OutputStream, terminalChunk, cancellationToken);
            wroteTerminalChunk = true;
        }

        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (!ChatSseParser.TryReadDataLine(line, out var data))
            {
                continue;
            }

            if (ChatSseParser.IsDone(data))
            {
                await WriteTerminalChunkAsync();
                await WriteSseDataAsync(context.Response.OutputStream, "[DONE]", cancellationToken);
                wroteDone = true;
                break;
            }

            var delta = ChatSseParser.TryExtractDelta(data);
            if (string.IsNullOrEmpty(delta))
            {
                TrackPromptCacheTokens(data);
                continue;
            }

            var chunk = _protocolTranslator.BuildOpenAiChatCompletionChunk(delta, model, wireApi, streamId);
            assistantText.Append(delta);
            if (!preferJsonStreamExtraction)
            {
                await WriteSseDataAsync(context.Response.OutputStream, chunk, cancellationToken);
            }

            TrackOutputTextTokens(delta);
            TrackPromptCacheTokens(data);
        }

        await WriteTerminalChunkAsync();
        if (!wroteDone)
        {
            await WriteSseDataAsync(context.Response.OutputStream, "[DONE]", cancellationToken);
        }

        context.Response.OutputStream.Close();
    }

    private static string? TryExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
                continue;
            }

            if (character != '}')
            {
                continue;
            }

            depth--;
            if (depth != 0)
            {
                continue;
            }

            var candidate = text[start..(index + 1)].Trim();
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static async Task WriteSseDataAsync(Stream outputStream, string data, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await outputStream.WriteAsync(bytes, cancellationToken);
        await outputStream.FlushAsync(cancellationToken);
    }

    private void TryTrackResponseBodyTokens(byte[] body)
    {
        if (_tokenTelemetry.TrackResponseBody(body))
        {
            PublishMetrics();
        }
    }

    private void TrackOutputTextTokens(string? text)
    {
        if (_tokenTelemetry.TrackOutputText(text))
        {
            PublishMetrics();
        }
    }

    private void TrackPromptCacheTokens(string? json)
    {
        if (_tokenTelemetry.TrackPromptCache(json))
        {
            PublishMetrics();
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstreamResponse, HttpListenerResponse response)
    {
        foreach (var header in upstreamResponse.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                TrySetResponseHeader(response, header.Key, header.Value);
            }
        }

        foreach (var header in upstreamResponse.Content.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key) &&
                !string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                TrySetResponseHeader(response, header.Key, header.Value);
            }
        }
    }

    private static void ClearTransformedResponseHeaders(HttpListenerResponse response)
    {
        foreach (var headerName in new[] { "Content-Encoding", "Content-MD5", "Content-Range" })
        {
            try
            {
                response.Headers.Remove(headerName);
            }
            catch
            {
            }
        }
    }

    private static void TrySetResponseHeader(HttpListenerResponse response, string name, IEnumerable<string> values)
    {
        try
        {
            response.Headers[name] = string.Join(",", values);
        }
        catch
        {
            // Some framework-managed headers cannot be set directly; keep proxying.
        }
    }

    private static bool CanRouteServeModel(TransparentProxyRoute route, string? requestedModel)
    {
        var model = requestedModel?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return true;
        }

        model = StripRoutePrefix(model, route);
        if (route.ExcludedModelPatterns.Any(pattern => WildcardMatch(model, pattern)))
        {
            return false;
        }

        return route.Models.Count == 0 ||
               route.ModelMappings.Any(mapping =>
                   string.Equals(mapping.Name, model, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mapping.EffectiveAlias, model, StringComparison.OrdinalIgnoreCase));
    }

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

    private static string StripRoutePrefix(string model, TransparentProxyRoute route)
    {
        var prefix = route.Prefix.Trim().Trim('/');
        return !string.IsNullOrWhiteSpace(prefix) &&
               model.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(prefix.Length + 1)..]
            : model;
    }

    private static string? BuildSessionAffinityKey(HttpListenerRequest request, byte[] requestBody)
    {
        var explicitSession =
            request.Headers["X-Session-ID"] ??
            request.Headers["X-Conversation-ID"] ??
            request.Headers["OpenAI-Conversation-ID"] ??
            TryReadStringProperty(requestBody, "conversation_id") ??
            TryReadStringProperty(requestBody, "thread_id") ??
            TryReadStringProperty(requestBody, "session_id");
        if (!string.IsNullOrWhiteSpace(explicitSession))
        {
            return explicitSession.Trim();
        }

        var model = TryReadRequestModel(requestBody);
        if (TryBuildConversationFingerprint(requestBody, out var conversationFingerprint))
        {
            return string.IsNullOrWhiteSpace(model)
                ? conversationFingerprint
                : $"{model.Trim()}:{conversationFingerprint}";
        }

        var bodyHash = SHA256.HashData(requestBody);
        return string.IsNullOrWhiteSpace(model)
            ? Convert.ToHexString(bodyHash[..8])
            : $"{model.Trim()}:{Convert.ToHexString(bodyHash[..6])}";
    }

    private static bool TryBuildConversationFingerprint(byte[] requestBody, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (requestBody.Length == 0 || requestBody.Length > 512 * 1024)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            List<string> parts = [];
            if (document.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray().Take(4))
                {
                    var role = TryReadStringProperty(message, "role") ?? "-";
                    var content = ReadConversationContentPreview(message, "content");
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        parts.Add($"{role}:{content}");
                    }
                }
            }
            else if (document.RootElement.TryGetProperty("input", out var input))
            {
                if (input.ValueKind == JsonValueKind.String)
                {
                    parts.Add("input:" + TruncateFingerprintPart(input.GetString()));
                }
                else if (input.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in input.EnumerateArray().Take(4))
                    {
                        var role = TryReadStringProperty(item, "role") ?? "-";
                        var content = ReadConversationContentPreview(item, "content");
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            parts.Add($"{role}:{content}");
                        }
                    }
                }
            }

            if (parts.Count == 0)
            {
                return false;
            }

            var source = string.Join("\u001F", parts);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
            fingerprint = Convert.ToHexString(hash[..10]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadConversationContentPreview(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return TruncateFingerprintPart(content.GetString());
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        List<string> parts = [];
        foreach (var item in content.EnumerateArray().Take(4))
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                parts.Add(TruncateFingerprintPart(item.GetString()));
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                parts.Add(TruncateFingerprintPart(text.GetString()));
            }
        }

        return string.Join(" ", parts.Where(static item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string TruncateFingerprintPart(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private IReadOnlyList<TransparentProxyRoute> BuildCandidateRoutes(
        TransparentProxyServerConfig config,
        string? requestedModel,
        string? sessionKey)
    {
        if (!config.EnableFallback || config.Routes.Count <= 1)
        {
            return config.Routes
                .Where(route => CanRouteServeModel(route, requestedModel))
                .Take(1)
                .ToArray();
        }

        var now = DateTimeOffset.UtcNow;
        lock (_syncRoot)
        {
            PruneSessionRouteBindings(now);
            List<TransparentProxyRoutePolicyCandidate> available = [];
            for (var index = 0; index < config.Routes.Count; index++)
            {
                var route = config.Routes[index];
                if (!CanRouteServeModel(route, requestedModel))
                {
                    continue;
                }

                if (!_routeStates.TryGetValue(route.Id, out var state) ||
                    _circuitBreaker.IsRouteAvailable(state, now))
                {
                    available.Add(new TransparentProxyRoutePolicyCandidate(route, index, state));
                }
            }

            var boundRouteId = string.Equals(TransparentProxyRouteStrategies.Normalize(config.RouteStrategy), TransparentProxyRouteStrategies.SessionAffinity, StringComparison.Ordinal) &&
                               !string.IsNullOrWhiteSpace(sessionKey) &&
                               TryGetSessionRouteBinding(config, sessionKey, now, out var sessionRouteId)
                ? sessionRouteId
                : null;
            return _routePolicy.OrderCandidateRoutes(config, available, boundRouteId, ref _roundRobinCursor);
        }
    }

    private void BindSessionRoute(TransparentProxyServerConfig config, string? sessionKey, string routeId)
    {
        if (!string.Equals(TransparentProxyRouteStrategies.Normalize(config.RouteStrategy), TransparentProxyRouteStrategies.SessionAffinity, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sessionKey))
        {
            return;
        }

        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            PruneSessionRouteBindings(now);
            if (_sessionRouteBindings.Count > 2048)
            {
                _sessionRouteBindings.Clear();
            }

            _sessionRouteBindings[sessionKey] = new TransparentProxySessionRouteBinding(
                routeId,
                now.AddSeconds(Math.Max(30, config.SessionAffinityTtlSeconds)));
        }
    }

    private bool TryGetSessionRouteBinding(
        TransparentProxyServerConfig config,
        string sessionKey,
        DateTimeOffset now,
        out string routeId)
    {
        routeId = string.Empty;
        if (!_sessionRouteBindings.TryGetValue(sessionKey, out var binding))
        {
            return false;
        }

        if (binding.ExpiresAt <= now)
        {
            _sessionRouteBindings.Remove(sessionKey);
            return false;
        }

        routeId = binding.RouteId;
        _sessionRouteBindings[sessionKey] = binding with
        {
            ExpiresAt = now.AddSeconds(Math.Max(30, config.SessionAffinityTtlSeconds))
        };
        return true;
    }

    private void PruneSessionRouteBindings(DateTimeOffset now)
    {
        if (_sessionRouteBindings.Count == 0)
        {
            return;
        }

        foreach (var key in _sessionRouteBindings
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _sessionRouteBindings.Remove(key);
        }
    }

    private bool TryAcquireRoutePermit(
        TransparentProxyRoute route,
        bool bypassCircuitBreaker,
        out TransparentProxyRoutePermit routePermit)
    {
        routePermit = new TransparentProxyRoutePermit(route.Id, UsedHalfOpenPermit: false);
        if (bypassCircuitBreaker)
        {
            return true;
        }

        lock (_syncRoot)
        {
            _routeStates.TryGetValue(route.Id, out var state);
            return _circuitBreaker.TryAcquirePermit(state, route.Id, bypassCircuitBreaker, out routePermit);
        }
    }

    private void MarkRouteSuccess(
        TransparentProxyRoute route,
        int statusCode,
        long latencyMs,
        TransparentProxyRoutePermit routePermit,
        string? modelName = null)
    {
        var recovered = false;
        TransparentProxyRouteRuntimeState? snapshot = null;
        lock (_syncRoot)
        {
            if (!_routeStates.TryGetValue(route.Id, out var state))
            {
                return;
            }

            recovered = _circuitBreaker.RecordSuccess(
                state,
                statusCode,
                latencyMs,
                routePermit) == TransparentProxyCircuitEvent.Recovered;
            state.RecordModelSuccess(modelName);
            route.CircuitOpenUntil = DateTimeOffset.MinValue;
            snapshot = state;
        }

        if (snapshot is not null)
        {
            _routeHealthStore.Save(snapshot);
        }

        if (recovered)
        {
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "INFO", "-", "/", route.Name, statusCode, latencyMs, "Half-open probe succeeded; route recovered."));
        }
    }

    private void MarkRouteFailure(
        TransparentProxyRoute route,
        int statusCode,
        long latencyMs,
        TransparentProxyRoutePermit routePermit,
        TimeSpan? retryAfter = null,
        string? modelName = null,
        TransparentProxyServerConfig? config = null)
    {
        TransparentProxyCircuitEvent circuitEvent = TransparentProxyCircuitEvent.None;
        TransparentProxyRouteRuntimeState? snapshot = null;
        lock (_syncRoot)
        {
            if (!_routeStates.TryGetValue(route.Id, out var state))
            {
                return;
            }

            circuitEvent = _circuitBreaker.RecordFailure(
                state,
                statusCode,
                latencyMs,
                routePermit,
                retryAfter);
            state.RecordModelFailure(
                modelName,
                statusCode,
                retryAfter,
                Math.Max(15, route.ModelCooldownSeconds ?? config?.ModelCooldownSeconds ?? 120));
            route.CircuitOpenUntil = state.CircuitOpenUntil;
            snapshot = state;
        }

        if (snapshot is not null)
        {
            _routeHealthStore.Save(snapshot);
        }

        if (circuitEvent.Opened)
        {
            var halfOpenFailed = circuitEvent.HalfOpenFailed;
            var retryAt = circuitEvent.RetryAt;
            var message = halfOpenFailed
                ? $"Half-open probe failed; route is open until {retryAt.ToLocalTime():HH:mm:ss}."
                : $"Consecutive failures or high error rate; route is open until {retryAt.ToLocalTime():HH:mm:ss}.";
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", "-", "/", route.Name, statusCode, latencyMs, message));
        }
    }

    private void MarkRouteModelFailureOnly(
        TransparentProxyRoute route,
        int statusCode,
        TimeSpan? retryAfter,
        string? modelName,
        TransparentProxyServerConfig config)
    {
        lock (_syncRoot)
        {
            if (_routeStates.TryGetValue(route.Id, out var state))
            {
                state.RecordModelFailure(
                    modelName,
                    statusCode,
                    retryAfter,
                    Math.Max(15, route.ModelCooldownSeconds ?? config.ModelCooldownSeconds));
            }
        }
    }

    private static TimeSpan? ResolveRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
        }

        return null;
    }

    private bool IsRouteModelCooling(string routeId, string? modelName, out DateTimeOffset cooldownUntil)
    {
        cooldownUntil = DateTimeOffset.MinValue;
        if (string.IsNullOrWhiteSpace(routeId) || string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (!_routeStates.TryGetValue(routeId, out var state))
            {
                return false;
            }

            return state.IsModelCooling(modelName, DateTimeOffset.UtcNow, out cooldownUntil);
        }
    }

    private static int ResolveRouteRequestRetry(TransparentProxyRoute route, TransparentProxyServerConfig config)
        => route.RequestRetry is >= 0
            ? Math.Clamp(route.RequestRetry.Value, 0, 5)
            : Math.Clamp(config.RequestRetry, 0, 5);

    private static bool ShouldRetryStatus(int statusCode)
        => statusCode is 408 or 425 or 429 or 500 or 502 or 503 or 504;

    private static bool ShouldTryNextModelCandidate(int statusCode)
        => statusCode is 429 or 503 || statusCode >= 500;

    private static bool HasLaterPreparedModelCandidate(
        IReadOnlyList<TransparentProxyPreparedRequest> preparedRequests,
        int currentIndex,
        string currentModel)
        => preparedRequests
            .Skip(currentIndex + 1)
            .Any(item => !string.Equals(
                item.UpstreamModel?.Trim(),
                currentModel?.Trim(),
                StringComparison.OrdinalIgnoreCase));

    private static TimeSpan ResolveRetryDelay(
        TimeSpan? retryAfter,
        int sendAttempt,
        TransparentProxyServerConfig config,
        TransparentProxyRoute route)
    {
        var maxIntervalSeconds = route.MaxRetryIntervalSeconds is > 0
            ? route.MaxRetryIntervalSeconds.Value
            : config.MaxRetryIntervalSeconds;
        var maxInterval = TimeSpan.FromSeconds(Math.Clamp(maxIntervalSeconds, 1, 60));
        if (retryAfter is { } explicitDelay && explicitDelay > TimeSpan.Zero)
        {
            return explicitDelay <= maxInterval ? explicitDelay : maxInterval;
        }

        var backoffMs = Math.Min(maxInterval.TotalMilliseconds, 220d * Math.Pow(2d, Math.Clamp(sendAttempt, 0, 5)));
        var jitterMs = Random.Shared.Next(20, 120);
        return TimeSpan.FromMilliseconds(Math.Min(maxInterval.TotalMilliseconds, backoffMs + jitterMs));
    }

    private bool TryAcquireRateSlot(TransparentProxyServerConfig config)
    {
        if (config.RateLimitPerMinute <= 0)
        {
            return true;
        }

        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _rateLimitWindowStart).TotalSeconds >= 60)
            {
                _rateLimitWindowStart = now;
                _rateLimitWindowCount = 0;
            }

            if (_rateLimitWindowCount >= config.RateLimitPerMinute)
            {
                return false;
            }

            _rateLimitWindowCount++;
            return true;
        }
    }

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

    private static bool IsStreamingRequest(HttpListenerRequest request, byte[] body)
    {
        if (request.RawUrl?.Contains("stream=true", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("stream", out var streamProperty) &&
                   streamProperty.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
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

    private static bool IsModelsListRequest(string method, string pathAndQuery)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = ExtractRelativePath(pathAndQuery);
        return relativePath.Equals("models", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals("v1/models", StringComparison.OrdinalIgnoreCase);
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

    private static bool ShouldFallback(int statusCode)
        => statusCode is 408 or 409 or 425 or 429 || statusCode >= 500;

    private static bool ShouldTryNextWireApi(int statusCode, string wireApi, bool hasNextProtocol)
    {
        if (!hasNextProtocol ||
            string.Equals(wireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal) ||
            statusCode is 401 or 403 or 429)
        {
            return false;
        }

        return statusCode is 400 or 404 or 405 or 415;
    }

    private static string DisplayWireApi(string wireApi)
        => wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic Messages",
            _ => "OpenAI Chat"
        };

    private static long GetElapsedMilliseconds(long startedAt)
        => (long)(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

    private void TrackLatency(long latencyMs)
    {
        lock (_syncRoot)
        {
            _latencies.Add(latencyMs);
            if (_latencies.Count > 300)
            {
                _latencies.RemoveRange(0, _latencies.Count - 300);
            }
        }
    }

    private TransparentProxyMetricsSnapshot CreateMetricsSnapshot()
    {
        lock (_syncRoot)
        {
            var orderedLatencies = _latencies.OrderBy(static item => item).ToArray();
            var p50 = Percentile(orderedLatencies, 0.50);
            var p95 = Percentile(orderedLatencies, 0.95);
            var tokenSnapshot = _tokenTelemetry.CreateSnapshot();
            var cacheStats = _responseCache.Stats;
            var promptSessionStats = _protocolTranslator.PromptSessionCacheStats;
            var routes = _routeStates.Values
                .Select(static state => state.ToSnapshot())
                .ToArray();

            return new TransparentProxyMetricsSnapshot(
                IsRunning,
                _config?.Port ?? 0,
                _activeRequests,
                _totalRequests,
                _successRequests,
                _failedRequests,
                _fallbackRequests,
                _cacheHits,
                _rateLimitedRequests,
                p50,
                p95,
                cacheStats.ResponseEntries + cacheStats.ModelListEntries + promptSessionStats.Entries,
                routes,
                tokenSnapshot.TotalOutputTokens,
                tokenSnapshot.TokensPerSecond,
                tokenSnapshot.LastTokenActivityAt,
                tokenSnapshot.PromptCacheTokens,
                cacheStats.ResponseEntries,
                cacheStats.ModelListEntries,
                cacheStats.Hits,
                cacheStats.Misses,
                cacheStats.Stores,
                cacheStats.Evictions,
                promptSessionStats.Entries,
                promptSessionStats.Hits,
                promptSessionStats.Misses);
        }
    }

    private object BuildHealthPayload()
        => new
        {
            status = IsRunning ? "ok" : "stopped",
            port = _config?.Port ?? 0,
            routes = _config?.Routes.Count ?? 0,
            metrics = CreateMetricsSnapshot()
        };

    private void PersistRouteHealthSnapshot()
    {
        TransparentProxyRouteRuntimeState[] states;
        lock (_syncRoot)
        {
            states = _routeStates.Values.ToArray();
        }

        if (states.Length > 0)
        {
            _routeHealthStore.SaveAll(states);
        }
    }

    private void PublishMetrics()
        => MetricsChanged?.Invoke(this, CreateMetricsSnapshot());

    private void EmitLog(TransparentProxyLogEntry entry)
    {
        var safeEntry = new TransparentProxyLogEntry(
            entry.Timestamp,
            entry.Level,
            entry.Method,
            ProbeTraceRedactor.RedactUrl(entry.Path),
            ProbeTraceRedactor.RedactText(entry.RouteName),
            entry.StatusCode,
            entry.ElapsedMs,
            ProbeTraceRedactor.RedactText(entry.Message),
            ProbeTraceRedactor.RedactText(entry.ModelName),
            ProbeTraceRedactor.RedactText(entry.RequestId),
            ProbeTraceRedactor.RedactText(entry.WireApi),
            ProbeTraceRedactor.RedactText(entry.AttemptSummary));
        LogEmitted?.Invoke(this, safeEntry);
    }

    private static long Percentile(IReadOnlyList<long> orderedValues, double percentile)
    {
        if (orderedValues.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Clamp(Math.Ceiling(orderedValues.Count * percentile) - 1, 0, orderedValues.Count - 1);
        return orderedValues[index];
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Headers"] = "authorization, content-type, x-api-key, anthropic-version, anthropic-beta, openai-beta, idempotency-key, session_id";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
        response.Headers["X-RelayBench-Proxy"] = "transparent";
    }

    private static async Task WriteCachedResponseAsync(HttpListenerContext context, TransparentProxyCachedResponse cachedResponse, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = cachedResponse.StatusCode;
        context.Response.ContentType = cachedResponse.ContentType;
        context.Response.Headers["X-RelayBench-Cache"] = "HIT";
        context.Response.ContentLength64 = cachedResponse.Body.LongLength;
        await context.Response.OutputStream.WriteAsync(cachedResponse.Body, cancellationToken);
        context.Response.OutputStream.Close();
    }

    private static async Task WriteJsonErrorAsync(HttpListenerContext context, int statusCode, string code, string message, CancellationToken cancellationToken)
        => await WriteJsonResponseAsync(context, statusCode, new
        {
            error = new
            {
                code,
                message,
                type = "relaybench_transparent_proxy_error"
            }
        }, cancellationToken);

    private static async Task WriteJsonResponseAsync(HttpListenerContext context, int statusCode, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, CompactJsonOptions);
        await WriteTextResponseAsync(context, statusCode, json, "application/json; charset=utf-8", cancellationToken);
    }

    private static async Task WriteJsonBytesResponseAsync(HttpListenerContext context, int statusCode, byte[] payload, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["X-RelayBench-Cache"] = "HIT";
        context.Response.ContentLength64 = payload.LongLength;
        if (payload.Length > 0)
        {
            await context.Response.OutputStream.WriteAsync(payload, cancellationToken);
        }

        context.Response.OutputStream.Close();
    }

    private static async Task WriteTextResponseAsync(HttpListenerContext context, int statusCode, string text, string contentType, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.LongLength;
        if (bytes.Length > 0)
        {
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        }

        context.Response.OutputStream.Close();
    }
}

public sealed class TransparentProxyRoute
{
    public TransparentProxyRoute(
        string id,
        string name,
        string baseUrl,
        string apiKey,
        string model,
        string? preferredWireApi = null,
        bool? chatCompletionsSupported = null,
        bool? responsesSupported = null,
        bool? anthropicMessagesSupported = null,
        DateTimeOffset? protocolCheckedAt = null,
        IReadOnlyList<string>? models = null,
        int priority = 0,
        string? prefix = null,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyList<TransparentProxyModelMapping>? modelMappings = null,
        IReadOnlyList<string>? excludedModelPatterns = null,
        string? outboundProxy = null,
        int? requestRetry = null,
        int? maxRetryIntervalSeconds = null,
        int? modelCooldownSeconds = null,
        string? payloadRulesText = null)
    {
        Id = id;
        Name = name;
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        Model = model;
        ModelMappings = NormalizeModelMappings(modelMappings, models, model);
        Models = ModelMappings
            .Select(static mapping => mapping.Name)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Priority = Math.Max(0, priority);
        Prefix = prefix?.Trim().Trim('/') ?? string.Empty;
        Headers = new Dictionary<string, string>(
            headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        ExcludedModelPatterns = (excludedModelPatterns ?? Array.Empty<string>())
            .Select(static item => item?.Trim() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OutboundProxy = outboundProxy?.Trim() ?? string.Empty;
        RequestRetry = requestRetry;
        MaxRetryIntervalSeconds = maxRetryIntervalSeconds;
        ModelCooldownSeconds = modelCooldownSeconds;
        PayloadRulesText = payloadRulesText?.Trim() ?? string.Empty;
        CacheScopeId = BuildCacheScopeId(Id, BaseUrl, Prefix, ModelMappings, PayloadRulesText);
        PreferredWireApi = ProxyWireApiProbeService.NormalizeWireApi(preferredWireApi);
        ChatCompletionsSupported = chatCompletionsSupported;
        ResponsesSupported = responsesSupported;
        AnthropicMessagesSupported = anthropicMessagesSupported;
        ProtocolCheckedAt = protocolCheckedAt;
    }

    public string Id { get; }

    public string Name { get; }

    public string BaseUrl { get; }

    public string ApiKey { get; }

    public string Model { get; }

    public IReadOnlyList<string> Models { get; }

    public IReadOnlyList<TransparentProxyModelMapping> ModelMappings { get; }

    public int Priority { get; }

    public string Prefix { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public IReadOnlyList<string> ExcludedModelPatterns { get; }

    public string OutboundProxy { get; }

    public int? RequestRetry { get; }

    public int? MaxRetryIntervalSeconds { get; }

    public int? ModelCooldownSeconds { get; }

    public string PayloadRulesText { get; }

    public string CacheScopeId { get; }

    public string? PreferredWireApi { get; }

    public bool? ChatCompletionsSupported { get; }

    public bool? ResponsesSupported { get; }

    public bool? AnthropicMessagesSupported { get; }

    public DateTimeOffset? ProtocolCheckedAt { get; }

    public TransparentProxyRoute WithProtocol(
        string? preferredWireApi,
        bool? chatCompletionsSupported,
        bool? responsesSupported,
        bool? anthropicMessagesSupported,
        DateTimeOffset? protocolCheckedAt)
        => new(
            Id,
            Name,
            BaseUrl,
            ApiKey,
            Model,
            preferredWireApi,
            chatCompletionsSupported,
            responsesSupported,
            anthropicMessagesSupported,
            protocolCheckedAt,
            Models,
            Priority,
            Prefix,
            Headers,
            ModelMappings,
            ExcludedModelPatterns,
            OutboundProxy,
            RequestRetry,
            MaxRetryIntervalSeconds,
            ModelCooldownSeconds,
            PayloadRulesText)
        {
            CircuitOpenUntil = CircuitOpenUntil
        };

    public TransparentProxyRoute WithModels(IReadOnlyList<string> models)
        => new(
            Id,
            Name,
            BaseUrl,
            ApiKey,
            Model,
            PreferredWireApi,
            ChatCompletionsSupported,
            ResponsesSupported,
            AnthropicMessagesSupported,
            ProtocolCheckedAt,
            models,
            Priority,
            Prefix,
            Headers,
            MergeModelMappings(models, ModelMappings),
            ExcludedModelPatterns,
            OutboundProxy,
            RequestRetry,
            MaxRetryIntervalSeconds,
            ModelCooldownSeconds,
            PayloadRulesText)
        {
            CircuitOpenUntil = CircuitOpenUntil
        };

    internal DateTimeOffset CircuitOpenUntil { get; set; } = DateTimeOffset.MinValue;

    private static IReadOnlyList<TransparentProxyModelMapping> NormalizeModelMappings(
        IReadOnlyList<TransparentProxyModelMapping>? mappings,
        IReadOnlyList<string>? models,
        string fallbackModel)
    {
        List<TransparentProxyModelMapping> normalized = [];
        foreach (var mapping in mappings ?? Array.Empty<TransparentProxyModelMapping>())
        {
            AddModelMapping(normalized, mapping.Name, mapping.Alias);
        }

        foreach (var model in models ?? Array.Empty<string>())
        {
            AddModelMapping(normalized, model, string.Empty);
        }

        AddModelMapping(normalized, fallbackModel, string.Empty);
        return normalized.ToArray();
    }

    private static IReadOnlyList<TransparentProxyModelMapping> MergeModelMappings(
        IReadOnlyList<string> models,
        IReadOnlyList<TransparentProxyModelMapping> existing)
    {
        var aliasByName = existing
            .Where(static mapping => !string.IsNullOrWhiteSpace(mapping.Name))
            .GroupBy(static mapping => mapping.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Alias, StringComparer.OrdinalIgnoreCase);
        List<TransparentProxyModelMapping> merged = [];
        foreach (var model in models)
        {
            var name = model?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            AddModelMapping(merged, name, aliasByName.TryGetValue(name, out var alias) ? alias : name);
        }

        return merged.ToArray();
    }

    private static void AddModelMapping(List<TransparentProxyModelMapping> mappings, string? name, string? alias)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedName) ||
            mappings.Any(item => string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var normalizedAlias = alias?.Trim() ?? string.Empty;
        mappings.Add(new TransparentProxyModelMapping(normalizedName, normalizedAlias));
    }

    private static string BuildCacheScopeId(
        string id,
        string baseUrl,
        string prefix,
        IReadOnlyList<TransparentProxyModelMapping> mappings,
        string payloadRulesText)
    {
        StringBuilder builder = new();
        builder.Append(id).Append('\u001F')
            .Append(baseUrl.Trim()).Append('\u001F')
            .Append(prefix.Trim()).Append('\u001F')
            .Append(payloadRulesText.Trim()).Append('\u001F');
        foreach (var mapping in mappings.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.Alias, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(mapping.Name.Trim()).Append("=>").Append(mapping.Alias.Trim()).Append('\u001F');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return $"{id}:{Convert.ToHexString(hash[..6])}";
    }
}

public sealed record TransparentProxyModelMapping(string Name, string Alias)
{
    public string EffectiveAlias
        => string.IsNullOrWhiteSpace(Alias) ? Name.Trim() : Alias.Trim();
}

public static class TransparentProxyRouteStrategies
{
    public const string Smart = "smart";
    public const string Priority = "priority";
    public const string RoundRobin = "round-robin";
    public const string FillFirst = "fill-first";
    public const string LowestLatency = "lowest-latency";
    public const string SessionAffinity = "session-affinity";

    public static string Normalize(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Priority => Priority,
            RoundRobin => RoundRobin,
            "roundrobin" => RoundRobin,
            FillFirst => FillFirst,
            "fillfirst" => FillFirst,
            LowestLatency => LowestLatency,
            "lowestlatency" => LowestLatency,
            SessionAffinity => SessionAffinity,
            "sessionaffinity" => SessionAffinity,
            _ => Smart
        };
}

public sealed record TransparentProxyServerConfig(
    int Port,
    IReadOnlyList<TransparentProxyRoute> Routes,
    int RateLimitPerMinute,
    int MaxConcurrency,
    bool EnableFallback,
    bool EnableCache,
    int CacheTtlSeconds,
    bool RewriteModel,
    bool IgnoreTlsErrors,
    int UpstreamTimeoutSeconds)
{
    public int CacheMaxBytes { get; init; } = 2 * 1024 * 1024;

    public string RouteStrategy { get; init; } = TransparentProxyRouteStrategies.Smart;

    public int RequestRetry { get; init; } = 1;

    public int MaxRetryIntervalSeconds { get; init; } = 8;

    public int SessionAffinityTtlSeconds { get; init; } = 30 * 60;

    public int ModelCooldownSeconds { get; init; } = 120;
}

public sealed record TransparentProxyLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Method,
    string Path,
    string RouteName,
    int StatusCode,
    long ElapsedMs,
    string Message,
    string ModelName = "-",
    string RequestId = "",
    string WireApi = "",
    string AttemptSummary = "");

public sealed record TransparentProxyMetricsSnapshot(
    bool IsRunning,
    int Port,
    int ActiveRequests,
    int TotalRequests,
    int SuccessRequests,
    int FailedRequests,
    int FallbackRequests,
    int CacheHits,
    int RateLimitedRequests,
    long P50LatencyMs,
    long P95LatencyMs,
    int CacheEntryCount,
    IReadOnlyList<TransparentProxyRouteMetrics> Routes,
    long TotalOutputTokens,
    double TokensPerSecond,
    DateTimeOffset? LastTokenActivityAt,
    long PromptCacheTokens = 0,
    int ResponseCacheEntryCount = 0,
    int ModelListCacheEntryCount = 0,
    long ResponseCacheHits = 0,
    long ResponseCacheMisses = 0,
    long ResponseCacheStores = 0,
    long ResponseCacheEvictions = 0,
    int PromptSessionCacheEntryCount = 0,
    long PromptSessionCacheHits = 0,
    long PromptSessionCacheMisses = 0);

public sealed record TransparentProxyRouteMetrics(
    string Id,
    string Name,
    int Sent,
    int Success,
    int Failed,
    int LastStatusCode,
    long LastLatencyMs,
    int ConsecutiveFailures,
    int ConsecutiveSuccesses,
    string CircuitState,
    DateTimeOffset CircuitOpenUntil,
    DateTimeOffset LastSeenAt,
    string? PreferredWireApi,
    bool? ChatCompletionsSupported,
    bool? ResponsesSupported,
    bool? AnthropicMessagesSupported,
    DateTimeOffset? ProtocolCheckedAt);

internal sealed record TransparentProxyCachedResponse(
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    int StatusCode,
    string ContentType,
    byte[] Body,
    string ModelName,
    int HitCount = 0);

internal sealed record TransparentProxyCachedModelsList(
    string Key,
    byte[] Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    DateTimeOffset ExpiresAt);

internal sealed record TransparentProxyUpstreamAttempt(bool DeliveredToClient, string RouteName, int StatusCode, string Message);

internal sealed record TransparentProxySessionRouteBinding(string RouteId, DateTimeOffset ExpiresAt);

internal sealed record TransparentProxyPreparedRequest(
    string WireApi,
    string UpstreamUrl,
    byte[] Body,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    bool NormalizeToChatCompletions,
    string ResponseModel,
    string UpstreamModel,
    bool IsToolExchange,
    bool PreferJsonStreamExtraction);
