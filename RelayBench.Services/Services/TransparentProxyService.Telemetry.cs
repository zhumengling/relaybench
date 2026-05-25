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
        var traceId = string.IsNullOrWhiteSpace(entry.TraceId) ? entry.RequestId : entry.TraceId;
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
            entry.WasTunnelOnly || ingressContext.WasTunnelOnly,
            ProbeTraceRedactor.RedactText(NormalizeErrorType(entry)),
            ProbeTraceRedactor.RedactText(NormalizeCacheState(entry)),
            Math.Max(0, entry.InputTokens),
            Math.Max(0, entry.OutputTokens),
            Math.Max(0, entry.CacheTokens),
            ProbeTraceRedactor.RedactText(traceId));
        AddRecentLog(safeEntry);
        UpdateIngressMetricsFromLog(safeEntry);
        LogEmitted?.Invoke(this, safeEntry);
    }

    private static string NormalizeErrorType(TransparentProxyLogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ErrorType))
        {
            return entry.ErrorType.Trim();
        }

        var message = entry.Message ?? string.Empty;
        if (entry.StatusCode == 429)
        {
            return message.Contains("concurrency", StringComparison.OrdinalIgnoreCase)
                ? "concurrency_limit"
                : "rate_limit";
        }

        if (message.Contains("open-circuit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("circuit open", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("circuit_open", StringComparison.OrdinalIgnoreCase))
        {
            return "circuit_open";
        }

        if (message.Contains("not available on any route", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "model_not_found";
        }

        if (message.Contains("OAuth", StringComparison.OrdinalIgnoreCase) ||
            entry.StatusCode is 401 or 403)
        {
            return "auth";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            entry.StatusCode == 504)
        {
            return "timeout";
        }

        if (message.Contains("Connection failed", StringComparison.OrdinalIgnoreCase))
        {
            return "connection";
        }

        if (entry.StatusCode >= 500)
        {
            return "upstream_unavailable";
        }

        return entry.StatusCode >= 400 ? "upstream_error" : string.Empty;
    }

    private static string NormalizeCacheState(TransparentProxyLogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.CacheState))
        {
            return entry.CacheState.Trim().ToUpperInvariant();
        }

        var level = entry.Level ?? string.Empty;
        var wireApi = entry.WireApi ?? string.Empty;
        var message = entry.Message ?? string.Empty;
        var attemptSummary = entry.AttemptSummary ?? string.Empty;
        if (!string.Equals(level, "CACHE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(wireApi, "cache", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (message.Contains("hit", StringComparison.OrdinalIgnoreCase) ||
            attemptSummary.Contains("hit", StringComparison.OrdinalIgnoreCase) ||
            attemptSummary.Contains("local response cache", StringComparison.OrdinalIgnoreCase))
        {
            return "HIT";
        }

        if (message.Contains("miss", StringComparison.OrdinalIgnoreCase) ||
            attemptSummary.Contains("miss", StringComparison.OrdinalIgnoreCase))
        {
            return "MISS";
        }

        if (message.Contains("bypass", StringComparison.OrdinalIgnoreCase))
        {
            return "BYPASS";
        }

        return string.Empty;
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
        response.Headers["Access-Control-Allow-Headers"] = "authorization, content-type, x-api-key, x-management-key, x-relaybench-management-key, anthropic-version, anthropic-beta, openai-beta, idempotency-key, session_id, session-id, x-session-id, x-conversation-id, openai-conversation-id, x-amp-thread-id, x-client-request-id";
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
