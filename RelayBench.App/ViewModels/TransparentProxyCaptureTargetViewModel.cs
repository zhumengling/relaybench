using RelayBench.App.Services;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels;

public sealed class TransparentProxyCaptureTargetViewModel : ObservableObject
{
    public TransparentProxyCaptureTargetViewModel(TransparentProxyDetectedApp app)
    {
        Id = app.Id;
        DisplayName = app.DisplayName;
        RecommendedMode = app.RecommendedMode;
        Status = app.Status;
        ExecutablePath = app.ExecutablePath ?? "-";
        ConfigPath = app.ConfigPath ?? "-";
        LastRequestText = "无请求";
        TokenText = "0 tokens";
        TrafficSummary = "等待流量";
        RestorePointText = "无恢复点";
        DetailToolTip = BuildDetailToolTip();
        StatusBrush = ResolveStatusBrush(Status);
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string RecommendedMode { get; }

    private string _lastRequestText = "无请求";

    private string _tokenText = "0 tokens";

    private string _trafficSummary = "等待流量";

    private string _restorePointText = "无恢复点";

    private string _detailToolTip = string.Empty;

    private string _statusBrush = "#64748B";

    public string Status { get; }

    public string ExecutablePath { get; }

    public string ConfigPath { get; }

    public string LastRequestText
    {
        get => _lastRequestText;
        private set => SetProperty(ref _lastRequestText, value);
    }

    public string TokenText
    {
        get => _tokenText;
        private set => SetProperty(ref _tokenText, value);
    }

    public string TrafficSummary
    {
        get => _trafficSummary;
        private set => SetProperty(ref _trafficSummary, value);
    }

    public string RestorePointText
    {
        get => _restorePointText;
        private set => SetProperty(ref _restorePointText, value);
    }

    public string DetailToolTip
    {
        get => _detailToolTip;
        private set => SetProperty(ref _detailToolTip, value);
    }

    public string StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public void ApplyMetrics(IReadOnlyList<TransparentProxyIngressMetricsSnapshot>? ingresses)
    {
        var match = FindMatchingIngress(ingresses);
        if (match is null || match.Requests <= 0)
        {
            LastRequestText = "无请求";
            TokenText = "0 tokens";
            TrafficSummary = "等待流量";
            DetailToolTip = BuildDetailToolTip();
            return;
        }

        LastRequestText = FormatLastSeen(match.LastRequestAt ?? match.LastTokenActivityAt);
        TokenText = $"{FormatCompactCount(match.OutputTokens)} tokens";
        TrafficSummary = $"{match.Requests} 请求 · {FormatCompactCount(match.OutputTokens)} tokens";
        DetailToolTip = BuildDetailToolTip();
    }

    internal void ApplyArtifact(TransparentProxyCaptureArtifactSnapshot? artifact)
    {
        RestorePointText = artifact is null || artifact.BackupCount <= 0
            ? "无恢复点"
            : $"恢复点 {artifact.BackupCount} · {FormatLastSeen(artifact.LatestBackupAt)}";
        DetailToolTip = BuildDetailToolTip();
    }

    private TransparentProxyIngressMetricsSnapshot? FindMatchingIngress(IReadOnlyList<TransparentProxyIngressMetricsSnapshot>? ingresses)
    {
        if (ingresses is null || ingresses.Count == 0)
        {
            return null;
        }

        var needles = BuildNeedles()
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ingresses
            .OrderByDescending(static item => item.LastRequestAt ?? item.LastTokenActivityAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault(ingress => needles.Any(needle =>
                ingress.SourceApplication.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                ingress.CaptureMode.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    }

    private IEnumerable<string> BuildNeedles()
    {
        yield return DisplayName;
        yield return RecommendedMode;
        if (Id.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Claude";
            yield return "Anthropic";
        }

        if (Id.Contains("vs", StringComparison.OrdinalIgnoreCase))
        {
            yield return "VS";
            yield return "Code";
        }

        if (Id.Contains("codex-cli", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Codex CLI";
            yield return "Codex config";
        }

        if (Id.Contains("codex-desktop", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Codex 桌面端";
            yield return "Codex 客户端";
        }
    }

    private string BuildDetailToolTip()
        => string.Join(
            Environment.NewLine,
            [
                $"{DisplayName} · {Status}",
                $"接管方式：{RecommendedMode}",
                $"程序路径：{ExecutablePath}",
                $"配置路径：{ConfigPath}",
                $"恢复点：{RestorePointText}",
                $"最近流量：{TrafficSummary}，{LastRequestText}"
            ]);

    private static string ResolveStatusBrush(string status)
    {
        if (status.Contains("运行中", StringComparison.OrdinalIgnoreCase))
        {
            return "#059669";
        }

        if (status.Contains("未检测到", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("未确认", StringComparison.OrdinalIgnoreCase))
        {
            return "#64748B";
        }

        if (status.Contains("待创建", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("未入 PATH", StringComparison.OrdinalIgnoreCase))
        {
            return "#D97706";
        }

        return "#0F62FE";
    }

    private static string FormatLastSeen(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "无请求";
        }

        var elapsed = DateTimeOffset.Now - value.Value.ToLocalTime();
        if (elapsed.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
        }

        if (elapsed.TotalHours < 24)
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前";
        }

        return value.Value.ToLocalTime().ToString("MM-dd HH:mm");
    }

    private static string FormatCompactCount(long value)
    {
        var absolute = Math.Abs(value);
        if (absolute >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (absolute >= 1_000)
        {
            return $"{value / 1_000d:0.#}k";
        }

        return value.ToString();
    }
}
