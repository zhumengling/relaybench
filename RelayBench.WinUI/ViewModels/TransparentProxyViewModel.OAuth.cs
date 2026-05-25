using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Services.Infrastructure;
using RelayBench.Services;
using RelayBench.Core.Services;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class TransparentProxyViewModel
{    // Phase 22: Codex OAuth Login Commands

    /// <summary>
    /// Initializes OAuth state from persisted credentials on load.
    /// </summary>
    public void InitializeOAuthState()
    {
        var credentials = _codexOAuthService.GetCredentials();
        ApplyOAuthState(credentials, startBackgroundRefresh: true);
        RefreshProviderAccounts();
    }

    private void ApplyOAuthState(IReadOnlyList<CodexOAuthCredential> credentials, bool startBackgroundRefresh)
    {
        var active = credentials.FirstOrDefault(c => c.State == CodexOAuthCredentialState.Ready);
        if (active is not null)
        {
            IsOAuthLoggedIn = true;
            IsOAuthNotLoggedIn = false;
            OAuthUserEmail = active.DisplayName;
            OAuthStatusText = "已登录";
            if (startBackgroundRefresh)
            {
                _codexOAuthService.StartBackgroundRefreshLoop(msg => AppDiagnosticLog.Write("CodexOAuth.BgRefresh", msg));
            }
        }
        else
        {
            IsOAuthLoggedIn = false;
            IsOAuthNotLoggedIn = true;
            OAuthUserEmail = string.Empty;
            OAuthStatusText = credentials.Count == 0 ? "未登录" : "没有可用 OpenAI 账号凭据";
        }
    }

    [RelayCommand]
    private async Task LoginWithCodexOAuthAsync()
    {
        if (IsOAuthLoggingIn) return;
        IsOAuthLoggingIn = true;
        OAuthStatusText = "正在开始登录...";
        try
        {
            _activeLoginSession?.Dispose();
            _activeLoginSession = await _codexOAuthService.BeginLoginAsync(CancellationToken.None);

            if (_activeLoginSession.BrowserOpened)
            {
                OAuthStatusText = "浏览器已打开，等待回调...";
            }
            else
            {
                OAuthStatusText = "无法打开浏览器，请使用下方手动回调。";
            }

            // Wait for the callback (either from local server or manual submission)
            var credential = await _codexOAuthService.CompleteLoginAsync(
                _activeLoginSession,
                CancellationToken.None);

            IsOAuthLoggedIn = true;
            IsOAuthNotLoggedIn = false;
            OAuthUserEmail = credential.DisplayName;
            OAuthStatusText = "登录成功";
            RefreshProviderAccounts();

            // Start background refresh loop
            _codexOAuthService.StartBackgroundRefreshLoop(msg => AppDiagnosticLog.Write("CodexOAuth.BgRefresh", msg));
        }
        catch (OperationCanceledException)
        {
            OAuthStatusText = "登录已取消";
        }
        catch (Exception ex)
        {
            OAuthStatusText = $"登录失败: {ex.Message}";
            AppDiagnosticLog.Write("CodexOAuth.Login", ex);
        }
        finally
        {
            IsOAuthLoggingIn = false;
            _activeLoginSession?.Dispose();
            _activeLoginSession = null;
        }
    }

    [RelayCommand]
    private async Task StartCodexOAuthLoginAsync()
        => await LoginWithCodexOAuthAsync();

    [RelayCommand]
    private void CancelCodexOAuthLogin()
    {
        _activeLoginSession?.Cancel();
        OAuthStatusText = "正在取消 OpenAI 账号登录...";
    }

    [RelayCommand]
    private void SubmitCodexOAuthCallback()
        => SubmitManualCallback();

    [RelayCommand]
    private void CopyCodexOAuthLoginUrl()
    {
        var authUrl = _activeLoginSession?.AuthUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(authUrl))
        {
            OAuthStatusText = "没有可复制的登录链接";
            return;
        }

        SetClipboardText(authUrl);
        OAuthStatusText = "OpenAI 账号登录链接已复制";
    }

    [RelayCommand]
    private void LogoutCodexOAuth()
    {
        var credentials = _codexOAuthService.GetCredentials();
        foreach (var credential in credentials)
        {
            _codexOAuthService.DeleteCredential(credential.Id);
        }

        IsOAuthLoggedIn = false;
        IsOAuthNotLoggedIn = true;
        OAuthUserEmail = string.Empty;
        OAuthStatusText = "已退出登录";
        RefreshProviderAccounts();
    }

    [RelayCommand]
    private void SubmitManualCallback()
    {
        if (string.IsNullOrWhiteSpace(ManualCallbackUrl)) return;

        var accepted = _codexOAuthService.SubmitManualCallback(_activeLoginSession, ManualCallbackUrl);
        if (accepted)
        {
            OAuthStatusText = "回调已接收，正在交换令牌...";
            ManualCallbackUrl = string.Empty;
        }
        else
        {
            OAuthStatusText = "回调未通过，请检查 URL 后重试。";
        }
    }

    [RelayCommand]
    private async Task ImportCodexOAuthCredentialAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add(".cpa");
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                OAuthStatusText = "已取消导入";
                return;
            }

            OAuthStatusText = "正在导入 OpenAI 账号凭据...";
            var result = await _codexOAuthService.ImportCpaCredentialFileAsync(file.Path, CancellationToken.None);
            ApplyOAuthState(_codexOAuthService.GetCredentials(), startBackgroundRefresh: true);
            RefreshProviderAccounts();
            OAuthStatusText = result.Refreshed
                ? "OpenAI 账号凭据已导入并刷新"
                : string.IsNullOrWhiteSpace(result.RefreshError)
                    ? string.IsNullOrWhiteSpace(result.Credential.RefreshToken)
                        ? "OpenAI 账号凭据已导入（缺少 refresh_token，仅短期可用）"
                        : "OpenAI 账号凭据已导入"
                    : $"凭据已导入，刷新待重试: {result.RefreshError}";
        }
        catch (Exception ex)
        {
            OAuthStatusText = $"导入失败: {ex.Message}";
            AppDiagnosticLog.Write("CodexOAuth.Import", ex);
        }
    }

    [RelayCommand]
    private async Task RefreshOAuthTokenAsync()
    {
        var credentials = _codexOAuthService.GetCredentials();
        var active = credentials.FirstOrDefault(c => c.State == CodexOAuthCredentialState.Ready);
        if (active is null)
        {
            OAuthStatusText = "没有可刷新的有效凭据";
            return;
        }

        OAuthStatusText = "正在刷新令牌...";
        try
        {
            await _codexOAuthService.RefreshCredentialAsync(active.Id, CancellationToken.None);
            OAuthStatusText = "令牌已刷新";
            ApplyOAuthState(_codexOAuthService.GetCredentials(), startBackgroundRefresh: false);
            RefreshProviderAccounts();
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("needs re-login", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                OAuthStatusText = "令牌已过期，请重新登录。";
                IsOAuthLoggedIn = false;
                IsOAuthNotLoggedIn = true;
                OAuthUserEmail = string.Empty;
            }
            else
            {
                OAuthStatusText = $"刷新失败: {ex.Message}";
            }
            AppDiagnosticLog.Write("CodexOAuth.Refresh", ex);
            RefreshProviderAccounts();
        }
    }

    [RelayCommand]
    private async Task RefreshCodexOAuthCredentialAsync(TransparentProxyProviderAccount? account)
    {
        var credential = ResolveCodexOAuthCredential(account);
        if (credential is null)
        {
            OAuthStatusText = "没有可刷新的 OpenAI 账号凭据";
            return;
        }

        OAuthStatusText = $"正在刷新 {credential.DisplayName}...";
        try
        {
            await _codexOAuthService.RefreshCredentialAsync(credential.Id, CancellationToken.None);
            OAuthStatusText = $"{credential.DisplayName} 已刷新";
            ApplyOAuthState(_codexOAuthService.GetCredentials(), startBackgroundRefresh: false);
            RefreshProviderAccounts();
            await ApplyRoutesToProxyAsync();
        }
        catch (Exception ex)
        {
            OAuthStatusText = $"刷新失败: {ProbeTraceRedactor.RedactText(ex.Message)}";
            AppDiagnosticLog.Write("CodexOAuth.RefreshCredential", ex);
            RefreshProviderAccounts();
        }
    }

    [RelayCommand]
    private async Task DisableCodexOAuthCredentialAsync(TransparentProxyProviderAccount? account)
    {
        var credential = ResolveCodexOAuthCredential(account);
        if (credential is null)
        {
            OAuthStatusText = "没有可切换的 OpenAI 账号凭据";
            return;
        }

        var shouldDisable = credential.State != CodexOAuthCredentialState.Disabled;
        _codexOAuthService.DisableCredential(credential.Id, shouldDisable);
        OAuthStatusText = shouldDisable
            ? $"{credential.DisplayName} 已停用"
            : $"{credential.DisplayName} 已启用";
        ApplyOAuthState(_codexOAuthService.GetCredentials(), startBackgroundRefresh: false);
        RefreshProviderAccounts();
        await ApplyRoutesToProxyAsync();
    }

    [RelayCommand]
    private async Task ExportCodexOAuthCredentialAsync(TransparentProxyProviderAccount? account)
    {
        var credential = ResolveCodexOAuthCredential(account);
        if (credential is null)
        {
            OAuthStatusText = "没有可导出的 OpenAI 账号凭据";
            return;
        }

        if (string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            OAuthStatusText = $"{credential.DisplayName} 缺少 refresh_token，无法导出 CPA 兼容文件";
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.FileTypeChoices.Add("CPA Auth JSON", [".json"]);
            picker.SuggestedFileName = BuildCpaCodexOAuthFileName(credential);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                OAuthStatusText = "已取消导出 OpenAI 账号凭据";
                return;
            }

            await File.WriteAllTextAsync(file.Path, BuildCpaCodexOAuthJson(credential));
            OAuthStatusText = $"已导出 OpenAI 账号凭据：{file.Path}";
        }
        catch (Exception ex)
        {
            OAuthStatusText = $"导出失败: {ProbeTraceRedactor.RedactText(ex.Message)}";
            AppDiagnosticLog.Write("CodexOAuth.ExportCredential", ex);
        }
    }

    [RelayCommand]
    private async Task DeleteCodexOAuthCredentialAsync(TransparentProxyProviderAccount? account)
    {
        var credential = ResolveCodexOAuthCredential(account);
        if (credential is null)
        {
            OAuthStatusText = "没有可删除的 OpenAI 账号凭据";
            return;
        }

        _codexOAuthService.DeleteCredential(credential.Id);
        var updatedAt = DateTime.UtcNow;
        for (var i = 0; i < Routes.Count; i++)
        {
            var route = Routes[i];
            if (!string.Equals(route.OAuthCredentialId, credential.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var updated = route with
            {
                AuthMode = TransparentProxyRouteAuthModes.ApiKey,
                OAuthProvider = null,
                OAuthCredentialId = null,
                CodexBackendBaseUrl = null,
                CodexOAuthFastMode = false,
                UpdatedAtUtc = updatedAt
            };
            Routes[i] = updated;
            if (_routeRepository is not null)
            {
                await _routeRepository.UpsertAsync(updated);
            }
        }

        OAuthStatusText = $"{credential.DisplayName} 已删除";
        ApplyOAuthState(_codexOAuthService.GetCredentials(), startBackgroundRefresh: false);
        RefreshProviderAccounts();
        await ApplyRoutesToProxyAsync();
    }

    private CodexOAuthCredential? ResolveCodexOAuthCredential(TransparentProxyProviderAccount? account)
    {
        var credentials = _codexOAuthService.GetCredentials();
        if (!string.IsNullOrWhiteSpace(account?.OAuthCredentialId))
        {
            var matched = credentials.FirstOrDefault(credential =>
                string.Equals(credential.Id, account.OAuthCredentialId, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return matched;
            }
        }

        return credentials.FirstOrDefault(static credential => credential.State == CodexOAuthCredentialState.Ready)
               ?? credentials.FirstOrDefault();
    }

    private static void SetClipboardText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static string BuildCpaCodexOAuthJson(CodexOAuthCredential credential)
    {
        var now = DateTimeOffset.UtcNow;
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["id_token"] = credential.IdToken,
            ["access_token"] = credential.AccessToken,
            ["refresh_token"] = credential.RefreshToken,
            ["account_id"] = credential.AccountId,
            ["last_refresh"] = FormatCpaTimestamp(credential.LastRefreshAt ?? credential.UpdatedAt),
            ["email"] = credential.Email,
            ["type"] = "codex",
            ["expired"] = FormatCpaTimestamp(credential.AccessTokenExpiresAt ?? now),
            ["disabled"] = credential.State == CodexOAuthCredentialState.Disabled
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string BuildCpaCodexOAuthFileName(CodexOAuthCredential credential)
    {
        var email = SanitizeCpaAuthFileNamePart(credential.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            email = SanitizeCpaAuthFileNamePart(credential.Id);
        }

        var plan = SanitizeCpaAuthFileNamePart(credential.PlanType).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(plan)
            ? $"codex-{email}.json"
            : $"codex-{email}-{plan}.json";
    }

    private static string FormatCpaTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string SanitizeCpaAuthFileNamePart(string? value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (value ?? string.Empty)
            .Trim()
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray();
        return new string(chars).Trim('-', ' ');
    }
}

public enum TokenMeterVisualTone
{
    Wait,
    Idle,
    Live
}
