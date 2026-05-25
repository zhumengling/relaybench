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

public sealed partial class TransparentProxyService : IAsyncDisposable
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
    private readonly TransparentProxyAppDetectorService _appDetector = new();
    private readonly TransparentProxyPortInspectorService _portInspector = new();
    private readonly TransparentProxyCaptureArtifactStore _captureArtifactStore = new();
    private readonly TransparentProxyCliEnvironmentService _cliEnvironmentService = new();
    private readonly TransparentProxyLaunchWrapperService _launchWrapperService = new();
    private readonly TransparentProxyCodexConfigService _codexConfigService = new();
    private readonly TransparentProxyClaudeConfigService _claudeConfigService = new();
    private readonly TransparentProxyVsCodeSettingsService _vsCodeSettingsService = new();
    private readonly TransparentProxyProtocolTranslatorService _protocolTranslator = new();
    private readonly TransparentProxyResponseNormalizationService _responseNormalizer = new();
    private readonly CodexOAuthService _codexOAuthService;
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
        : this(null, null)
    {
    }

    internal TransparentProxyService(TransparentProxyResponseCacheService? responseCache)
        : this(responseCache, null)
    {
    }

    internal TransparentProxyService(TransparentProxyResponseCacheService? responseCache, CodexOAuthService? codexOAuthService)
    {
        _responseCache = responseCache ?? new TransparentProxyResponseCacheService();
        _codexOAuthService = codexOAuthService ?? new CodexOAuthService();
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

    public void UpdateRoutes(IReadOnlyList<TransparentProxyRoute> routes)
    {
        lock (_syncRoot)
        {
            var config = _config;
            if (config is null)
            {
                return;
            }

            var nextRoutes = routes.ToArray();
            var nextIds = new HashSet<string>(nextRoutes.Select(static route => route.Id), StringComparer.OrdinalIgnoreCase);
            var removedStates = _routeStates
                .Where(item => !nextIds.Contains(item.Key))
                .Select(static item => item.Value)
                .ToArray();
            if (removedStates.Length > 0)
            {
                _routeHealthStore.SaveAll(removedStates);
                foreach (var routeId in removedStates.Select(static state => state.Id))
                {
                    _routeStates.Remove(routeId);
                }
            }

            var missingIds = nextRoutes
                .Select(static route => route.Id)
                .Where(routeId => !_routeStates.ContainsKey(routeId))
                .ToArray();
            var persistedHealth = missingIds.Length == 0
                ? new Dictionary<string, TransparentProxyRouteHealthSnapshot>(StringComparer.OrdinalIgnoreCase)
                : _routeHealthStore.Load(missingIds);

            foreach (var route in nextRoutes)
            {
                if (_routeStates.TryGetValue(route.Id, out var state))
                {
                    state.ApplyProtocol(route);
                    route.CircuitOpenUntil = state.CircuitOpenUntil;
                    continue;
                }

                state = new TransparentProxyRouteRuntimeState(route);
                if (persistedHealth.TryGetValue(route.Id, out var snapshot))
                {
                    state.ApplyHealthSnapshot(snapshot);
                    route.CircuitOpenUntil = state.CircuitOpenUntil;
                }

                _routeStates[route.Id] = state;
            }

            foreach (var key in _sessionRouteBindings
                         .Where(item => !nextIds.Contains(item.Value.RouteId))
                         .Select(static item => item.Key)
                         .ToArray())
            {
                _sessionRouteBindings.Remove(key);
            }

            _config = config with { Routes = nextRoutes };
            _responseCache.ClearModelsList();
            _roundRobinCursor = 0;
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
        _codexOAuthService.StartBackgroundRefreshLoop(EmitCodexOAuthBackgroundLog);
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

        await _codexOAuthService.StopBackgroundRefreshLoopAsync();

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

    private bool TryResolveHttpClientForRoute(
        TransparentProxyRoute route,
        TransparentProxyServerConfig config,
        out HttpClient? client,
        out string errorMessage)
    {
        var outboundProxy = TransparentProxyOutboundProxy.Parse(route.OutboundProxy);
        if (outboundProxy.Mode is TransparentProxyOutboundProxyMode.Invalid)
        {
            client = null;
            errorMessage = outboundProxy.ErrorMessage;
            return false;
        }

        if (outboundProxy.Mode is TransparentProxyOutboundProxyMode.Inherit)
        {
            client = _httpClient ?? throw new InvalidOperationException("Transparent proxy HTTP client is not ready.");
            errorMessage = string.Empty;
            return true;
        }

        var key = $"{outboundProxy.CacheKey}|tls:{config.IgnoreTlsErrors}|timeout:{config.UpstreamTimeoutSeconds}|concurrency:{config.MaxConcurrency}";
        lock (_syncRoot)
        {
            if (_routeHttpClients.TryGetValue(key, out var cachedClient))
            {
                client = cachedClient;
                errorMessage = string.Empty;
                return true;
            }

            try
            {
                client = CreateHttpClient(config, outboundProxy);
            }
            catch (Exception ex)
            {
                client = null;
                errorMessage = ProbeTraceRedactor.RedactText(ex.Message);
                return false;
            }

            _routeHttpClients[key] = client;
            errorMessage = string.Empty;
            return true;
        }
    }

    private HttpClient ResolveHttpClientForRoute(TransparentProxyRoute route, TransparentProxyServerConfig config)
        => TryResolveHttpClientForRoute(route, config, out var client, out var errorMessage)
            ? client!
            : throw new InvalidOperationException(errorMessage);

    private static HttpClient CreateHttpClient(TransparentProxyServerConfig config, string outboundProxy)
        => CreateHttpClient(config, TransparentProxyOutboundProxy.Parse(outboundProxy));

    private static HttpClient CreateHttpClient(
        TransparentProxyServerConfig config,
        TransparentProxyOutboundProxySetting outboundProxy)
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

        TransparentProxyOutboundProxy.ApplyTo(handler, outboundProxy);

        if (config.IgnoreTlsErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
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

}

