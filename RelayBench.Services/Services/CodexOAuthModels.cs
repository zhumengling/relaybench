using System.Security.Cryptography;
using System.Text;

namespace RelayBench.Services;

internal enum CodexOAuthCredentialState
{
    Ready,
    Refreshing,
    RefreshBackoff,
    NeedsRelogin,
    Disabled
}

internal sealed class CodexOAuthCredential
{
    public string Id { get; set; } = string.Empty;

    public string Provider { get; set; } = CodexOAuthConstants.Provider;

    public string Email { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public string AccountIdHash { get; set; } = string.Empty;

    public string PlanType { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public string IdToken { get; set; } = string.Empty;

    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    public DateTimeOffset? LastRefreshAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public CodexOAuthCredentialState State { get; set; } = CodexOAuthCredentialState.Ready;

    public string LastError { get; set; } = string.Empty;

    public DateTimeOffset? RefreshBackoffUntil { get; set; }

    public int RefreshFailureCount { get; set; }

    public bool QuotaExceeded { get; set; }

    public string QuotaReason { get; set; } = string.Empty;

    public DateTimeOffset? QuotaNextRecoverAt { get; set; }

    public int QuotaBackoffLevel { get; set; }

    public DateTimeOffset? QuotaLastCheckedAt { get; set; }

    public string QuotaLastError { get; set; } = string.Empty;

    public CodexOAuthCredential Clone()
        => new()
        {
            Id = Id,
            Provider = Provider,
            Email = Email,
            AccountId = AccountId,
            AccountIdHash = AccountIdHash,
            PlanType = PlanType,
            AccessToken = AccessToken,
            RefreshToken = RefreshToken,
            IdToken = IdToken,
            AccessTokenExpiresAt = AccessTokenExpiresAt,
            LastRefreshAt = LastRefreshAt,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            State = State,
            LastError = LastError,
            RefreshBackoffUntil = RefreshBackoffUntil,
            RefreshFailureCount = RefreshFailureCount,
            QuotaExceeded = QuotaExceeded,
            QuotaReason = QuotaReason,
            QuotaNextRecoverAt = QuotaNextRecoverAt,
            QuotaBackoffLevel = QuotaBackoffLevel,
            QuotaLastCheckedAt = QuotaLastCheckedAt,
            QuotaLastError = QuotaLastError
        };

    public bool IsQuotaCooling(DateTimeOffset now)
        => QuotaExceeded &&
           QuotaNextRecoverAt is { } recoverAt &&
           recoverAt > now;

    public bool ShouldRecoverQuota(DateTimeOffset now)
        => QuotaExceeded &&
           (QuotaNextRecoverAt is null || QuotaNextRecoverAt <= now);

    public string DisplayName
        => !string.IsNullOrWhiteSpace(Email)
            ? MaskEmail(Email)
            : !string.IsNullOrWhiteSpace(AccountIdHash)
                ? $"Codex {AccountIdHash}"
                : Id;

    public static string BuildStableId(string email, string planType, string accountId)
    {
        var normalizedEmail = (email ?? string.Empty).Trim();
        var normalizedPlan = NormalizeForId(planType);
        var accountHash = HashAccountId(accountId);
        if (string.Equals(normalizedPlan, "team", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(accountHash))
        {
            return $"codex-{accountHash}-{normalizedEmail}-{normalizedPlan}";
        }

        return string.IsNullOrWhiteSpace(normalizedPlan)
            ? $"codex-{normalizedEmail}"
            : $"codex-{normalizedEmail}-{normalizedPlan}";
    }

    public static string HashAccountId(string accountId)
    {
        var normalized = (accountId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest[..4]).ToLowerInvariant();
    }

    public static string MaskEmail(string email)
    {
        var normalized = (email ?? string.Empty).Trim();
        var at = normalized.IndexOf('@');
        if (at <= 0)
        {
            return normalized.Length <= 4
                ? "***"
                : $"{normalized[..Math.Min(3, normalized.Length)]}***";
        }

        var name = normalized[..at];
        var domain = normalized[(at + 1)..];
        var prefix = name.Length <= 3 ? name : name[..3];
        return $"{prefix}***@{domain}";
    }

    private static string NormalizeForId(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in (value ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }
}

internal sealed record CodexOAuthTokenResponse(
    string AccessToken,
    string RefreshToken,
    string IdToken,
    int ExpiresIn);

internal sealed record CodexOAuthAuthMaterial(
    string BearerToken,
    string AccountId,
    string AccountLabel,
    DateTimeOffset? ExpiresAt);

internal sealed record CodexOAuthImportResult(
    CodexOAuthCredential Credential,
    bool Refreshed,
    string RefreshError);

internal sealed record CodexOAuthImportBatchResult(
    IReadOnlyList<CodexOAuthImportResult> Imported,
    IReadOnlyList<CodexOAuthImportFailure> Failed);

internal sealed record CodexOAuthImportFailure(
    string FileName,
    string Error);

internal sealed record CodexOAuthCallbackResult(
    string Code,
    string State,
    string Error,
    string ErrorDescription);

internal sealed class CodexOAuthLoginSession : IDisposable
{
    private readonly TaskCompletionSource<CodexOAuthCallbackResult> _callbackCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellationSource = new();
    private bool _disposed;

    public CodexOAuthLoginSession(
        string id,
        string state,
        string codeVerifier,
        string authUrl,
        CodexOAuthCallbackServer callbackServer)
    {
        Id = id;
        State = state;
        CodeVerifier = codeVerifier;
        AuthUrl = authUrl;
        CallbackServer = callbackServer;
    }

    public string Id { get; }

    public string State { get; }

    public string CodeVerifier { get; }

    public string AuthUrl { get; }

    public CodexOAuthCallbackServer CallbackServer { get; }

    public bool CallbackServerStarted { get; internal set; } = true;

    public bool BrowserOpened { get; internal set; } = true;

    public CancellationToken CancellationToken => _cancellationSource.Token;

    public Task<CodexOAuthCallbackResult> CallbackTask => _callbackCompletion.Task;

    public bool TryComplete(CodexOAuthCallbackResult result)
        => _callbackCompletion.TrySetResult(result);

    public bool TryFail(Exception exception)
        => _callbackCompletion.TrySetException(exception);

    public void Cancel()
    {
        if (!_cancellationSource.IsCancellationRequested)
        {
            _cancellationSource.Cancel();
        }

        _callbackCompletion.TrySetCanceled(_cancellationSource.Token);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
        CallbackServer.Dispose();
        _cancellationSource.Dispose();
    }
}

internal static class CodexOAuthConstants
{
    public const string Provider = "codex";
    public const string AuthUrl = "https://auth.openai.com/oauth/authorize";
    public const string TokenUrl = "https://auth.openai.com/oauth/token";
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string RedirectUri = "http://localhost:1455/auth/callback";
    public const string DefaultBackendBaseUrl = "https://chatgpt.com/backend-api/codex";
    public const string Originator = "codex_cli_rs";
    public const string UserAgent = "codex_cli_rs/0.118.0 (Mac OS 26.3.1; arm64) iTerm.app/3.6.9";
}
