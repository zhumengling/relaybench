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
        string? sessionKey,
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
            sessionKey,
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
        string? sessionKey,
        CancellationTokenSource attemptCancellationSource)
    {
        if (!TryResolveHttpClientForRoute(route, config, out var routeClient, out var outboundProxyError))
        {
            var safeMessage = ProbeTraceRedactor.RedactText(outboundProxyError);
            MarkRouteFailure(route, 502, routeStopwatch.ElapsedMilliseconds, routePermit, config: config);
            requestAttemptSummaries.Add($"{route.Name}/proxy-config-invalid");
            EmitLog(new TransparentProxyLogEntry(
                DateTimeOffset.Now,
                "WARN",
                method,
                pathAndQuery,
                route.Name,
                502,
                routeStopwatch.ElapsedMilliseconds,
                $"Invalid outbound proxy configuration: {safeMessage}",
                string.Empty,
                requestId,
                "proxy",
                string.Join(" > ", requestAttemptSummaries)));
            return new TransparentProxyUpstreamAttempt(false, route.Name, 502, $"Invalid outbound proxy configuration: {safeMessage}");
        }

        var activeRouteClient = routeClient ?? throw new InvalidOperationException("Transparent proxy HTTP client is not ready.");
        var maxRequestRetries = _retryOrchestrator.ResolveRouteRequestRetry(route, config);
        var lastLogModel = string.Empty;
        TransparentProxyUpstreamAttempt? lastProtocolAttempt = null;
        var preparedRequests = _protocolTranslator.BuildPreparedUpstreamRequests(
            method,
            pathAndQuery,
            requestBody,
            route,
            streamRequested,
            sessionKey,
            context.Request.Headers);
        if (preparedRequests.Count == 0)
        {
            ReleaseRoutePermit(route, routePermit);
            requestAttemptSummaries.Add($"{route.Name}/unsupported-endpoint");
            EmitLog(new TransparentProxyLogEntry(
                DateTimeOffset.Now,
                "WARN",
                method,
                pathAndQuery,
                route.Name,
                501,
                routeStopwatch.ElapsedMilliseconds,
                "Route does not support this request endpoint; trying next route.",
                string.Empty,
                requestId,
                "unsupported",
                string.Join(" > ", requestAttemptSummaries)));
            return new TransparentProxyUpstreamAttempt(false, route.Name, 501, "Route does not support this request endpoint.");
        }

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
                TransparentProxyRouteAuthMaterial? authMaterial;
                try
                {
                    authMaterial = await BuildRouteAuthMaterialAsync(route, streamRequested, attemptCancellationSource.Token);
                }
                catch (Exception ex)
                {
                    var safeMessage = ProbeTraceRedactor.RedactText(ex.Message);
                    MarkRouteFailure(route, 401, routeStopwatch.ElapsedMilliseconds, routePermit, modelName: lastLogModel, config: config);
                    EmitLog(new TransparentProxyLogEntry(
                        DateTimeOffset.Now,
                        "WARN",
                        method,
                        pathAndQuery,
                        route.Name,
                        401,
                        routeStopwatch.ElapsedMilliseconds,
                        $"Codex OAuth credential is unavailable: {safeMessage}",
                        lastLogModel,
                        requestId,
                        prepared.WireApi,
                        string.Join(" > ", requestAttemptSummaries)));
                    return new TransparentProxyUpstreamAttempt(false, route.Name, 401, "Codex OAuth credential is unavailable.");
                }

                using var upstreamRequest = TransparentProxyUpstreamRequestFactory.Create(
                    context.Request,
                    method,
                    prepared.UpstreamUrl,
                    route,
                    prepared.Body,
                    prepared.WireApi,
                    prepared.ExtraHeaders,
                    authMaterial);

                HttpResponseMessage upstreamResponse;
                try
                {
                    upstreamResponse = await activeRouteClient.SendAsync(
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
                    if (route.IsCodexOAuth &&
                        (statusCode == 401 || statusCode == 403) &&
                        sendAttempt == 0)
                    {
                        try
                        {
                            await _codexOAuthService.ForceRefreshAsync(route.OAuthCredentialId, attemptCancellationSource.Token);
                            EmitLog(new TransparentProxyLogEntry(
                                DateTimeOffset.Now,
                                "WARN",
                                method,
                                pathAndQuery,
                                route.Name,
                                statusCode,
                                routeStopwatch.ElapsedMilliseconds,
                                "Codex OAuth token was rejected; refreshed token and retrying once.",
                                logModel,
                                requestId,
                                prepared.WireApi,
                                string.Join(" > ", requestAttemptSummaries)));
                            continue;
                        }
                        catch (Exception ex)
                        {
                            EmitLog(new TransparentProxyLogEntry(
                                DateTimeOffset.Now,
                                "WARN",
                                method,
                                pathAndQuery,
                                route.Name,
                                statusCode,
                                routeStopwatch.ElapsedMilliseconds,
                                $"Codex OAuth refresh failed after {statusCode}: {ProbeTraceRedactor.RedactText(ex.Message)}",
                                logModel,
                                requestId,
                                prepared.WireApi,
                                string.Join(" > ", requestAttemptSummaries)));
                        }
                    }

                    if (route.IsCodexOAuth &&
                        (statusCode == 401 || statusCode == 403) &&
                        sendAttempt > 0)
                    {
                        _codexOAuthService.MarkCredentialRejected(
                            route.OAuthCredentialId,
                            $"Codex OAuth token was rejected after forced refresh with status {statusCode}.");
                        EmitLog(new TransparentProxyLogEntry(
                            DateTimeOffset.Now,
                            "WARN",
                            method,
                            pathAndQuery,
                            route.Name,
                            statusCode,
                            routeStopwatch.ElapsedMilliseconds,
                            "Codex OAuth token was rejected after refresh; account marked for re-login.",
                            logModel,
                            requestId,
                            prepared.WireApi,
                            string.Join(" > ", requestAttemptSummaries)));
                    }

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
                            prepared.ResponseClientWireApi,
                            prepared.ResponseModel,
                            logModel,
                            prepared.NormalizeToChatCompletions,
                            prepared.IsToolExchange,
                            prepared.PreferJsonStreamExtraction,
                            prepared.ToolNameAliases,
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
                        $"Routed to {route.Name} using {DisplayWireApi(prepared.WireApi)}{BuildAuthLogSuffix(authMaterial)}.",
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

    private static string BuildAuthLogSuffix(TransparentProxyRouteAuthMaterial? authMaterial)
        => authMaterial?.IsCodexOAuth == true
            ? $" via Codex OAuth {authMaterial.AccountLabel}"
            : string.Empty;

    private void EmitCodexOAuthBackgroundLog(string message)
    {
        var level = message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("stopped", StringComparison.OrdinalIgnoreCase)
            ? "WARN"
            : "INFO";
        EmitLog(new TransparentProxyLogEntry(
            DateTimeOffset.Now,
            level,
            "-",
            "/relaybench/oauth/codex",
            "Codex OAuth",
            string.Equals(level, "WARN", StringComparison.Ordinal) ? 502 : 200,
            0,
            ProbeTraceRedactor.RedactText(message)));
    }

    private async Task<TransparentProxyRouteAuthMaterial?> BuildRouteAuthMaterialAsync(
        TransparentProxyRoute route,
        bool streamRequested,
        CancellationToken cancellationToken)
    {
        if (!route.IsCodexOAuth)
        {
            return null;
        }

        var material = await _codexOAuthService.EnsureAccessTokenAsync(route.OAuthCredentialId, cancellationToken);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = streamRequested ? "text/event-stream" : "application/json",
            ["Connection"] = "Keep-Alive",
            ["Originator"] = CodexOAuthConstants.Originator,
            ["User-Agent"] = CodexOAuthConstants.UserAgent,
            ["Session_id"] = Guid.NewGuid().ToString()
        };
        if (!string.IsNullOrWhiteSpace(material.AccountId))
        {
            headers["Chatgpt-Account-Id"] = material.AccountId;
        }

        return new TransparentProxyRouteAuthMaterial(
            TransparentProxyRouteAuthModes.CodexOAuth,
            material.BearerToken,
            headers,
            material.AccountLabel);
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

}
