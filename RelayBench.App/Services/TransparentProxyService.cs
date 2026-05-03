using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.App.Services;

public sealed class TransparentProxyService : IAsyncDisposable
{
    private const int CircuitFailureThreshold = 4;
    private const int CircuitSuccessThreshold = 2;
    private const int CircuitTimeoutSeconds = 60;
    private const int CircuitMaxCooldownSeconds = 30 * 60;
    private const int CircuitMinRequests = 10;
    private const double CircuitErrorRateThreshold = 0.60d;

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
    private readonly ConcurrentDictionary<string, TransparentProxyCachedResponse> _cache = new(StringComparer.Ordinal);
    private readonly List<long> _latencies = [];
    private readonly Queue<TransparentProxyTokenSample> _tokenSamples = new();
    private readonly Dictionary<string, TransparentProxyRouteRuntimeState> _routeStates = new(StringComparer.OrdinalIgnoreCase);

    private HttpListener? _listener;
    private HttpClient? _httpClient;
    private CancellationTokenSource? _serverCancellationSource;
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
    private long _totalOutputTokens;
    private DateTimeOffset? _lastTokenActivityAt;

    public event EventHandler<TransparentProxyLogEntry>? LogEmitted;

    public event EventHandler<TransparentProxyMetricsSnapshot>? MetricsChanged;

    public bool IsRunning => _listener?.IsListening == true;

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
        lock (_syncRoot)
        {
            _totalOutputTokens = 0;
            _lastTokenActivityAt = null;
            _tokenSamples.Clear();
        }

        PublishMetrics();
    }

    public async Task StartAsync(TransparentProxyServerConfig config)
    {
        if (config.Routes.Count == 0)
        {
            throw new InvalidOperationException("至少需要一个上游路由。");
        }

        await StopAsync();

        _config = config;
        _cache.Clear();
        _concurrencyGate = new SemaphoreSlim(Math.Max(1, config.MaxConcurrency), Math.Max(1, config.MaxConcurrency));

        lock (_syncRoot)
        {
            _latencies.Clear();
            _routeStates.Clear();
            foreach (var route in config.Routes)
            {
                _routeStates[route.Id] = new TransparentProxyRouteRuntimeState(route);
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
            _totalOutputTokens = 0;
            _lastTokenActivityAt = null;
            _tokenSamples.Clear();
        }

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

        if (config.IgnoreTlsErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        _serverCancellationSource = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{config.Port}/");
        _listener.Start();

        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_serverCancellationSource.Token));
        EmitLog(new TransparentProxyLogEntry(
            DateTimeOffset.Now,
            "INFO",
            "-",
            "/",
            "-",
            200,
            0,
            $"透明代理已启动，监听 http://127.0.0.1:{config.Port}/v1"));
        PublishMetrics();
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        var cancellationSource = _serverCancellationSource;

        _serverCancellationSource = null;
        _listener = null;

        if (cancellationSource is not null)
        {
            await cancellationSource.CancelAsync();
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
        _httpClient?.Dispose();
        _httpClient = null;
        _concurrencyGate?.Dispose();
        _concurrencyGate = null;

        if (cancellationSource is not null)
        {
            cancellationSource.Dispose();
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "INFO", "-", "/", "-", 200, 0, "透明代理已停止。"));
            PublishMetrics();
        }
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

                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
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
                EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "ERROR", "-", "/", "-", 500, 0, $"监听失败：{ex.Message}"));
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
            await WriteJsonErrorAsync(context, 503, "transparent_proxy_not_ready", "透明代理尚未配置上游路由。", serverCancellationToken);
            return;
        }

        if (!TryAcquireRateSlot(config))
        {
            Interlocked.Increment(ref _rateLimitedRequests);
            Interlocked.Increment(ref _totalRequests);
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", method, pathAndQuery, "-", 429, 0, "已触发本地限速。"));
            PublishMetrics();
            await WriteJsonErrorAsync(context, 429, "relaybench_rate_limited", "RelayBench 本地透明代理限速中。", serverCancellationToken);
            return;
        }

        var concurrencyGate = _concurrencyGate;
        if (concurrencyGate is null || !await concurrencyGate.WaitAsync(0, serverCancellationToken))
        {
            Interlocked.Increment(ref _rateLimitedRequests);
            Interlocked.Increment(ref _totalRequests);
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", method, pathAndQuery, "-", 429, 0, "并发保护已拒绝请求。"));
            PublishMetrics();
            await WriteJsonErrorAsync(context, 429, "relaybench_concurrency_limited", "RelayBench 本地透明代理并发已满。", serverCancellationToken);
            return;
        }

        Interlocked.Increment(ref _activeRequests);
        Interlocked.Increment(ref _totalRequests);
        PublishMetrics();

        try
        {
            var requestBody = await ReadRequestBodyAsync(context.Request, serverCancellationToken);
            var streamRequested = IsStreamingRequest(context.Request, requestBody);
            var cacheKey = config.EnableCache && !streamRequested
                ? BuildCacheKey(method, pathAndQuery, requestBody)
                : string.Empty;

            if (config.EnableCache &&
                !streamRequested &&
                TryGetCachedResponse(cacheKey, config.CacheTtlSeconds, out var cachedResponse))
            {
                Interlocked.Increment(ref _cacheHits);
                Interlocked.Increment(ref _successRequests);
                await WriteCachedResponseAsync(context, cachedResponse, serverCancellationToken);
                TryTrackResponseBodyTokens(cachedResponse.Body);
                var elapsed = GetElapsedMilliseconds(startedAt);
                TrackLatency(elapsed);
                EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "CACHE", method, pathAndQuery, "cache", cachedResponse.StatusCode, elapsed, "命中本地短缓存。"));
                PublishMetrics();
                return;
            }

            TransparentProxyUpstreamAttempt? lastAttempt = null;
            var candidateRoutes = BuildCandidateRoutes(config);
            if (candidateRoutes.Count == 0)
            {
                Interlocked.Increment(ref _failedRequests);
                var elapsed = GetElapsedMilliseconds(startedAt);
                TrackLatency(elapsed);
                EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", method, pathAndQuery, "-", 503, elapsed, "所有上游暂时熔断，等待半开探测窗口。"));
                PublishMetrics();
                await WriteJsonErrorAsync(context, 503, "relaybench_all_routes_circuit_open", "所有上游暂时不可用，稍后会自动半开探测。", serverCancellationToken);
                return;
            }

            var attempts = config.EnableFallback ? candidateRoutes.Count : Math.Min(1, candidateRoutes.Count);
            var bypassCircuitBreaker = !config.EnableFallback || config.Routes.Count <= 1;
            var attemptedRoutes = 0;
            for (var index = 0; index < attempts; index++)
            {
                var route = candidateRoutes[index];
                if (!TryAcquireRoutePermit(route, bypassCircuitBreaker, out var routePermit))
                {
                    continue;
                }

                attemptedRoutes++;

                var attempt = await TryProxyToRouteAsync(
                    context,
                    client,
                    config,
                    route,
                    routePermit,
                    method,
                    pathAndQuery,
                    requestBody,
                    cacheKey,
                    streamRequested,
                    config.EnableFallback && index < attempts - 1,
                    serverCancellationToken);

                lastAttempt = attempt;
                if (attempt.DeliveredToClient)
                {
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
                ? "所有上游暂时处于半开探测限流中。"
                : lastAttempt?.Message ?? "所有上游路由都不可用。";
            var totalElapsed = GetElapsedMilliseconds(startedAt);
            TrackLatency(totalElapsed);
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "ERROR", method, pathAndQuery, lastAttempt?.RouteName ?? "-", status, totalElapsed, message));
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
        HttpClient client,
        TransparentProxyServerConfig config,
        TransparentProxyRoute route,
        TransparentProxyRoutePermit routePermit,
        string method,
        string pathAndQuery,
        byte[] requestBody,
        string cacheKey,
        bool streamRequested,
        bool allowRouteFallback,
        CancellationToken serverCancellationToken)
    {
        var routeStopwatch = Stopwatch.StartNew();
        using CancellationTokenSource attemptCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        attemptCancellationSource.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.UpstreamTimeoutSeconds)));

        try
        {
            var preparedRequests = BuildPreparedUpstreamRequests(
                method,
                pathAndQuery,
                requestBody,
                route,
                config,
                streamRequested);
            TransparentProxyUpstreamAttempt? lastProtocolAttempt = null;

            for (var protocolIndex = 0; protocolIndex < preparedRequests.Count; protocolIndex++)
            {
                var prepared = preparedRequests[protocolIndex];
                using var upstreamRequest = CreateUpstreamRequest(
                    context.Request,
                    method,
                    prepared.UpstreamUrl,
                    route,
                    prepared.Body,
                    prepared.WireApi,
                    prepared.ExtraHeaders);
                using var upstreamResponse = await client.SendAsync(
                    upstreamRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptCancellationSource.Token);

                var statusCode = (int)upstreamResponse.StatusCode;
                var hasNextProtocol = protocolIndex < preparedRequests.Count - 1;
                if (ShouldTryNextWireApi(statusCode, prepared.WireApi, hasNextProtocol))
                {
                    lastProtocolAttempt = new TransparentProxyUpstreamAttempt(false, route.Name, statusCode, $"上游 {DisplayWireApi(prepared.WireApi)} 不可用。");
                    EmitLog(new TransparentProxyLogEntry(
                        DateTimeOffset.Now,
                        "WARN",
                        method,
                        pathAndQuery,
                        route.Name,
                        statusCode,
                        routeStopwatch.ElapsedMilliseconds,
                        $"{DisplayWireApi(prepared.WireApi)} 返回 {statusCode}，尝试下一种协议。"));
                    continue;
                }

                if (ShouldFallback(statusCode))
                {
                    MarkRouteFailure(
                        route,
                        statusCode,
                        routeStopwatch.ElapsedMilliseconds,
                        routePermit,
                        ResolveRetryAfter(upstreamResponse.Headers.RetryAfter));
                    if (allowRouteFallback)
                    {
                        EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", method, pathAndQuery, route.Name, statusCode, routeStopwatch.ElapsedMilliseconds, "上游返回可 fallback 状态，切换下一路。"));
                        return new TransparentProxyUpstreamAttempt(false, route.Name, statusCode, "上游返回可 fallback 状态。");
                    }
                }
                else
                {
                    MarkRouteSuccess(route, statusCode, routeStopwatch.ElapsedMilliseconds, routePermit);
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
                    prepared.NormalizeToChatCompletions,
                    attemptCancellationSource.Token);
                EmitLog(new TransparentProxyLogEntry(
                    DateTimeOffset.Now,
                    "INFO",
                    method,
                    pathAndQuery,
                    route.Name,
                    statusCode,
                    routeStopwatch.ElapsedMilliseconds,
                    $"已路由到 {route.Name}，协议 {DisplayWireApi(prepared.WireApi)}。"));
                return new TransparentProxyUpstreamAttempt(true, route.Name, statusCode, "已转发。");
            }

            MarkRouteFailure(route, lastProtocolAttempt?.StatusCode ?? 502, routeStopwatch.ElapsedMilliseconds, routePermit);
            return lastProtocolAttempt ?? new TransparentProxyUpstreamAttempt(false, route.Name, 502, "没有可用协议。");
        }
        catch (OperationCanceledException)
        {
            MarkRouteFailure(route, 504, routeStopwatch.ElapsedMilliseconds, routePermit);
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", method, pathAndQuery, route.Name, 504, routeStopwatch.ElapsedMilliseconds, "上游超时，尝试 fallback。"));
            return new TransparentProxyUpstreamAttempt(false, route.Name, 504, "上游超时。");
        }
        catch (Exception ex)
        {
            MarkRouteFailure(route, 502, routeStopwatch.ElapsedMilliseconds, routePermit);
            var safeMessage = ProbeTraceRedactor.RedactText(ex.Message);
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", method, pathAndQuery, route.Name, 502, routeStopwatch.ElapsedMilliseconds, $"上游连接失败：{safeMessage}"));
            return new TransparentProxyUpstreamAttempt(false, route.Name, 502, $"上游连接失败：{safeMessage}");
        }
    }

    private static IReadOnlyList<TransparentProxyPreparedRequest> BuildPreparedUpstreamRequests(
        string method,
        string pathAndQuery,
        byte[] requestBody,
        TransparentProxyRoute route,
        TransparentProxyServerConfig config,
        bool streamRequested)
    {
        var rewrittenBody = MaybeRewriteModel(requestBody, route, config.RewriteModel);
        if (!IsOpenAiChatCompletionsRequest(method, pathAndQuery) || rewrittenBody.Length == 0)
        {
            var inferredWireApi = InferWireApiFromPath(pathAndQuery);
            return
            [
                new TransparentProxyPreparedRequest(
                    inferredWireApi,
                    BuildUpstreamUrl(route.BaseUrl, pathAndQuery),
                    rewrittenBody,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    false,
                    ResolveRequestModel(rewrittenBody, route))
            ];
        }

        var relativePath = ExtractRelativePath(pathAndQuery);
        var query = ExtractQuery(pathAndQuery);
        var requestBodyText = Encoding.UTF8.GetString(rewrittenBody);
        List<TransparentProxyPreparedRequest> preparedRequests = [];

        foreach (var wireApi in BuildWireApiAttempts(route))
        {
            try
            {
                var prepared = AdvancedWireRequestBuilder.PreparePostJson(
                    relativePath,
                    requestBodyText,
                    wireApi,
                    streamRequested);
                var upstreamPath = string.IsNullOrWhiteSpace(query)
                    ? prepared.RelativePath
                    : $"{prepared.RelativePath}{query}";
                preparedRequests.Add(new TransparentProxyPreparedRequest(
                    prepared.WireApi,
                    BuildUpstreamUrl(route.BaseUrl, upstreamPath),
                    Encoding.UTF8.GetBytes(prepared.RequestBody),
                    prepared.ExtraHeaders,
                    !string.Equals(prepared.WireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal),
                    ResolveRequestModel(Encoding.UTF8.GetBytes(prepared.RequestBody), route)));
            }
            catch
            {
                if (preparedRequests.Count == 0)
                {
                    preparedRequests.Add(new TransparentProxyPreparedRequest(
                        ProxyWireApiProbeService.ChatCompletionsWireApi,
                        BuildUpstreamUrl(route.BaseUrl, pathAndQuery),
                        rewrittenBody,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        false,
                        ResolveRequestModel(rewrittenBody, route)));
                }

                break;
            }
        }

        return preparedRequests;
    }

    private static IReadOnlyList<string> BuildWireApiAttempts(TransparentProxyRoute route)
    {
        List<string> candidates = [];
        var hasProtocolProbe =
            route.ResponsesSupported.HasValue ||
            route.AnthropicMessagesSupported.HasValue ||
            route.ChatCompletionsSupported.HasValue;

        if (!hasProtocolProbe || route.ResponsesSupported == true)
        {
            AddWireApiCandidate(candidates, ProxyWireApiProbeService.ResponsesWireApi);
        }

        if (!hasProtocolProbe || route.AnthropicMessagesSupported == true)
        {
            AddWireApiCandidate(candidates, ProxyWireApiProbeService.AnthropicMessagesWireApi);
        }

        if (!hasProtocolProbe ||
            route.ChatCompletionsSupported != false ||
            candidates.Count == 0)
        {
            AddWireApiCandidate(candidates, ProxyWireApiProbeService.ChatCompletionsWireApi);
        }

        return candidates;
    }

    private static void AddWireApiCandidate(List<string> candidates, string? wireApi)
    {
        var normalized = ProxyWireApiProbeService.NormalizeWireApi(wireApi);
        if (string.IsNullOrWhiteSpace(normalized) ||
            candidates.Contains(normalized, StringComparer.Ordinal))
        {
            return;
        }

        candidates.Add(normalized);
    }

    private static HttpRequestMessage CreateUpstreamRequest(
        HttpListenerRequest source,
        string method,
        string upstreamUrl,
        TransparentProxyRoute route,
        byte[] body,
        string wireApi,
        IReadOnlyDictionary<string, string> extraHeaders)
    {
        HttpRequestMessage request = new(new HttpMethod(method), upstreamUrl);
        foreach (var headerName in source.Headers.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(headerName) || HopByHopHeaders.Contains(headerName))
            {
                continue;
            }

            var values = source.Headers.GetValues(headerName);
            if (values is null)
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(headerName, values))
            {
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.TryAddWithoutValidation(headerName, values);
            }
        }

        var effectiveApiKey = !string.IsNullOrWhiteSpace(route.ApiKey)
            ? route.ApiKey.Trim()
            : ExtractBearerToken(source.Headers["Authorization"]);

        if (!string.IsNullOrWhiteSpace(route.ApiKey))
        {
            request.Headers.Remove("Authorization");
            request.Headers.Remove("x-api-key");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {effectiveApiKey}");
        }

        foreach (var header in extraHeaders)
        {
            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (string.Equals(wireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal))
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            if (!string.IsNullOrWhiteSpace(effectiveApiKey))
            {
                request.Headers.Remove("x-api-key");
                request.Headers.TryAddWithoutValidation("x-api-key", effectiveApiKey);
            }
        }

        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            if (!string.IsNullOrWhiteSpace(source.ContentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", source.ContentType);
            }
            else
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            }
        }

        return request;
    }

    private static string ExtractBearerToken(string? authorizationHeader)
    {
        const string bearerPrefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return authorizationHeader[bearerPrefix.Length..].Trim();
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
        bool normalizeToChatCompletions,
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
                await CopyNormalizedChatStreamAsync(
                    context,
                    upstreamResponse,
                    responseModel,
                    wireApi,
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
                _cache[cacheKey] = new TransparentProxyCachedResponse(DateTimeOffset.UtcNow, statusCode, contentType, bytes);
            }
        }
        else
        {
            await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            await stream.CopyToAsync(context.Response.OutputStream, cancellationToken);
        }

        context.Response.OutputStream.Close();
    }

    private async Task CopyNormalizedChatJsonAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        int statusCode,
        string cacheKey,
        TransparentProxyServerConfig config,
        string responseModel,
        string wireApi,
        CancellationToken cancellationToken)
    {
        var upstreamBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var upstreamText = Encoding.UTF8.GetString(upstreamBytes);
        var assistantText = ModelResponseTextExtractor.TryExtractAssistantText(upstreamText);
        if (assistantText is null)
        {
            context.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
            context.Response.ContentLength64 = upstreamBytes.LongLength;
            await context.Response.OutputStream.WriteAsync(upstreamBytes, cancellationToken);
            TryTrackResponseBodyTokens(upstreamBytes);
            context.Response.OutputStream.Close();
            return;
        }

        var normalizedBytes = BuildOpenAiChatCompletionBytes(
            assistantText,
            ResolveResponseModel(upstreamText, responseModel),
            wireApi);
        const string normalizedContentType = "application/json; charset=utf-8";
        context.Response.ContentType = normalizedContentType;
        context.Response.ContentLength64 = normalizedBytes.LongLength;
        await context.Response.OutputStream.WriteAsync(normalizedBytes, cancellationToken);
        TrackOutputTextTokens(assistantText);
        if (config.EnableCache &&
            !string.IsNullOrWhiteSpace(cacheKey) &&
            statusCode >= 200 &&
            statusCode < 300 &&
            normalizedBytes.Length <= config.CacheMaxBytes)
        {
            _cache[cacheKey] = new TransparentProxyCachedResponse(
                DateTimeOffset.UtcNow,
                statusCode,
                normalizedContentType,
                normalizedBytes);
        }

        context.Response.OutputStream.Close();
    }

    private async Task CopyNormalizedChatStreamAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        string responseModel,
        string wireApi,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;
        var model = string.IsNullOrWhiteSpace(responseModel) ? "relaybench-proxy" : responseModel.Trim();
        var streamId = $"chatcmpl-relaybench-{Guid.NewGuid():N}";
        var wroteDone = false;

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
                await WriteSseDataAsync(context.Response.OutputStream, "[DONE]", cancellationToken);
                wroteDone = true;
                break;
            }

            var delta = ChatSseParser.TryExtractDelta(data);
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            var chunk = BuildOpenAiChatCompletionChunk(delta, model, wireApi, streamId);
            await WriteSseDataAsync(context.Response.OutputStream, chunk, cancellationToken);
            TrackOutputTextTokens(delta);
        }

        if (!wroteDone)
        {
            await WriteSseDataAsync(context.Response.OutputStream, "[DONE]", cancellationToken);
        }

        context.Response.OutputStream.Close();
    }

    private static byte[] BuildOpenAiChatCompletionBytes(
        string content,
        string model,
        string wireApi)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = $"chatcmpl-relaybench-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = string.IsNullOrWhiteSpace(model) ? "relaybench-proxy" : model.Trim(),
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content
                    },
                    finish_reason = "stop"
                }
            },
            relaybench = new
            {
                upstream_wire_api = wireApi
            }
        }, CompactJsonOptions);

    private static string BuildOpenAiChatCompletionChunk(
        string delta,
        string model,
        string wireApi,
        string streamId)
        => JsonSerializer.Serialize(new
        {
            id = streamId,
            @object = "chat.completion.chunk",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        content = delta
                    },
                    finish_reason = (string?)null
                }
            },
            relaybench = new
            {
                upstream_wire_api = wireApi
            }
        }, CompactJsonOptions);

    private static async Task WriteSseDataAsync(Stream outputStream, string data, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await outputStream.WriteAsync(bytes, cancellationToken);
        await outputStream.FlushAsync(cancellationToken);
    }

    private void TryTrackResponseBodyTokens(byte[] body)
    {
        if (body.Length == 0)
        {
            return;
        }

        try
        {
            var text = Encoding.UTF8.GetString(body);
            var assistantText = ModelResponseTextExtractor.TryExtractAssistantText(text);
            TrackOutputTextTokens(assistantText);
        }
        catch
        {
            // Token telemetry is observability only; proxying should never fail because estimation failed.
        }
    }

    private void TrackOutputTextTokens(string? text)
    {
        var tokenCount = TokenCountEstimator.EstimateOutputTokens(text);
        if (tokenCount <= 0)
        {
            return;
        }

        TrackOutputTokens(tokenCount);
    }

    private void TrackOutputTokens(int tokenCount)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_syncRoot)
        {
            _totalOutputTokens += tokenCount;
            _lastTokenActivityAt = now;
            _tokenSamples.Enqueue(new TransparentProxyTokenSample(now, tokenCount));
            PruneTokenSamples(now);
        }

        PublishMetrics();
    }

    private void PruneTokenSamples(DateTimeOffset now)
    {
        while (_tokenSamples.Count > 0 &&
               (now - _tokenSamples.Peek().Timestamp).TotalMilliseconds > 1250)
        {
            _tokenSamples.Dequeue();
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

    private IReadOnlyList<TransparentProxyRoute> BuildCandidateRoutes(TransparentProxyServerConfig config)
    {
        if (!config.EnableFallback || config.Routes.Count <= 1)
        {
            return config.Routes.Take(1).ToArray();
        }

        var now = DateTimeOffset.UtcNow;
        lock (_syncRoot)
        {
            List<(TransparentProxyRoute Route, int Priority, double Score)> available = [];
            for (var index = 0; index < config.Routes.Count; index++)
            {
                var route = config.Routes[index];
                if (!_routeStates.TryGetValue(route.Id, out var state) ||
                    state.IsCircuitAvailable(now, CircuitTimeoutSeconds))
                {
                    available.Add((route, index, CalculateRouteScheduleScore(state, index)));
                }
            }

            return available
                .OrderBy(static item => item.Score)
                .ThenBy(static item => item.Priority)
                .Select(static item => item.Route)
                .ToArray();
        }
    }

    private static double CalculateRouteScheduleScore(TransparentProxyRouteRuntimeState? state, int priority)
    {
        var score = priority * 8d;
        if (state is null || state.Sent <= 0)
        {
            return score;
        }

        if (state.CircuitState == TransparentProxyCircuitState.HalfOpen)
        {
            score += 1_000d;
        }

        var windowRequests = state.CircuitWindowRequests > 0 ? state.CircuitWindowRequests : state.Sent;
        var windowFailures = state.CircuitWindowRequests > 0 ? state.CircuitWindowFailures : state.Failed;
        var failureRate = windowFailures / (double)Math.Max(1, windowRequests);
        score += Math.Clamp(failureRate, 0d, 1d) * 120d;
        score += Math.Min(6, state.ConsecutiveFailures) * 80d;

        if (state.LastLatencyMs > 0)
        {
            score += Math.Clamp(state.LastLatencyMs / 35d, 0d, 80d);
        }

        return score;
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
            if (!_routeStates.TryGetValue(route.Id, out var state))
            {
                return true;
            }

            var allowed = state.TryAcquirePermit(DateTimeOffset.UtcNow, CircuitTimeoutSeconds, out var usedHalfOpenPermit);
            routePermit = new TransparentProxyRoutePermit(route.Id, usedHalfOpenPermit);
            return allowed;
        }
    }

    private void MarkRouteSuccess(
        TransparentProxyRoute route,
        int statusCode,
        long latencyMs,
        TransparentProxyRoutePermit routePermit,
        TimeSpan? retryAfter = null)
    {
        var recovered = false;
        lock (_syncRoot)
        {
            if (!_routeStates.TryGetValue(route.Id, out var state))
            {
                return;
            }

            state.Sent++;
            state.Success++;
            state.LastStatusCode = statusCode;
            state.LastLatencyMs = latencyMs;
            state.LastSeenAt = DateTimeOffset.Now;
            state.ConsecutiveFailures = 0;
            state.CircuitWindowRequests++;
            state.ReleasePermit(routePermit.UsedHalfOpenPermit);
            if (state.CircuitState == TransparentProxyCircuitState.HalfOpen)
            {
                state.ConsecutiveSuccesses++;
                if (state.ConsecutiveSuccesses >= CircuitSuccessThreshold)
                {
                    state.TransitionToClosed();
                    recovered = true;
                }
            }
            else if (state.CircuitState == TransparentProxyCircuitState.Closed)
            {
                state.ConsecutiveSuccesses = 0;
            }

            route.CircuitOpenUntil = DateTimeOffset.MinValue;
        }

        if (recovered)
        {
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "INFO", "-", "/", route.Name, statusCode, latencyMs, "半开探测成功，路由已恢复。"));
        }
    }

    private void MarkRouteFailure(
        TransparentProxyRoute route,
        int statusCode,
        long latencyMs,
        TransparentProxyRoutePermit routePermit,
        TimeSpan? retryAfter = null)
    {
        var opened = false;
        var halfOpenFailed = false;
        DateTimeOffset retryAt = DateTimeOffset.MinValue;
        lock (_syncRoot)
        {
            if (!_routeStates.TryGetValue(route.Id, out var state))
            {
                return;
            }

            state.Sent++;
            state.Failed++;
            state.LastStatusCode = statusCode;
            state.LastLatencyMs = latencyMs;
            state.LastSeenAt = DateTimeOffset.Now;
            state.ConsecutiveFailures++;
            state.ConsecutiveSuccesses = 0;
            state.CircuitWindowRequests++;
            state.CircuitWindowFailures++;
            state.ReleasePermit(routePermit.UsedHalfOpenPermit);

            var now = DateTimeOffset.UtcNow;
            var cooldownSeconds = ResolveCircuitCooldownSeconds(retryAfter);
            var explicitCooldown = retryAfter is not null && statusCode is 429 or 503;
            if (state.CircuitState == TransparentProxyCircuitState.HalfOpen)
            {
                halfOpenFailed = true;
                retryAt = state.TransitionToOpen(now, cooldownSeconds);
                opened = true;
            }
            else if (explicitCooldown)
            {
                retryAt = state.TransitionToOpen(now, cooldownSeconds);
                opened = true;
            }
            else if (state.CircuitState == TransparentProxyCircuitState.Closed &&
                     ShouldOpenCircuit(state))
            {
                retryAt = state.TransitionToOpen(now, cooldownSeconds);
                opened = true;
            }

            route.CircuitOpenUntil = state.CircuitOpenUntil;
        }

        if (opened)
        {
            var message = halfOpenFailed
                ? $"半开探测失败，路由熔断至 {retryAt.ToLocalTime():HH:mm:ss}。"
                : $"连续失败或错误率过高，路由熔断至 {retryAt.ToLocalTime():HH:mm:ss}。";
            EmitLog(new TransparentProxyLogEntry(DateTimeOffset.Now, "WARN", "-", "/", route.Name, statusCode, latencyMs, message));
        }
    }

    private static bool ShouldOpenCircuit(TransparentProxyRouteRuntimeState state)
    {
        if (state.ConsecutiveFailures >= CircuitFailureThreshold)
        {
            return true;
        }

        return state.CircuitWindowRequests >= CircuitMinRequests &&
               state.CircuitWindowFailures / (double)Math.Max(1, state.CircuitWindowRequests) >= CircuitErrorRateThreshold;
    }

    private static int ResolveCircuitCooldownSeconds(TimeSpan? retryAfter)
    {
        if (retryAfter is null)
        {
            return CircuitTimeoutSeconds;
        }

        var seconds = (int)Math.Ceiling(retryAfter.Value.TotalSeconds);
        return Math.Clamp(seconds, 1, CircuitMaxCooldownSeconds);
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

    private bool TryGetCachedResponse(string cacheKey, int ttlSeconds, out TransparentProxyCachedResponse cachedResponse)
    {
        cachedResponse = default!;
        if (string.IsNullOrWhiteSpace(cacheKey) || !_cache.TryGetValue(cacheKey, out var entry))
        {
            return false;
        }

        if ((DateTimeOffset.UtcNow - entry.CreatedAt).TotalSeconds > Math.Max(1, ttlSeconds))
        {
            _cache.TryRemove(cacheKey, out _);
            return false;
        }

        cachedResponse = entry;
        return true;
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

    private static byte[] MaybeRewriteModel(byte[] requestBody, TransparentProxyRoute route, bool rewriteModel)
    {
        if (!rewriteModel || requestBody.Length == 0 || string.IsNullOrWhiteSpace(route.Model))
        {
            return requestBody;
        }

        try
        {
            var node = JsonNode.Parse(requestBody);
            if (node is not JsonObject obj)
            {
                return requestBody;
            }

            obj["model"] = route.Model;
            return JsonSerializer.SerializeToUtf8Bytes(obj);
        }
        catch
        {
            return requestBody;
        }
    }

    private static string BuildUpstreamUrl(string baseUrl, string pathAndQuery)
    {
        var baseUri = new Uri(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        var path = pathAndQuery;
        var queryIndex = path.IndexOf('?');
        var query = queryIndex >= 0 ? path[queryIndex..] : string.Empty;
        var pathOnly = queryIndex >= 0 ? path[..queryIndex] : path;

        var normalizedBasePath = baseUri.AbsolutePath.TrimEnd('/');
        var normalizedPath = pathOnly.TrimStart('/');
        if (normalizedBasePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
            normalizedPath.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[3..];
        }

        return new Uri(baseUri, normalizedPath).ToString().TrimEnd('/') + query;
    }

    private static bool IsOpenAiChatCompletionsRequest(string method, string pathAndQuery)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = ExtractRelativePath(pathAndQuery);
        return relativePath.Equals("chat/completions", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals("v1/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractRelativePath(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        var pathOnly = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
        return pathOnly.Trim().TrimStart('/');
    }

    private static string ExtractQuery(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        return queryIndex >= 0 ? pathAndQuery[queryIndex..] : string.Empty;
    }

    private static string InferWireApiFromPath(string pathAndQuery)
    {
        var relativePath = ExtractRelativePath(pathAndQuery);
        if (relativePath.EndsWith("responses", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyWireApiProbeService.ResponsesWireApi;
        }

        if (relativePath.EndsWith("messages", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyWireApiProbeService.AnthropicMessagesWireApi;
        }

        return ProxyWireApiProbeService.ChatCompletionsWireApi;
    }

    private static string ResolveRequestModel(byte[] requestBody, TransparentProxyRoute route)
    {
        if (!string.IsNullOrWhiteSpace(route.Model))
        {
            return route.Model.Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return TryReadStringProperty(document.RootElement, "model") ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveResponseModel(string upstreamText, string fallbackModel)
    {
        if (!string.IsNullOrWhiteSpace(fallbackModel))
        {
            return fallbackModel.Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            return TryReadStringProperty(document.RootElement, "model") ?? "relaybench-proxy";
        }
        catch
        {
            return "relaybench-proxy";
        }
    }

    private static string? TryReadStringProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string BuildCacheKey(string method, string pathAndQuery, byte[] requestBody)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(requestBody);
        return $"{method.ToUpperInvariant()}|{pathAndQuery}|{Convert.ToHexString(hash)}";
    }

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

        return statusCode >= 400;
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
            var now = DateTimeOffset.UtcNow;
            PruneTokenSamples(now);
            var orderedLatencies = _latencies.OrderBy(static item => item).ToArray();
            var p50 = Percentile(orderedLatencies, 0.50);
            var p95 = Percentile(orderedLatencies, 0.95);
            var tokensPerSecond = _tokenSamples.Sum(static item => item.TokenCount) / 1.25d;
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
                _cache.Count,
                routes,
                _totalOutputTokens,
                tokensPerSecond,
                _lastTokenActivityAt);
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
            ProbeTraceRedactor.RedactText(entry.Message));
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
        response.Headers["Access-Control-Allow-Headers"] = "authorization, content-type, x-api-key, anthropic-version";
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
        DateTimeOffset? protocolCheckedAt = null)
    {
        Id = id;
        Name = name;
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        Model = model;
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
            protocolCheckedAt)
        {
            CircuitOpenUntil = CircuitOpenUntil
        };

    internal DateTimeOffset CircuitOpenUntil { get; set; } = DateTimeOffset.MinValue;
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
}

public sealed record TransparentProxyLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Method,
    string Path,
    string RouteName,
    int StatusCode,
    long ElapsedMs,
    string Message);

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
    DateTimeOffset? LastTokenActivityAt);

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

internal sealed record TransparentProxyCachedResponse(DateTimeOffset CreatedAt, int StatusCode, string ContentType, byte[] Body);

internal sealed record TransparentProxyTokenSample(DateTimeOffset Timestamp, int TokenCount);

internal sealed record TransparentProxyUpstreamAttempt(bool DeliveredToClient, string RouteName, int StatusCode, string Message);

internal sealed record TransparentProxyRoutePermit(string RouteId, bool UsedHalfOpenPermit);

internal sealed record TransparentProxyPreparedRequest(
    string WireApi,
    string UpstreamUrl,
    byte[] Body,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    bool NormalizeToChatCompletions,
    string ResponseModel);

internal enum TransparentProxyCircuitState
{
    Closed,
    Open,
    HalfOpen
}

internal sealed class TransparentProxyRouteRuntimeState
{
    public TransparentProxyRouteRuntimeState(TransparentProxyRoute route)
    {
        Id = route.Id;
        Name = route.Name;
        PreferredWireApi = route.PreferredWireApi;
        ChatCompletionsSupported = route.ChatCompletionsSupported;
        ResponsesSupported = route.ResponsesSupported;
        AnthropicMessagesSupported = route.AnthropicMessagesSupported;
        ProtocolCheckedAt = route.ProtocolCheckedAt;
    }

    public string Id { get; }

    public string Name { get; }

    public int Sent { get; set; }

    public int Success { get; set; }

    public int Failed { get; set; }

    public int LastStatusCode { get; set; }

    public long LastLatencyMs { get; set; }

    public int ConsecutiveFailures { get; set; }

    public int ConsecutiveSuccesses { get; set; }

    public int CircuitWindowRequests { get; set; }

    public int CircuitWindowFailures { get; set; }

    public TransparentProxyCircuitState CircuitState { get; set; } = TransparentProxyCircuitState.Closed;

    public DateTimeOffset CircuitOpenUntil { get; set; } = DateTimeOffset.MinValue;

    public bool HalfOpenInFlight { get; set; }

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.MinValue;

    public string? PreferredWireApi { get; private set; }

    public bool? ChatCompletionsSupported { get; private set; }

    public bool? ResponsesSupported { get; private set; }

    public bool? AnthropicMessagesSupported { get; private set; }

    public DateTimeOffset? ProtocolCheckedAt { get; private set; }

    public void ApplyProtocol(TransparentProxyRoute route)
    {
        PreferredWireApi = route.PreferredWireApi;
        ChatCompletionsSupported = route.ChatCompletionsSupported;
        ResponsesSupported = route.ResponsesSupported;
        AnthropicMessagesSupported = route.AnthropicMessagesSupported;
        ProtocolCheckedAt = route.ProtocolCheckedAt;
    }

    public bool IsCircuitAvailable(DateTimeOffset now, int timeoutSeconds)
    {
        if (CircuitState == TransparentProxyCircuitState.Open && CircuitOpenUntil <= now)
        {
            TransitionToHalfOpen();
        }

        return CircuitState != TransparentProxyCircuitState.Open;
    }

    public bool TryAcquirePermit(DateTimeOffset now, int timeoutSeconds, out bool usedHalfOpenPermit)
    {
        usedHalfOpenPermit = false;
        if (CircuitState == TransparentProxyCircuitState.Open)
        {
            if (CircuitOpenUntil > now)
            {
                return false;
            }

            TransitionToHalfOpen();
        }

        if (CircuitState != TransparentProxyCircuitState.HalfOpen)
        {
            return true;
        }

        if (HalfOpenInFlight)
        {
            return false;
        }

        HalfOpenInFlight = true;
        usedHalfOpenPermit = true;
        return true;
    }

    public void ReleasePermit(bool usedHalfOpenPermit)
    {
        if (usedHalfOpenPermit)
        {
            HalfOpenInFlight = false;
        }
    }

    public DateTimeOffset TransitionToOpen(DateTimeOffset now, int timeoutSeconds)
    {
        CircuitState = TransparentProxyCircuitState.Open;
        CircuitOpenUntil = now.AddSeconds(Math.Max(1, timeoutSeconds));
        ConsecutiveSuccesses = 0;
        HalfOpenInFlight = false;
        return CircuitOpenUntil;
    }

    public void TransitionToHalfOpen()
    {
        if (CircuitState != TransparentProxyCircuitState.Open)
        {
            return;
        }

        CircuitState = TransparentProxyCircuitState.HalfOpen;
        ConsecutiveSuccesses = 0;
        HalfOpenInFlight = false;
    }

    public void TransitionToClosed()
    {
        CircuitState = TransparentProxyCircuitState.Closed;
        CircuitOpenUntil = DateTimeOffset.MinValue;
        ConsecutiveFailures = 0;
        ConsecutiveSuccesses = 0;
        CircuitWindowRequests = 0;
        CircuitWindowFailures = 0;
        HalfOpenInFlight = false;
    }

    public TransparentProxyRouteMetrics ToSnapshot()
        => new(
            Id,
            Name,
            Sent,
            Success,
            Failed,
            LastStatusCode,
            LastLatencyMs,
            ConsecutiveFailures,
            ConsecutiveSuccesses,
            CircuitState.ToString(),
            CircuitOpenUntil,
            LastSeenAt,
            PreferredWireApi,
            ChatCompletionsSupported,
            ResponsesSupported,
            AnthropicMessagesSupported,
            ProtocolCheckedAt);
}
