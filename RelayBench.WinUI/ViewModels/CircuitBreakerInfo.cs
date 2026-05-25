namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents the circuit breaker state for a route.
/// Maps from the internal TransparentProxyCircuitState enum in the Services layer.
/// </summary>
public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>
/// Holds circuit breaker information for display in the route queue.
/// The badge is only shown when State != Closed.
/// </summary>
public sealed record CircuitBreakerInfo(CircuitState State, int ConsecutiveFailures)
{
    /// <summary>
    /// Whether the badge should be visible (state is not Closed).
    /// </summary>
    public bool IsVisible => State != CircuitState.Closed;

    /// <summary>
    /// Display label for the circuit state (e.g., "Open", "Half-Open").
    /// </summary>
    public string StateLabel => State switch
    {
        CircuitState.Open => "熔断",
        CircuitState.HalfOpen => "半开",
        _ => string.Empty
    };

    /// <summary>
    /// Combined badge text showing state and failure count (e.g., "Open (5)").
    /// </summary>
    public string BadgeText => IsVisible ? $"{StateLabel} ({ConsecutiveFailures})" : string.Empty;

    /// <summary>
    /// Whether the circuit is in Open state (for red coloring).
    /// </summary>
    public bool IsOpen => State == CircuitState.Open;

    /// <summary>
    /// Whether the circuit is in HalfOpen state (for orange coloring).
    /// </summary>
    public bool IsHalfOpen => State == CircuitState.HalfOpen;
}
