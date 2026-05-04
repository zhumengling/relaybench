namespace RelayBench.App.Services;

internal sealed class TransparentProxyCircuitBreakerService
{
    private const int FailureThreshold = 4;
    private const int SuccessThreshold = 2;
    private const int TimeoutSeconds = 60;
    private const int MaxCooldownSeconds = 30 * 60;
    private const int MinRequests = 10;
    private const double ErrorRateThreshold = 0.60d;

    public bool IsRouteAvailable(TransparentProxyRouteRuntimeState state, DateTimeOffset now)
        => state.IsCircuitAvailable(now);

    public bool TryAcquirePermit(
        TransparentProxyRouteRuntimeState? state,
        string routeId,
        bool bypassCircuitBreaker,
        out TransparentProxyRoutePermit routePermit)
    {
        routePermit = new TransparentProxyRoutePermit(routeId, UsedHalfOpenPermit: false);
        if (bypassCircuitBreaker || state is null)
        {
            return true;
        }

        var allowed = state.TryAcquirePermit(DateTimeOffset.UtcNow, out var usedHalfOpenPermit);
        routePermit = new TransparentProxyRoutePermit(routeId, usedHalfOpenPermit);
        return allowed;
    }

    public TransparentProxyCircuitEvent RecordSuccess(
        TransparentProxyRouteRuntimeState state,
        int statusCode,
        long latencyMs,
        TransparentProxyRoutePermit routePermit)
    {
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
            if (state.ConsecutiveSuccesses >= SuccessThreshold)
            {
                state.TransitionToClosed();
                return TransparentProxyCircuitEvent.Recovered;
            }
        }
        else if (state.CircuitState == TransparentProxyCircuitState.Closed)
        {
            state.ConsecutiveSuccesses = 0;
        }

        return TransparentProxyCircuitEvent.None;
    }

    public TransparentProxyCircuitEvent RecordFailure(
        TransparentProxyRouteRuntimeState state,
        int statusCode,
        long latencyMs,
        TransparentProxyRoutePermit routePermit,
        TimeSpan? retryAfter = null)
    {
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
        var cooldownSeconds = ResolveCooldownSeconds(retryAfter);
        var explicitCooldown = retryAfter is not null && statusCode is 429 or 503;
        if (state.CircuitState == TransparentProxyCircuitState.HalfOpen)
        {
            var retryAt = state.TransitionToOpen(now, cooldownSeconds);
            return new TransparentProxyCircuitEvent(true, true, retryAt);
        }

        if (explicitCooldown ||
            (state.CircuitState == TransparentProxyCircuitState.Closed && ShouldOpenCircuit(state)))
        {
            var retryAt = state.TransitionToOpen(now, cooldownSeconds);
            return new TransparentProxyCircuitEvent(true, false, retryAt);
        }

        return TransparentProxyCircuitEvent.None;
    }

    public void Reset(TransparentProxyRouteRuntimeState state)
        => state.TransitionToClosed();

    private static bool ShouldOpenCircuit(TransparentProxyRouteRuntimeState state)
    {
        if (state.ConsecutiveFailures >= FailureThreshold)
        {
            return true;
        }

        return state.CircuitWindowRequests >= MinRequests &&
               state.CircuitWindowFailures / (double)Math.Max(1, state.CircuitWindowRequests) >= ErrorRateThreshold;
    }

    private static int ResolveCooldownSeconds(TimeSpan? retryAfter)
    {
        if (retryAfter is null)
        {
            return TimeoutSeconds;
        }

        var seconds = (int)Math.Ceiling(retryAfter.Value.TotalSeconds);
        return Math.Clamp(seconds, 1, MaxCooldownSeconds);
    }
}

internal readonly record struct TransparentProxyCircuitEvent(
    bool Opened,
    bool HalfOpenFailed,
    DateTimeOffset RetryAt)
{
    public static TransparentProxyCircuitEvent None => new(false, false, DateTimeOffset.MinValue);

    public static TransparentProxyCircuitEvent Recovered => new(false, false, DateTimeOffset.MinValue);
}

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

    public Dictionary<string, TransparentProxyModelRuntimeState> ModelStates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void ApplyProtocol(TransparentProxyRoute route)
    {
        PreferredWireApi = route.PreferredWireApi;
        ChatCompletionsSupported = route.ChatCompletionsSupported;
        ResponsesSupported = route.ResponsesSupported;
        AnthropicMessagesSupported = route.AnthropicMessagesSupported;
        ProtocolCheckedAt = route.ProtocolCheckedAt;
    }

    public void ApplyHealthSnapshot(TransparentProxyRouteHealthSnapshot snapshot)
    {
        Sent = Math.Max(0, snapshot.Sent);
        Success = Math.Max(0, snapshot.Success);
        Failed = Math.Max(0, snapshot.Failed);
        LastStatusCode = Math.Max(0, snapshot.LastStatusCode);
        LastLatencyMs = Math.Max(0, snapshot.LastLatencyMs);
        ConsecutiveFailures = Math.Max(0, snapshot.ConsecutiveFailures);
        ConsecutiveSuccesses = Math.Max(0, snapshot.ConsecutiveSuccesses);
        CircuitWindowRequests = Math.Max(0, snapshot.CircuitWindowRequests);
        CircuitWindowFailures = Math.Max(0, snapshot.CircuitWindowFailures);
        CircuitState = snapshot.CircuitState;
        CircuitOpenUntil = snapshot.CircuitOpenUntil;
        HalfOpenInFlight = false;
        LastSeenAt = snapshot.LastSeenAt;
    }

    public bool IsCircuitAvailable(DateTimeOffset now)
    {
        if (CircuitState == TransparentProxyCircuitState.Open && CircuitOpenUntil <= now)
        {
            TransitionToHalfOpen();
        }

        return CircuitState != TransparentProxyCircuitState.Open;
    }

    public bool TryAcquirePermit(DateTimeOffset now, out bool usedHalfOpenPermit)
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

    public bool IsModelCooling(string? modelName, DateTimeOffset now, out DateTimeOffset cooldownUntil)
    {
        cooldownUntil = DateTimeOffset.MinValue;
        var key = NormalizeModelKey(modelName);
        if (string.IsNullOrWhiteSpace(key) || !ModelStates.TryGetValue(key, out var state))
        {
            return false;
        }

        if (state.CooldownUntil <= now)
        {
            if (state.ConsecutiveFailures <= 0)
            {
                ModelStates.Remove(key);
            }

            return false;
        }

        cooldownUntil = state.CooldownUntil;
        return true;
    }

    public void RecordModelSuccess(string? modelName)
    {
        var key = NormalizeModelKey(modelName);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        ModelStates.Remove(key);
    }

    public void RecordModelFailure(
        string? modelName,
        int statusCode,
        TimeSpan? retryAfter,
        int defaultCooldownSeconds)
    {
        var key = NormalizeModelKey(modelName);
        if (string.IsNullOrWhiteSpace(key) || !ShouldCoolModel(statusCode))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        ModelStates.TryGetValue(key, out var state);
        var failures = Math.Min(8, (state?.ConsecutiveFailures ?? 0) + 1);
        var retrySeconds = retryAfter is { } delay && delay > TimeSpan.Zero
            ? (int)Math.Ceiling(delay.TotalSeconds)
            : Math.Max(15, defaultCooldownSeconds) * Math.Pow(2, Math.Min(3, failures - 1));
        var cooldownSeconds = Math.Clamp((int)Math.Ceiling(retrySeconds), 15, 30 * 60);
        ModelStates[key] = new TransparentProxyModelRuntimeState(failures, now.AddSeconds(cooldownSeconds));
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

    private static string NormalizeModelKey(string? modelName)
    {
        var normalized = (modelName ?? string.Empty).Trim();
        var arrow = normalized.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            normalized = normalized[(arrow + 2)..].Trim();
        }

        return string.IsNullOrWhiteSpace(normalized) || normalized == "-"
            ? string.Empty
            : normalized.ToLowerInvariant();
    }

    private static bool ShouldCoolModel(int statusCode)
        => statusCode is 429 or 503 || statusCode >= 500;
}

internal sealed record TransparentProxyRoutePermit(string RouteId, bool UsedHalfOpenPermit);

internal sealed record TransparentProxyModelRuntimeState(
    int ConsecutiveFailures,
    DateTimeOffset CooldownUntil);
