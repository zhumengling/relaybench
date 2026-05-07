using RelayBench.App.Infrastructure;
using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

public sealed class CodexOAuthCredentialViewModel : ObservableObject
{
    private readonly CodexOAuthCredential _credential;

    internal CodexOAuthCredentialViewModel(CodexOAuthCredential credential)
    {
        _credential = credential.Clone();
    }

    public string Id => _credential.Id;

    public string Email => _credential.Email;

    public string DisplayName => _credential.DisplayName;

    public string PlanType => string.IsNullOrWhiteSpace(_credential.PlanType) ? "Codex" : _credential.PlanType;

    public string AccountIdHash => _credential.AccountIdHash;

    public string State => _credential.State.ToString();

    public string ComboDisplayText
        => $"{DisplayName} · {StateText}";

    public string DetailText
    {
        get
        {
            List<string> parts = [];
            if (!string.IsNullOrWhiteSpace(PlanType))
            {
                parts.Add(PlanType);
            }

            if (!string.IsNullOrWhiteSpace(AccountIdHash))
            {
                parts.Add(AccountIdHash);
            }

            parts.Add(ExpireText);
            return string.Join(" · ", parts);
        }
    }

    public string StateText
        => _credential.State switch
        {
            CodexOAuthCredentialState.Ready => "可用",
            CodexOAuthCredentialState.Refreshing => "刷新中",
            CodexOAuthCredentialState.RefreshBackoff => BuildBackoffStateText(),
            CodexOAuthCredentialState.NeedsRelogin => "需重新登录",
            CodexOAuthCredentialState.Disabled => "已停用",
            _ => "未知"
        };

    public string StateBrush
        => _credential.State switch
        {
            CodexOAuthCredentialState.Ready => "#15803D",
            CodexOAuthCredentialState.Refreshing => "#2563EB",
            CodexOAuthCredentialState.RefreshBackoff => "#B45309",
            CodexOAuthCredentialState.NeedsRelogin => "#B91C1C",
            CodexOAuthCredentialState.Disabled => "#64748B",
            _ => "#64748B"
        };

    public string ExpireText
        => _credential.AccessTokenExpiresAt is { } expiresAt
            ? $"到期 {expiresAt.ToLocalTime():MM-dd HH:mm}"
            : "到期未知";

    public string LastRefreshText
        => _credential.LastRefreshAt is { } lastRefresh
            ? $"刷新 {lastRefresh.ToLocalTime():MM-dd HH:mm}"
            : "尚未刷新";

    public string LastError
        => _credential.LastError;

    public bool CanUse
        => _credential.State is CodexOAuthCredentialState.Ready or CodexOAuthCredentialState.RefreshBackoff;

    private string BuildBackoffStateText()
    {
        if (_credential.RefreshBackoffUntil is not { } retryAt)
        {
            return "等待重试";
        }

        var remaining = retryAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "可重试";
        }

        return remaining.TotalMinutes >= 1
            ? $"等待 {Math.Ceiling(remaining.TotalMinutes):0} 分钟"
            : $"等待 {Math.Ceiling(remaining.TotalSeconds):0} 秒";
    }
}
