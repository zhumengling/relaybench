namespace RelayBench.Services;

internal sealed class TransparentProxyCooldownService
{
    public bool IsModelCooling(
        TransparentProxyRouteRuntimeState? state,
        string? modelName,
        DateTimeOffset now,
        out DateTimeOffset cooldownUntil)
    {
        cooldownUntil = DateTimeOffset.MinValue;
        return state is not null && state.IsModelCooling(modelName, now, out cooldownUntil);
    }

    public void RecordModelSuccess(TransparentProxyRouteRuntimeState state, string? modelName)
        => state.RecordModelSuccess(modelName);

    public bool RecordModelFailure(
        TransparentProxyRouteRuntimeState state,
        string? modelName,
        int statusCode,
        TimeSpan? retryAfter,
        int defaultCooldownSeconds)
    {
        var beforeCooling = state.IsModelCooling(modelName, DateTimeOffset.UtcNow, out var beforeUntil);
        state.RecordModelFailure(modelName, statusCode, retryAfter, Math.Max(15, defaultCooldownSeconds));
        var afterCooling = state.IsModelCooling(modelName, DateTimeOffset.UtcNow, out var afterUntil);
        return !beforeCooling && afterCooling ||
               beforeCooling && afterCooling && afterUntil > beforeUntil;
    }

    public TransparentProxyCooldownKind Classify(int statusCode, string? message = null)
    {
        if (statusCode == 404)
        {
            return TransparentProxyCooldownKind.ModelNotFound;
        }

        if (statusCode == 429)
        {
            return ContainsQuotaSignal(message)
                ? TransparentProxyCooldownKind.Quota
                : TransparentProxyCooldownKind.RateLimit;
        }

        if (statusCode is 408 or 425)
        {
            return TransparentProxyCooldownKind.Timeout;
        }

        if (statusCode == 503 || statusCode >= 500)
        {
            return TransparentProxyCooldownKind.ServerError;
        }

        return TransparentProxyCooldownKind.None;
    }

    public string ResolveReasonCode(int statusCode, string? message = null)
        => Classify(statusCode, message) switch
        {
            TransparentProxyCooldownKind.ModelNotFound => "model_not_found",
            TransparentProxyCooldownKind.Quota => "quota",
            TransparentProxyCooldownKind.RateLimit => "rate_limit",
            TransparentProxyCooldownKind.Timeout => "timeout",
            TransparentProxyCooldownKind.ServerError => "server_error",
            _ => "none"
        };

    private static bool ContainsQuotaSignal(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           (message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("billing", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("credits", StringComparison.OrdinalIgnoreCase));
}

internal enum TransparentProxyCooldownKind
{
    None,
    ModelNotFound,
    RateLimit,
    Quota,
    ServerError,
    Timeout
}
