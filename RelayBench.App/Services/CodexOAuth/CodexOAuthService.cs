using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

internal sealed class CodexOAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan AccessTokenRefreshLead = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LongLivedRefreshLead = TimeSpan.FromDays(5);
    private static readonly TimeSpan RefreshPendingBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] RefreshFailureBackoffs =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30)
    ];
    private static readonly TimeSpan BackgroundRefreshInterval = TimeSpan.FromMinutes(30);

    private readonly CodexOAuthCredentialStore _store;
    private readonly HttpClient _httpClient;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, CodexOAuthCredential> _credentials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _backgroundRefreshCts;
    private Task? _backgroundRefreshTask;

    public CodexOAuthService()
        : this(new CodexOAuthCredentialStore(), new HttpClient())
    {
    }

    public CodexOAuthService(CodexOAuthCredentialStore store, HttpClient httpClient)
    {
        _store = store;
        _httpClient = httpClient;
        Reload();
    }

    public event EventHandler? CredentialsChanged;

    public IReadOnlyList<CodexOAuthCredential> GetCredentials()
    {
        lock (_syncRoot)
        {
            return _credentials.Values
                .OrderBy(static item => item.Email, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.PlanType, StringComparer.OrdinalIgnoreCase)
                .Select(static item => item.Clone())
                .ToArray();
        }
    }

    public void Reload()
    {
        var recovered = false;
        lock (_syncRoot)
        {
            _credentials.Clear();
            var now = DateTimeOffset.UtcNow;
            foreach (var credential in _store.Load())
            {
                if (credential.State == CodexOAuthCredentialState.Refreshing)
                {
                    credential.State = CodexOAuthCredentialState.RefreshBackoff;
                    credential.RefreshFailureCount = Math.Max(1, credential.RefreshFailureCount);
                    credential.RefreshBackoffUntil = now.Add(ResolveRefreshBackoff(credential.RefreshFailureCount));
                    credential.LastError = string.IsNullOrWhiteSpace(credential.LastError)
                        ? "上次刷新未完成，已等待重试。"
                        : credential.LastError;
                    credential.UpdatedAt = now;
                    recovered = true;
                }
                else if (credential.State == CodexOAuthCredentialState.RefreshBackoff &&
                         credential.RefreshBackoffUntil is null)
                {
                    credential.RefreshFailureCount = Math.Max(1, credential.RefreshFailureCount);
                    credential.RefreshBackoffUntil = now.Add(ResolveRefreshBackoff(credential.RefreshFailureCount));
                    credential.UpdatedAt = now;
                    recovered = true;
                }

                _credentials[credential.Id] = credential;
            }

            if (recovered)
            {
                SaveLocked();
            }
        }

        CredentialsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<CodexOAuthLoginSession> BeginLoginAsync(CancellationToken cancellationToken)
    {
        var pkce = CodexOAuthPkceService.Generate();
        var state = CodexOAuthPkceService.GenerateState();
        CodexOAuthLoginSession? session = null;
        var authUrl = BuildAuthUrl(state, pkce.CodeChallenge);
        var callbackServer = new CodexOAuthCallbackServer(result => session?.TryComplete(result) == true);
        session = new CodexOAuthLoginSession(Guid.NewGuid().ToString("N"), state, pkce.CodeVerifier, authUrl, callbackServer);

        var callbackServerStarted = true;
        try
        {
            callbackServer.Start();
        }
        catch (Exception ex)
        {
            callbackServerStarted = false;
            AppDiagnosticLog.Write("CodexOAuthService.CallbackServerStart", ex);
        }

        session.CallbackServerStarted = callbackServerStarted;
        session.BrowserOpened = TryOpenBrowser(authUrl);
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return session;
    }

    public async Task<CodexOAuthCredential> CompleteLoginAsync(
        CodexOAuthLoginSession session,
        CancellationToken cancellationToken,
        Action? authorizationCodeAccepted = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken, cancellationToken);
        linked.CancelAfter(TimeSpan.FromMinutes(5));
        var result = await session.CallbackTask.WaitAsync(linked.Token);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.ErrorDescription)
                ? $"Codex OAuth failed: {result.Error}"
                : $"Codex OAuth failed: {result.Error}: {result.ErrorDescription}");
        }

        if (!string.Equals(result.State, session.State, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex OAuth state validation failed.");
        }

        if (string.IsNullOrWhiteSpace(result.Code))
        {
            throw new InvalidOperationException("Codex OAuth callback did not include an authorization code.");
        }

        authorizationCodeAccepted?.Invoke();
        var token = await ExchangeCodeAsync(result.Code, session.CodeVerifier, cancellationToken);
        var credential = BuildCredential(token);
        UpsertCredential(credential);
        return credential.Clone();
    }

    public bool SubmitManualCallback(CodexOAuthLoginSession? session, string callbackUrl)
    {
        if (session is null || string.IsNullOrWhiteSpace(callbackUrl))
        {
            return false;
        }

        return session.TryComplete(CodexOAuthCallbackServer.ParseCallback(callbackUrl));
    }

    public async Task<CodexOAuthAuthMaterial> EnsureAccessTokenAsync(string credentialId, CancellationToken cancellationToken)
        => await EnsureAccessTokenCoreAsync(credentialId, forceRefresh: false, cancellationToken);

    public async Task<CodexOAuthAuthMaterial> ForceRefreshAsync(string credentialId, CancellationToken cancellationToken)
        => await EnsureAccessTokenCoreAsync(credentialId, forceRefresh: true, cancellationToken);

    public async Task RefreshCredentialAsync(string credentialId, CancellationToken cancellationToken)
    {
        await ForceRefreshAsync(credentialId, cancellationToken);
    }

    public void StartBackgroundRefreshLoop(Action<string>? log = null)
    {
        lock (_syncRoot)
        {
            if (_backgroundRefreshTask is not null)
            {
                return;
            }

            _backgroundRefreshCts = new CancellationTokenSource();
            _backgroundRefreshTask = Task.Run(() => RunBackgroundRefreshLoopAsync(log, _backgroundRefreshCts.Token));
        }
    }

    public async Task StopBackgroundRefreshLoopAsync()
    {
        CancellationTokenSource? cancellationSource;
        Task? backgroundTask;
        lock (_syncRoot)
        {
            cancellationSource = _backgroundRefreshCts;
            backgroundTask = _backgroundRefreshTask;
            _backgroundRefreshCts = null;
            _backgroundRefreshTask = null;
        }

        if (cancellationSource is null)
        {
            return;
        }

        try
        {
            await cancellationSource.CancelAsync();
            if (backgroundTask is not null)
            {
                await backgroundTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("CodexOAuthService.BackgroundRefreshStop", ex);
        }
        finally
        {
            cancellationSource.Dispose();
        }
    }

    public void DisableCredential(string credentialId, bool disabled)
    {
        lock (_syncRoot)
        {
            if (_credentials.TryGetValue(credentialId, out var credential))
            {
                credential.State = disabled ? CodexOAuthCredentialState.Disabled : CodexOAuthCredentialState.Ready;
                if (!disabled)
                {
                    credential.LastError = string.Empty;
                    credential.RefreshBackoffUntil = null;
                    credential.RefreshFailureCount = 0;
                }

                credential.UpdatedAt = DateTimeOffset.UtcNow;
                SaveLocked();
            }
        }

        CredentialsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteCredential(string credentialId)
    {
        lock (_syncRoot)
        {
            _credentials.Remove(credentialId);
            _refreshLocks.Remove(credentialId);
            SaveLocked();
        }

        CredentialsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkCredentialRejected(string credentialId, string reason)
    {
        lock (_syncRoot)
        {
            if (_credentials.TryGetValue(credentialId, out var credential))
            {
                credential.State = CodexOAuthCredentialState.NeedsRelogin;
                credential.LastError = ProbeTraceRedactor.RedactText(reason);
                credential.RefreshBackoffUntil = null;
                credential.RefreshFailureCount = Math.Max(1, credential.RefreshFailureCount);
                credential.UpdatedAt = DateTimeOffset.UtcNow;
                SaveLocked();
            }
        }

        CredentialsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<CodexOAuthAuthMaterial> EnsureAccessTokenCoreAsync(
        string credentialId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var credential = GetCredentialOrThrow(credentialId);
        if (credential.State is CodexOAuthCredentialState.Disabled or CodexOAuthCredentialState.NeedsRelogin)
        {
            throw new InvalidOperationException($"Codex OAuth credential {credential.DisplayName} is not ready.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && IsWaitingBackoff(credential, now))
        {
            if (HasUsableAccessToken(credential, now))
            {
                return BuildAuthMaterial(credential);
            }

            throw new InvalidOperationException(BuildBackoffMessage(credential, now));
        }

        var retryBackoffElapsed = IsRefreshBackoffElapsed(credential, now);
        if (!forceRefresh && !retryBackoffElapsed && !ShouldRefresh(credential, now))
        {
            return BuildAuthMaterial(credential);
        }

        var gate = GetRefreshLock(credentialId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            credential = GetCredentialOrThrow(credentialId);
            now = DateTimeOffset.UtcNow;
            if (!forceRefresh && IsWaitingBackoff(credential, now))
            {
                if (HasUsableAccessToken(credential, now))
                {
                    return BuildAuthMaterial(credential);
                }

                throw new InvalidOperationException(BuildBackoffMessage(credential, now));
            }

            retryBackoffElapsed = IsRefreshBackoffElapsed(credential, now);
            if (!forceRefresh && !retryBackoffElapsed && !ShouldRefresh(credential, now))
            {
                return BuildAuthMaterial(credential);
            }

            if (credential.State == CodexOAuthCredentialState.RefreshBackoff &&
                credential.RefreshBackoffUntil is { } backoffUntil &&
                backoffUntil > DateTimeOffset.UtcNow &&
                credential.AccessTokenExpiresAt is { } expiresAt &&
                expiresAt > DateTimeOffset.UtcNow)
            {
                return BuildAuthMaterial(credential);
            }

            SetCredentialState(credentialId, CodexOAuthCredentialState.Refreshing, string.Empty, DateTimeOffset.UtcNow.Add(RefreshPendingBackoff));
            var refreshed = await RefreshWithRetryAsync(credential, cancellationToken);
            UpsertCredential(refreshed);
            return BuildAuthMaterial(refreshed);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunBackgroundRefreshLoopAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        try
        {
            await RunBackgroundRefreshScanAsync(log, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(BackgroundRefreshInterval, cancellationToken);
                await RunBackgroundRefreshScanAsync(log, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("CodexOAuthService.BackgroundRefreshLoop", ex);
            log?.Invoke($"Codex OAuth background refresh stopped: {ProbeTraceRedactor.RedactText(ex.Message)}");
        }
    }

    private async Task RunBackgroundRefreshScanAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = GetCredentials()
            .Where(item => item.State is not CodexOAuthCredentialState.Disabled and not CodexOAuthCredentialState.NeedsRelogin)
            .Where(item => ShouldRefresh(item, now) ||
                           item.State == CodexOAuthCredentialState.RefreshBackoff &&
                           item.RefreshBackoffUntil is { } backoffUntil &&
                           backoffUntil <= now)
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        using SemaphoreSlim concurrency = new(2, 2);
        var tasks = candidates.Select(async credential =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var material = await EnsureAccessTokenAsync(credential.Id, cancellationToken);
                var expires = material.ExpiresAt is null
                    ? "unknown"
                    : material.ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                log?.Invoke($"Codex OAuth refresh succeeded: {material.AccountLabel}, expires={expires}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppDiagnosticLog.Write("CodexOAuthService.BackgroundRefreshScan", ex);
                log?.Invoke($"Codex OAuth refresh failed: {credential.DisplayName}, {ProbeTraceRedactor.RedactText(ex.Message)}");
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task<CodexOAuthCredential> RefreshWithRetryAsync(CodexOAuthCredential credential, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }

            try
            {
                var token = await RefreshTokenAsync(credential.RefreshToken, cancellationToken);
                var updated = BuildCredential(token, credential);
                updated.State = CodexOAuthCredentialState.Ready;
                updated.LastError = string.Empty;
                updated.RefreshBackoffUntil = null;
                updated.RefreshFailureCount = 0;
                return updated;
            }
            catch (Exception ex) when (!IsNonRetryableRefreshError(ex))
            {
                lastException = ex;
                AppDiagnosticLog.Write("CodexOAuthService.RefreshRetry", ex);
            }
            catch (Exception ex)
            {
                var failed = credential.Clone();
                failed.State = CodexOAuthCredentialState.NeedsRelogin;
                failed.LastError = ProbeTraceRedactor.RedactText(ex.Message);
                failed.RefreshBackoffUntil = null;
                failed.RefreshFailureCount = Math.Max(1, credential.RefreshFailureCount + 1);
                failed.UpdatedAt = DateTimeOffset.UtcNow;
                UpsertCredential(failed);
                throw;
            }
        }

        var backoff = credential.Clone();
        backoff.RefreshFailureCount = Math.Max(1, credential.RefreshFailureCount + 1);
        backoff.State = CodexOAuthCredentialState.RefreshBackoff;
        backoff.LastError = ProbeTraceRedactor.RedactText(lastException?.Message ?? "Codex OAuth refresh failed.");
        backoff.RefreshBackoffUntil = DateTimeOffset.UtcNow.Add(ResolveRefreshBackoff(backoff.RefreshFailureCount));
        backoff.UpdatedAt = DateTimeOffset.UtcNow;
        UpsertCredential(backoff);
        throw lastException ?? new InvalidOperationException("Codex OAuth refresh failed.");
    }

    private async Task<CodexOAuthTokenResponse> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken)
    {
        Dictionary<string, string> values = new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = CodexOAuthConstants.ClientId,
            ["code"] = code.Trim(),
            ["redirect_uri"] = CodexOAuthConstants.RedirectUri,
            ["code_verifier"] = codeVerifier
        };
        return await PostTokenRequestAsync(values, cancellationToken);
    }

    private async Task<CodexOAuthTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Codex OAuth refresh token is missing.");
        }

        Dictionary<string, string> values = new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = CodexOAuthConstants.ClientId,
            ["refresh_token"] = refreshToken.Trim(),
            ["scope"] = "openid profile email"
        };
        return await PostTokenRequestAsync(values, cancellationToken);
    }

    private async Task<CodexOAuthTokenResponse> PostTokenRequestAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CodexOAuthConstants.TokenUrl)
        {
            Content = new FormUrlEncodedContent(values)
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Codex OAuth token endpoint returned {(int)response.StatusCode}: {ProbeTraceRedactor.RedactText(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new CodexOAuthTokenResponse(
            TryReadString(root, "access_token"),
            TryReadString(root, "refresh_token"),
            TryReadString(root, "id_token"),
            TryReadInt(root, "expires_in"));
    }

    private CodexOAuthCredential BuildCredential(CodexOAuthTokenResponse token, CodexOAuthCredential? existing = null)
    {
        var jwt = CodexOAuthJwtParser.Parse(token.IdToken);
        var email = FirstNonEmpty(jwt.Email, existing?.Email);
        var accountId = FirstNonEmpty(jwt.AccountId, existing?.AccountId);
        var planType = FirstNonEmpty(jwt.PlanType, existing?.PlanType);
        var now = DateTimeOffset.UtcNow;
        var id = !string.IsNullOrWhiteSpace(existing?.Id)
            ? existing.Id
            : CodexOAuthCredential.BuildStableId(email, planType, accountId);
        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"codex-{Guid.NewGuid():N}";
        }

        return new CodexOAuthCredential
        {
            Id = id,
            Provider = CodexOAuthConstants.Provider,
            Email = email,
            AccountId = accountId,
            AccountIdHash = CodexOAuthCredential.HashAccountId(accountId),
            PlanType = planType,
            AccessToken = token.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? existing?.RefreshToken ?? string.Empty : token.RefreshToken,
            IdToken = string.IsNullOrWhiteSpace(token.IdToken) ? existing?.IdToken ?? string.Empty : token.IdToken,
            AccessTokenExpiresAt = token.ExpiresIn > 0 ? now.AddSeconds(token.ExpiresIn) : existing?.AccessTokenExpiresAt,
            LastRefreshAt = now,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            State = CodexOAuthCredentialState.Ready,
            LastError = string.Empty,
            RefreshBackoffUntil = null,
            RefreshFailureCount = 0
        };
    }

    private static CodexOAuthAuthMaterial BuildAuthMaterial(CodexOAuthCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            throw new InvalidOperationException($"Codex OAuth credential {credential.DisplayName} has no access token.");
        }

        return new CodexOAuthAuthMaterial(
            credential.AccessToken,
            credential.AccountId,
            credential.DisplayName,
            credential.AccessTokenExpiresAt);
    }

    private CodexOAuthCredential GetCredentialOrThrow(string credentialId)
    {
        lock (_syncRoot)
        {
            if (_credentials.TryGetValue(credentialId, out var credential))
            {
                return credential.Clone();
            }
        }

        throw new InvalidOperationException($"Codex OAuth credential was not found: {credentialId}");
    }

    private SemaphoreSlim GetRefreshLock(string credentialId)
    {
        lock (_syncRoot)
        {
            if (!_refreshLocks.TryGetValue(credentialId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _refreshLocks[credentialId] = gate;
            }

            return gate;
        }
    }

    private void UpsertCredential(CodexOAuthCredential credential)
    {
        lock (_syncRoot)
        {
            _credentials[credential.Id] = credential.Clone();
            SaveLocked();
        }

        CredentialsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetCredentialState(
        string credentialId,
        CodexOAuthCredentialState state,
        string error,
        DateTimeOffset? backoffUntil)
    {
        lock (_syncRoot)
        {
            if (_credentials.TryGetValue(credentialId, out var credential))
            {
                credential.State = state;
                credential.LastError = error;
                credential.RefreshBackoffUntil = backoffUntil;
                credential.UpdatedAt = DateTimeOffset.UtcNow;
                SaveLocked();
            }
        }

        CredentialsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveLocked()
        => _store.Save(_credentials.Values.Select(static item => item.Clone()));

    private static bool ShouldRefresh(CodexOAuthCredential credential, DateTimeOffset now)
    {
        if (credential.AccessTokenExpiresAt is { } expiresAt)
        {
            return expiresAt <= now.Add(AccessTokenRefreshLead);
        }

        return credential.LastRefreshAt is null ||
               now - credential.LastRefreshAt.Value >= LongLivedRefreshLead;
    }

    private static bool IsRefreshBackoffElapsed(CodexOAuthCredential credential, DateTimeOffset now)
        => credential.State == CodexOAuthCredentialState.RefreshBackoff &&
           credential.RefreshBackoffUntil is { } backoffUntil &&
           backoffUntil <= now;

    private static bool IsWaitingBackoff(CodexOAuthCredential credential, DateTimeOffset now)
        => credential.State == CodexOAuthCredentialState.RefreshBackoff &&
           credential.RefreshBackoffUntil is { } backoffUntil &&
           backoffUntil > now;

    private static bool HasUsableAccessToken(CodexOAuthCredential credential, DateTimeOffset now)
        => !string.IsNullOrWhiteSpace(credential.AccessToken) &&
           (credential.AccessTokenExpiresAt is null || credential.AccessTokenExpiresAt > now);

    private static TimeSpan ResolveRefreshBackoff(int failureCount)
    {
        var index = Math.Clamp(Math.Max(1, failureCount) - 1, 0, RefreshFailureBackoffs.Length - 1);
        return RefreshFailureBackoffs[index];
    }

    private static string BuildBackoffMessage(CodexOAuthCredential credential, DateTimeOffset now)
    {
        var retryAt = credential.RefreshBackoffUntil;
        var wait = retryAt is null ? TimeSpan.Zero : retryAt.Value - now;
        var waitText = wait <= TimeSpan.Zero
            ? "soon"
            : wait.TotalMinutes >= 1
                ? $"{Math.Ceiling(wait.TotalMinutes):0} minutes"
                : $"{Math.Ceiling(wait.TotalSeconds):0} seconds";
        return $"Codex OAuth credential {credential.DisplayName} is waiting for refresh retry in {waitText}.";
    }

    private static bool IsNonRetryableRefreshError(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("refresh_token_reused", StringComparison.Ordinal) ||
               message.Contains("invalid_grant", StringComparison.Ordinal) ||
               message.Contains("invalid_request", StringComparison.Ordinal);
    }

    private static string BuildAuthUrl(string state, string codeChallenge)
    {
        Dictionary<string, string> query = new()
        {
            ["client_id"] = CodexOAuthConstants.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = CodexOAuthConstants.RedirectUri,
            ["scope"] = "openid email profile offline_access",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "login",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true"
        };
        var builder = new StringBuilder(CodexOAuthConstants.AuthUrl);
        builder.Append('?');
        builder.Append(string.Join("&", query.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
        return builder.ToString();
    }

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("CodexOAuthService.OpenBrowser", ex);
            return false;
        }
    }

    private static string TryReadString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int TryReadInt(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var parsed)
            ? Math.Max(0, parsed)
            : 0;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
