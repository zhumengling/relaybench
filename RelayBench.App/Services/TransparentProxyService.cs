using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

public sealed class TransparentProxyService : IAsyncDisposable
{
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly object _syncRoot = new();
    private readonly object _recentLogsSyncRoot = new();
    private static readonly AsyncLocal<TransparentProxyIngressContext?> CurrentIngressContext = new();
    private readonly TransparentProxyResponseCacheService _responseCache;
    private readonly TransparentProxyResponseCachePolicyService _responseCachePolicy = new();
    private readonly TransparentProxyGatewayService _gateway = new();
    private readonly TransparentProxyManagementApiService _managementApi = new();
    private readonly TransparentProxySystemProxyService _systemProxyService = new();
    private readonly TransparentProxyAppDetectorService _appDetector = new();
    private readonly TransparentProxyNetworkGuardService _networkGuard = new();
    private readonly TransparentProxyPortInspectorService _portInspector = new();
    private readonly TransparentProxyCaptureArtifactStore _captureArtifactStore = new();
    private readonly TransparentProxyCliEnvironmentService _cliEnvironmentService = new();
    private readonly TransparentProxyLaunchWrapperService _launchWrapperService = new();
    private readonly TransparentProxyCodexConfigService _codexConfigService = new();
    private readonly TransparentProxyClaudeConfigService _claudeConfigService = new();
    private readonly TransparentProxyVsCodeSettingsService _vsCodeSettingsService = new();
    private readonly TransparentProxyProtocolTranslatorService _protocolTranslator = new();
    private readonly TransparentProxyResponseNormalizationService _responseNormalizer = new();
    private readonly TransparentProxyRoutePolicyService _routePolicy = new();
    private readonly TransparentProxySchedulerService _scheduler;
    private readonly TransparentProxyResponseForwarderService _responseForwarder;
    private readonly TransparentProxyCooldownService _cooldown = new();
    private readonly TransparentProxyTokenTelemetryService _tokenTelemetry = new();
    private readonly TransparentProxyModelCatalogService _modelCatalog = new();
    private readonly TransparentProxyModelRegistryService _modelRegistry = new();
    private readonly TransparentProxySseFramer _sseFramer = new();
    private readonly TransparentProxySecurityFilterService _securityFilter = new();
    private readonly TransparentProxyRequestClassifier _requestClassifier = new();
    private readonly TransparentProxySessionAffinityKeyService _sessionAffinityKeyService = new();
    private readonly TransparentProxyRetryOrchestrator _retryOrchestrator = new();
    private readonly TransparentProxyCircuitBreakerService _circuitBreaker = new();
    private readonly TransparentProxyRouteHealthStore _routeHealthStore = new();
    private readonly TransparentProxyMetricsService _metrics = new();
    private readonly Dictionary<string, TransparentProxyRouteRuntimeState> _routeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransparentProxySessionRouteBinding> _sessionRouteBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HttpClient> _routeHttpClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransparentProxyIngressRuntimeState> _ingressStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<TransparentProxyLogEntry> _recentLogs = new();
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

    public TransparentProxyService()
        : this(null)
    {
    }

    internal TransparentProxyService(TransparentProxyResponseCacheService? responseCache)
    {
        _responseCache = responseCache ?? new TransparentProxyResponseCacheService();
        _scheduler = new TransparentProxySchedulerService(_routePolicy);
        _responseForwarder = new TransparentProxyResponseForwarderService(
            _responseCache,
            _responseNormalizer,
            _sseFramer,
            _tokenTelemetry,
            PublishMetrics);
        _tokenTelemetry.UsageEvents.UsageEmitted += OnTransparentProxyUsageEmitted;
    }

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
        ResetIngressTokenTotals();
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
        await StopAsync();

        _config = config;
        _responseCache.ClearMemory();
        _concurrencyGate = new SemaphoreSlim(Math.Max(1, config.MaxConcurrency), Math.Max(1, config.MaxConcurrency));
        _metrics.Reset();

        lock (_syncRoot)
        {
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
        var requestId = _gateway.CreateRequestId();
        CurrentIngressContext.Value = ClassifyTransparentProxyIngress(context.Request, pathAndQuery);

        AddCorsHeaders(context.Response);

        if (_gateway.IsCorsPreflight(method))
        {
            await WriteTextResponseAsync(context, 204, string.Empty, "text/plain", serverCancellationToken);
            return;
        }

        var isManagementEndpoint = _gateway.TryResolveManagementEndpoint(pathAndQuery, out var managementEndpoint);
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

        var config = _config;
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
        var maxRequestRetries = _retryOrchestrator.ResolveRouteRequestRetry(route, config);
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
                    var wait = _retryOrchestrator.ResolveRetryDelay(null, sendAttempt, config, route);
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
                    if (_retryOrchestrator.ShouldTryNextWireApi(statusCode, prepared.WireApi, hasNextProtocol))
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

                    if (_retryOrchestrator.ShouldFallback(statusCode))
                    {
                        var retryAfter = _retryOrchestrator.ResolveRetryAfter(upstreamResponse.Headers.RetryAfter);
                        if (_retryOrchestrator.ShouldTryNextModelCandidate(statusCode) &&
                            _retryOrchestrator.HasLaterPreparedModelCandidate(preparedRequests, protocolIndex, prepared.UpstreamModel))
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

                        if (sendAttempt < maxRequestRetries && _retryOrchestrator.ShouldRetryStatus(statusCode))
                        {
                            var wait = _retryOrchestrator.ResolveRetryDelay(retryAfter, sendAttempt, config, route);
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

                        if (statusCode == 404 && !string.IsNullOrWhiteSpace(prepared.UpstreamModel))
                        {
                            MarkRouteModelFailureOnly(
                                route,
                                statusCode,
                                retryAfter,
                                prepared.UpstreamModel,
                                config);
                            ReleaseRoutePermit(route, routePermit);
                        }
                        else
                        {
                            MarkRouteFailure(
                                route,
                                statusCode,
                                routeStopwatch.ElapsedMilliseconds,
                                routePermit,
                                retryAfter,
                                prepared.UpstreamModel,
                                config);
                        }

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
                                statusCode == 404
                                    ? "Upstream model was not found, switching route."
                                    : "Upstream returned fallback status, switching route.",
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

                    using (PushTransparentProxyUsageContext(logModel, route.Name, prepared.WireApi, "upstream"))
                    {
                        await _responseForwarder.CopyResponseToClientAsync(
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
                    }
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

    private static bool CanRouteServeModel(TransparentProxyRoute route, string? requestedModel)
        => TransparentProxySchedulerService.CanRouteServeModel(route, requestedModel);

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

    private IReadOnlyList<TransparentProxyRoute> BuildCandidateRoutes(
        TransparentProxyServerConfig config,
        string? requestedModel,
        string? sessionKey)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_syncRoot)
        {
            PruneSessionRouteBindings(now);
            var boundRouteId = string.Equals(TransparentProxyRouteStrategies.Normalize(config.RouteStrategy), TransparentProxyRouteStrategies.SessionAffinity, StringComparison.Ordinal) &&
                               !string.IsNullOrWhiteSpace(sessionKey) &&
                               TryGetSessionRouteBinding(config, sessionKey, now, out var sessionRouteId)
                ? sessionRouteId
                : null;
            return _scheduler.BuildCandidateRoutes(
                config,
                requestedModel,
                boundRouteId,
                _routeStates,
                _circuitBreaker,
                now,
                ref _roundRobinCursor);
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
            _cooldown.RecordModelSuccess(state, modelName);
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
            _cooldown.RecordModelFailure(
                state,
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
                _cooldown.RecordModelFailure(
                    state,
                    modelName,
                    statusCode,
                    retryAfter,
                    Math.Max(15, route.ModelCooldownSeconds ?? config.ModelCooldownSeconds));
            }
        }
    }

    private void ReleaseRoutePermit(TransparentProxyRoute route, TransparentProxyRoutePermit routePermit)
    {
        if (!routePermit.UsedHalfOpenPermit)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_routeStates.TryGetValue(route.Id, out var state))
            {
                state.ReleasePermit(usedHalfOpenPermit: true);
            }
        }
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

            return _cooldown.IsModelCooling(state, modelName, DateTimeOffset.UtcNow, out cooldownUntil);
        }
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

    private object BuildHealthPayload()
        => _managementApi.BuildHealthPayload(IsRunning, _config, CreateMetricsSnapshot());

    private object BuildCachePayload()
    {
        var responseStats = _responseCache.Stats;
        var promptStats = _protocolTranslator.PromptSessionCacheStats;
        return _managementApi.BuildCachePayload(
            responseStats,
            promptStats,
            _config,
            _responseCachePolicy.BuildPolicyPayload());
    }

    private object BuildUsagePayload()
    {
        var snapshot = _tokenTelemetry.CreateSnapshot();
        var events = _tokenTelemetry.UsageEvents.Snapshot(96);
        return _managementApi.BuildUsagePayload(snapshot, events);
    }

    private object BuildIngressPayload()
        => _managementApi.BuildIngressPayload(CreateMetricsSnapshot());

    private object BuildCaptureAppsPayload()
        => _managementApi.BuildCaptureAppsPayload(_appDetector.Detect(), CreateMetricsSnapshot());

    private object BuildCaptureDiagnosticsPayload()
    {
        var config = _config;
        var basePort = config?.Port ?? 17880;
        var forwardProxyPort = ResolvePacForwardProxyPort(basePort);
        var tunOptions = new TransparentProxyTunConfigOptions(
            basePort,
            forwardProxyPort,
            ResolveOffsetPort(basePort, 2, 17882),
            ResolveOffsetPort(basePort, 3, 17883),
            ResolveOffsetPort(basePort, 4, 17884));
        var tunService = new TransparentProxyTunService();
        var tunConfig = tunService.BuildMihomoConfig(tunOptions);
        var guard = _networkGuard.Inspect();
        var tunValidation = _networkGuard.ValidateMihomoConfig(tunConfig);
        return _managementApi.BuildCaptureDiagnosticsPayload(
            IsRunning,
            config,
            _appDetector.Detect(),
            guard,
            tunValidation,
            tunService.InspectResidualSession(),
            _systemProxyService.Inspect($"http://127.0.0.1:{basePort}/relaybench/pac"),
            _portInspector.Inspect(basePort),
            _cliEnvironmentService.Build(basePort),
            _launchWrapperService.ScanKnownLaunchers(),
            $"127.0.0.1:{forwardProxyPort}",
            $"127.0.0.1:{tunOptions.MixedPort}",
            $"127.0.0.1:{tunOptions.ControllerPort}");
    }

    private object BuildCaptureRecoveryPayload(bool execute)
        => _managementApi.BuildCaptureRecoveryPayload(
            execute,
            execute ? ExecuteCaptureRecovery() : PreviewCaptureRecovery());

    private object BuildLogsPayload(string pathAndQuery)
    {
        var limit = 96;
        var requestedLimit = TryReadQueryParameter(pathAndQuery, "limit");
        if (int.TryParse(requestedLimit, out var parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, 500);
        }

        return _managementApi.BuildLogsPayload(SnapshotRecentLogs(limit));
    }

    private object BuildProtocolsPayload()
        => _managementApi.BuildProtocolsPayload(_config?.Routes ?? Array.Empty<TransparentProxyRoute>());

    private static int ResolvePacForwardProxyPort(int transparentProxyPort)
        => transparentProxyPort < 65535 ? transparentProxyPort + 1 : 17881;

    private static int ResolveOffsetPort(int transparentProxyPort, int offset, int fallback)
    {
        var candidate = transparentProxyPort + offset;
        return candidate is >= 1 and <= 65535 ? candidate : fallback;
    }

    private static bool ShouldExecuteCaptureRecovery(string method)
        => string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<TransparentProxyCaptureRecoveryItem> PreviewCaptureRecovery()
    {
        var items = _captureArtifactStore
            .ScanDefaultArtifacts()
            .Select(static artifact => new TransparentProxyCaptureRecoveryItem(
                artifact.TargetId,
                artifact.DisplayName,
                artifact.BackupCount > 0 ? "ready" : "missing",
                true,
                artifact.BackupCount > 0
                    ? $"已找到 {artifact.BackupCount} 个恢复点；POST 后将使用最近一次备份恢复。"
                    : "未找到 RelayBench 接管备份；POST 时会跳过该目标。",
                artifact.Path,
                artifact.LatestBackupPath))
            .ToList();

        items.AddRange(_launchWrapperService
            .ScanKnownLaunchers()
            .Select(static artifact => new TransparentProxyCaptureRecoveryItem(
                $"launcher-{artifact.Id}",
                $"{artifact.DisplayName} 临时启动器",
                artifact.ExistingCount > 0 ? "ready" : "missing",
                true,
                artifact.ExistingCount > 0
                    ? $"已找到 {artifact.ExistingCount} 个临时启动器；POST 后将删除这些启动器。"
                    : "未发现 RelayBench 临时启动器；POST 时会跳过。",
                string.Join(";", artifact.ExistingPaths),
                null)));

        return items;
    }

    private IReadOnlyList<TransparentProxyCaptureRecoveryItem> ExecuteCaptureRecovery()
    {
        List<TransparentProxyCaptureRecoveryItem> items = [];
        try
        {
            var result = _codexConfigService.RestoreLatestBackup();
            items.Add(new(
                "codex-cli",
                "Codex CLI",
                result.Succeeded ? "restored" : "skipped",
                result.Succeeded,
                result.Summary,
                result.ConfigPath,
                result.BackupPath));
        }
        catch (Exception ex)
        {
            items.Add(new(
                "codex-cli",
                "Codex CLI",
                "failed",
                false,
                ex.Message,
                ResolveCodexConfigPath(),
                null));
        }

        try
        {
            var result = _claudeConfigService.RestoreLatestBackup();
            items.Add(new(
                "claude-cli",
                "Claude CLI",
                result.Succeeded ? "restored" : "skipped",
                result.Succeeded,
                result.Summary,
                result.SettingsPath,
                result.BackupPath));
        }
        catch (Exception ex)
        {
            items.Add(new(
                "claude-cli",
                "Claude CLI",
                "failed",
                false,
                ex.Message,
                ResolveClaudeSettingsPath(),
                null));
        }

        try
        {
            var result = _vsCodeSettingsService.RestoreLatestBackups();
            items.Add(new(
                "vs-codex",
                "VS Codex / VS Code",
                result.Succeeded ? "restored" : "skipped",
                result.Succeeded,
                result.Summary,
                string.Join(";", result.ChangedFiles),
                string.Join(";", result.BackupFiles)));
        }
        catch (Exception ex)
        {
            items.Add(new(
                "vs-codex",
                "VS Codex / VS Code",
                "failed",
                false,
                ex.Message,
                string.Join(";", ResolveVsCodeSettingsPaths()),
                null));
        }

        try
        {
            var result = _launchWrapperService.DeleteKnownLaunchers();
            items.Add(new(
                "launch-wrappers",
                "CLI 临时启动器",
                result.DeletedCount > 0 ? "restored" : "skipped",
                result.Succeeded,
                result.Summary,
                string.Join(";", result.DeletedPaths.Concat(result.FailedPaths)),
                null));
        }
        catch (Exception ex)
        {
            items.Add(new(
                "launch-wrappers",
                "CLI 临时启动器",
                "failed",
                false,
                ex.Message,
                null,
                null));
        }

        return items;
    }

    private string ResolveDefaultCaptureModel()
    {
        var config = _config;
        var model = config?.Routes
            .SelectMany(static route => route.Models)
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item));
        return string.IsNullOrWhiteSpace(model) ? "gpt-5.4" : model;
    }

    private static string ResolveCodexConfigPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    private static string ResolveClaudeSettingsPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static IReadOnlyList<string> ResolveVsCodeSettingsPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var stable = Path.Combine(appData, "Code", "User", "settings.json");
        var insiders = Path.Combine(appData, "Code - Insiders", "User", "settings.json");
        var insidersDirectory = Path.GetDirectoryName(insiders);
        return File.Exists(insiders) ||
               (!string.IsNullOrWhiteSpace(insidersDirectory) && Directory.Exists(insidersDirectory))
            ? [stable, insiders]
            : [stable];
    }

    private static double CalculateRate(long numerator, long denominator)
        => denominator <= 0
            ? 0d
            : Math.Round(numerator * 100d / denominator, 2);

    private object BuildSchedulerPayload(string pathAndQuery)
    {
        var config = _config;
        var requestedModel = TryReadQueryParameter(pathAndQuery, "model");
        var sessionKey = TryReadQueryParameter(pathAndQuery, "session");
        if (config is null)
        {
            return new
            {
                ready = false,
                model = requestedModel,
                routeCount = 0,
                candidates = Array.Empty<object>(),
                routes = Array.Empty<object>()
            };
        }

        var now = DateTimeOffset.UtcNow;
        TransparentProxyRoute[] candidates;
        Dictionary<string, TransparentProxyRouteRuntimeState> states;
        string? boundRouteId;
        var explicitPoolExists = TransparentProxySchedulerService.HasExplicitRouteModelPool(config.Routes, requestedModel);
        lock (_syncRoot)
        {
            PruneSessionRouteBindings(now);
            boundRouteId = string.Equals(TransparentProxyRouteStrategies.Normalize(config.RouteStrategy), TransparentProxyRouteStrategies.SessionAffinity, StringComparison.Ordinal) &&
                           !string.IsNullOrWhiteSpace(sessionKey) &&
                           TryGetSessionRouteBinding(config, sessionKey, now, out var sessionRouteId)
                ? sessionRouteId
                : null;
            states = new Dictionary<string, TransparentProxyRouteRuntimeState>(_routeStates, StringComparer.OrdinalIgnoreCase);
            var previewCursor = _roundRobinCursor;
            candidates = _scheduler.BuildCandidateRoutes(
                    config,
                    requestedModel,
                    boundRouteId,
                    states,
                    _circuitBreaker,
                    now,
                    ref previewCursor)
                .ToArray();
        }

        var orderByRouteId = candidates
            .Select((route, index) => (route.Id, Order: index + 1))
            .ToDictionary(static item => item.Id, static item => item.Order, StringComparer.OrdinalIgnoreCase);
        var routeRows = config.Routes
            .Select(route =>
            {
                states.TryGetValue(route.Id, out var state);
                var canServeModel = TransparentProxySchedulerService.CanRouteServeModel(route, requestedModel);
                var explicitMatch = TransparentProxySchedulerService.HasExplicitRouteModelMatch(route, requestedModel);
                var routeAvailable = state is null || _circuitBreaker.IsRouteAvailable(state, now);
                orderByRouteId.TryGetValue(route.Id, out var order);
                return new
                {
                    id = route.Id,
                    name = route.Name,
                    order = order == 0 ? (int?)null : order,
                    selected = order > 0,
                    canServeModel,
                    explicitMatch,
                    passThroughFallback = explicitPoolExists && canServeModel && !explicitMatch,
                    available = routeAvailable,
                    skipReason = ResolveSchedulerSkipReason(order, canServeModel, routeAvailable, explicitPoolExists, explicitMatch),
                    priority = route.Priority,
                    models = route.Models.Count,
                    protocol = route.PreferredWireApi,
                    circuit = state?.CircuitState.ToString() ?? "Closed",
                    circuitOpenUntil = state?.CircuitOpenUntil,
                    latencyMs = state?.LastLatencyMs ?? 0,
                    success = state?.Success ?? 0,
                    failed = state?.Failed ?? 0
                };
            })
            .ToArray();

        return new
        {
            ready = true,
            generatedAt = now,
            model = requestedModel,
            session = string.IsNullOrWhiteSpace(sessionKey) ? null : "provided",
            strategy = TransparentProxyRouteStrategies.Normalize(config.RouteStrategy),
            boundRouteId,
            explicitPoolExists,
            candidateCount = candidates.Length,
            candidates = candidates.Select((route, index) => new
            {
                order = index + 1,
                id = route.Id,
                name = route.Name,
                priority = route.Priority,
                models = route.Models.Count,
                protocol = route.PreferredWireApi
            }).ToArray(),
            routes = routeRows
        };
    }

    private static string ResolveSchedulerSkipReason(
        int order,
        bool canServeModel,
        bool routeAvailable,
        bool explicitPoolExists,
        bool explicitMatch)
    {
        if (order > 0)
        {
            return string.Empty;
        }

        if (!canServeModel)
        {
            return "model-not-served";
        }

        if (!routeAvailable)
        {
            return "circuit-open";
        }

        if (explicitPoolExists && !explicitMatch)
        {
            return "pass-through-fallback-later";
        }

        return "not-selected";
    }

    private static string? TryReadQueryParameter(string pathAndQuery, string name)
    {
        var queryStart = pathAndQuery.IndexOf('?');
        if (queryStart < 0 || queryStart == pathAndQuery.Length - 1)
        {
            return null;
        }

        foreach (var part in pathAndQuery[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            var rawName = equals >= 0 ? part[..equals] : part;
            if (!string.Equals(DecodeQueryValue(rawName), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = equals >= 0 ? part[(equals + 1)..] : string.Empty;
            var value = DecodeQueryValue(rawValue);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string DecodeQueryValue(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal)).Trim();
        }
        catch
        {
            return value.Trim();
        }
    }

    private object BuildRoutesPayload()
        => _managementApi.BuildRoutesPayload(IsRunning, _config, CreateMetricsSnapshot());

    private static string BuildApiKeyPreview(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "pass-through";
        }

        return apiKey.Length <= 8
            ? "***"
            : $"{apiKey[..Math.Min(3, apiKey.Length)]}...{apiKey[^Math.Min(4, apiKey.Length)..]}";
    }

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
        var ingressContext = CurrentIngressContext.Value ?? TransparentProxyIngressContext.Default;
        var ingressKind = string.IsNullOrWhiteSpace(entry.IngressKind) ? ingressContext.IngressKind : entry.IngressKind;
        var sourceApplication = string.IsNullOrWhiteSpace(entry.SourceApplication) ? ingressContext.SourceApplication : entry.SourceApplication;
        var captureMode = string.IsNullOrWhiteSpace(entry.CaptureMode) ? ingressContext.CaptureMode : entry.CaptureMode;
        var targetHost = string.IsNullOrWhiteSpace(entry.TargetHost) ? ingressContext.TargetHost : entry.TargetHost;
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
            ProbeTraceRedactor.RedactText(entry.AttemptSummary),
            ProbeTraceRedactor.RedactText(ingressKind),
            ProbeTraceRedactor.RedactText(sourceApplication),
            ProbeTraceRedactor.RedactText(captureMode),
            ProbeTraceRedactor.RedactText(targetHost),
            entry.WasTunnelOnly || ingressContext.WasTunnelOnly);
        AddRecentLog(safeEntry);
        UpdateIngressMetricsFromLog(safeEntry);
        LogEmitted?.Invoke(this, safeEntry);
    }

    private static TransparentProxyIngressContext ClassifyTransparentProxyIngress(HttpListenerRequest request, string pathAndQuery)
    {
        var userAgent = request.UserAgent ?? request.Headers["User-Agent"] ?? string.Empty;
        var sourceHeader = request.Headers["X-RelayBench-Source"] ?? request.Headers["X-Source-Application"] ?? string.Empty;
        var captureModeHeader = request.Headers["X-RelayBench-Capture-Mode"] ?? string.Empty;
        var targetHost = request.Headers["Host"] ?? request.UserHostName ?? string.Empty;
        var path = pathAndQuery.Split('?', 2)[0];

        if (!string.IsNullOrWhiteSpace(sourceHeader))
        {
            return new TransparentProxyIngressContext(
                "AppCapture",
                sourceHeader.Trim(),
                string.IsNullOrWhiteSpace(captureModeHeader) ? "显式 Base URL" : captureModeHeader.Trim(),
                targetHost,
                false);
        }

        if (userAgent.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            return new TransparentProxyIngressContext("UnifiedLocalEndpoint", "Claude / Anthropic 客户端", "显式 Base URL", targetHost, false);
        }

        if (userAgent.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            return new TransparentProxyIngressContext("UnifiedLocalEndpoint", "Codex 客户端", "显式 Base URL", targetHost, false);
        }

        return new TransparentProxyIngressContext("UnifiedLocalEndpoint", "本地统一出口", "显式 Base URL", targetHost, false);
    }

    private IDisposable PushTransparentProxyUsageContext(
        string modelName,
        string routeName,
        string wireApi,
        string cacheState)
    {
        var ingressContext = CurrentIngressContext.Value ?? TransparentProxyIngressContext.Default;
        return _tokenTelemetry.PushUsageContext(
            modelName,
            routeName,
            wireApi,
            cacheState,
            ingressContext.IngressKind,
            ingressContext.SourceApplication,
            ingressContext.CaptureMode);
    }

    private void OnTransparentProxyUsageEmitted(object? sender, TransparentProxyUsageEvent usageEvent)
    {
        if (usageEvent.OutputTokenDelta == 0 && usageEvent.PromptCacheTokenDelta == 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            var state = GetOrCreateIngressState(
                usageEvent.IngressKind,
                usageEvent.SourceApplication,
                usageEvent.CaptureMode);
            state.OutputTokens = Math.Max(0, state.OutputTokens + usageEvent.OutputTokenDelta);
            state.PromptCacheTokens = Math.Max(0, state.PromptCacheTokens + usageEvent.PromptCacheTokenDelta);
            state.LastTokenActivityAt = usageEvent.Timestamp;
        }
    }

    private void UpdateIngressMetricsFromLog(TransparentProxyLogEntry entry)
    {
        if (entry.StatusCode <= 0 || string.Equals(entry.Method, "-", StringComparison.Ordinal))
        {
            return;
        }

        lock (_syncRoot)
        {
            var state = GetOrCreateIngressState(entry.IngressKind, entry.SourceApplication, entry.CaptureMode);
            state.Requests++;
            if (entry.StatusCode is >= 200 and < 400)
            {
                state.Successes++;
            }
            else if (entry.StatusCode >= 400)
            {
                state.Failures++;
            }

            if (entry.WasTunnelOnly)
            {
                state.TunnelOnlyRequests++;
            }

            state.LastRequestAt = entry.Timestamp;
        }
    }

    private void ResetIngressTokenTotals()
    {
        lock (_syncRoot)
        {
            foreach (var state in _ingressStates.Values)
            {
                state.OutputTokens = 0;
                state.PromptCacheTokens = 0;
                state.LastTokenActivityAt = null;
            }
        }
    }

    private IReadOnlyList<TransparentProxyIngressMetricsSnapshot> SnapshotIngressMetrics()
    {
        lock (_syncRoot)
        {
            return _ingressStates.Values
                .OrderByDescending(static item => item.LastRequestAt ?? item.LastTokenActivityAt ?? DateTimeOffset.MinValue)
                .Select(static item => item.ToSnapshot())
                .ToArray();
        }
    }

    private TransparentProxyIngressRuntimeState GetOrCreateIngressState(
        string ingressKind,
        string sourceApplication,
        string captureMode)
    {
        var normalizedIngress = NormalizeIngressMetricPart(ingressKind, "UnifiedLocalEndpoint");
        var normalizedSource = NormalizeIngressMetricPart(sourceApplication, "本地统一出口");
        var normalizedCapture = NormalizeIngressMetricPart(captureMode, "显式 Base URL");
        var key = $"{normalizedIngress}\u001F{normalizedSource}\u001F{normalizedCapture}";
        if (_ingressStates.TryGetValue(key, out var state))
        {
            return state;
        }

        state = new TransparentProxyIngressRuntimeState(normalizedIngress, normalizedSource, normalizedCapture);
        _ingressStates[key] = state;
        return state;
    }

    private static string NormalizeIngressMetricPart(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "-", StringComparison.Ordinal)
            ? fallback
            : value.Trim();

    private void AddRecentLog(TransparentProxyLogEntry entry)
    {
        lock (_recentLogsSyncRoot)
        {
            _recentLogs.Enqueue(entry);
            while (_recentLogs.Count > 500)
            {
                _recentLogs.Dequeue();
            }
        }
    }

    private IReadOnlyList<TransparentProxyLogEntry> SnapshotRecentLogs(int limit)
    {
        lock (_recentLogsSyncRoot)
        {
            return _recentLogs
                .Reverse()
                .Take(Math.Clamp(limit, 1, 500))
                .ToArray();
        }
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Headers"] = "authorization, content-type, x-api-key, anthropic-version, anthropic-beta, openai-beta, idempotency-key, session_id, session-id, x-session-id, x-conversation-id, openai-conversation-id, x-amp-thread-id, x-client-request-id";
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
    string AttemptSummary = "",
    string IngressKind = "",
    string SourceApplication = "",
    string CaptureMode = "",
    string TargetHost = "",
    bool WasTunnelOnly = false);

internal sealed record TransparentProxyIngressContext(
    string IngressKind,
    string SourceApplication,
    string CaptureMode,
    string TargetHost,
    bool WasTunnelOnly)
{
    public static TransparentProxyIngressContext Default { get; } =
        new("UnifiedLocalEndpoint", "本地统一出口", "显式 Base URL", string.Empty, false);
}

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
    long PromptSessionCacheMisses = 0,
    IReadOnlyList<TransparentProxyModelPoolSnapshot>? ModelPools = null,
    int ResponseCacheInFlightKeys = 0,
    long ResponseCacheLeaseWaits = 0,
    IReadOnlyList<TransparentProxyUsageEvent>? RecentUsageEvents = null,
    IReadOnlyList<TransparentProxyIngressMetricsSnapshot>? Ingresses = null);

public sealed record TransparentProxyIngressMetricsSnapshot(
    string IngressKind,
    string SourceApplication,
    string CaptureMode,
    long Requests,
    long Successes,
    long Failures,
    long TunnelOnlyRequests,
    long OutputTokens,
    long PromptCacheTokens,
    DateTimeOffset? LastRequestAt,
    DateTimeOffset? LastTokenActivityAt);

internal sealed class TransparentProxyIngressRuntimeState
{
    public TransparentProxyIngressRuntimeState(
        string ingressKind,
        string sourceApplication,
        string captureMode)
    {
        IngressKind = ingressKind;
        SourceApplication = sourceApplication;
        CaptureMode = captureMode;
    }

    public string IngressKind { get; }

    public string SourceApplication { get; }

    public string CaptureMode { get; }

    public long Requests { get; set; }

    public long Successes { get; set; }

    public long Failures { get; set; }

    public long TunnelOnlyRequests { get; set; }

    public long OutputTokens { get; set; }

    public long PromptCacheTokens { get; set; }

    public DateTimeOffset? LastRequestAt { get; set; }

    public DateTimeOffset? LastTokenActivityAt { get; set; }

    public TransparentProxyIngressMetricsSnapshot ToSnapshot()
        => new(
            IngressKind,
            SourceApplication,
            CaptureMode,
            Requests,
            Successes,
            Failures,
            TunnelOnlyRequests,
            OutputTokens,
            PromptCacheTokens,
            LastRequestAt,
            LastTokenActivityAt);
}

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
    DateTimeOffset? ProtocolCheckedAt,
    IReadOnlyList<TransparentProxyModelCooldownSnapshot>? ModelCooldowns = null);

public sealed record TransparentProxyModelCooldownSnapshot(
    string ModelName,
    int ConsecutiveFailures,
    DateTimeOffset CooldownUntil);

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
