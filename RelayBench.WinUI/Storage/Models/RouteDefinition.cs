namespace RelayBench.WinUI.Storage;

/// <summary>
/// Represents a proxy route definition persisted in SQLite.
/// The <see cref="ApiKeyProtected"/> field is stored encrypted via DPAPI (SecretProtector).
/// </summary>
public sealed record RouteDefinition(
    string Id,
    string Name,
    string UpstreamUrl,
    string? ApiKeyProtected,
    int Priority,
    string? ModelFilter,
    bool Enabled,
    DateTime UpdatedAtUtc,
    string? Prefix = null,
    string? OutboundProxy = null,
    int? RequestRetry = null,
    int? MaxRetryIntervalSeconds = null,
    int? ModelCooldownSeconds = null,
    string? ExcludedModelPatterns = null,
    string? PayloadRulesText = null,
    string? PreferredWireApi = null,
    string? HeadersText = null,
    string? AuthMode = null,
    string? OAuthProvider = null,
    string? OAuthCredentialId = null,
    string? CodexBackendBaseUrl = null,
    bool CodexOAuthFastMode = false);
