using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Storage;
using Windows.Storage;
using Windows.System;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class ApplicationCenterViewModel
{
    private readonly ClientApiDiagnosticsService _clientApiDiagnosticsService = new();

    [ObservableProperty] public partial bool IsClientApiDiagnosing { get; set; }
    [ObservableProperty] public partial string ClientApiCheckedAtText { get; set; } = "--";
    [ObservableProperty] public partial string ClientApiSummary { get; set; } = "运行后显示已发现应用和接入状态。";
    [ObservableProperty] public partial string ClientApiDetail { get; set; } = "尚未扫描应用接入。";

    public ObservableCollection<ClientApiStatusRow> ClientApiStatusRows { get; } = [];

    public bool HasClientApiStatusRows => ClientApiStatusRows.Count > 0;

    partial void OnIsClientApiDiagnosingChanged(bool value)
        => OnPropertyChanged(nameof(IsEndpointBusy));

    [RelayCommand]
    private async Task RunClientApiDiagnosticsAsync()
    {
        if (IsClientApiDiagnosing)
        {
            return;
        }

        IsClientApiDiagnosing = true;
        StatusText = "正在扫描客户端 API...";
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            var result = await _clientApiDiagnosticsService.RunAsync(progress);
            ApplyClientApiDiagnosticsResult(result);
            StatusText = $"客户端 API 诊断完成：{result.ReachableCount}/{result.Checks.Count} 可达";
        }
        catch (Exception ex)
        {
            StatusText = $"客户端 API 诊断失败: {ex.Message}";
        }
        finally
        {
            IsClientApiDiagnosing = false;
        }
    }

    [RelayCommand]
    private async Task OpenApplicationCenterProxyEndpointHistoryAsync()
    {
        try
        {
            var historyPath = Path.Combine(StoragePaths.Root, "endpoint-history.json");
            if (File.Exists(historyPath))
            {
                var file = await StorageFile.GetFileFromPathAsync(historyPath);
                await Launcher.LaunchFileAsync(file);
                StatusText = "已打开接口历史";
                return;
            }

            var folder = await StorageFolder.GetFolderFromPathAsync(StoragePaths.Root);
            await Launcher.LaunchFolderAsync(folder);
            StatusText = "历史文件尚未生成，已打开数据目录";
        }
        catch (Exception ex)
        {
            StatusText = $"打开接口历史失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestoreClientApiDefaultConfigAsync(ClientApiStatusRow? row)
    {
        if (row is null || !row.RestoreSupported)
        {
            return;
        }

        IsApplying = true;
        SelectedTargetName = row.Name;
        try
        {
            var result = await _restoreService.RestoreAsync(row.Name);
            StatusMessage = result.Summary;
            StatusText = result.Succeeded ? result.Summary : $"还原失败: {result.Error ?? result.Summary}";
            LastWriteTime = DateTime.Now.ToString("HH:mm:ss");
            UpdateLastFileOperationCounts(result.ChangedFiles.Count, result.BackupFiles.Count);
            await RecordClientApiRestoreHistoryAsync(row, result);
            if (result.Succeeded)
            {
                RefreshTargets();
                await RunClientApiDiagnosticsAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"{row.Name} 还原错误: {ex.Message}";
            StatusText = StatusMessage;
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void ApplyClientApiDiagnosticsResult(ClientApiDiagnosticsResult result)
    {
        ClientApiCheckedAtText = $"检测时间：{result.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        ClientApiSummary =
            $"目标 {result.Checks.Count} · 已发现 {result.InstalledCount}/{result.Checks.Count} · " +
            $"配置 {result.ConfiguredCount}/{result.Checks.Count} · API 可达 {result.ReachableCount}/{result.Checks.Count}" +
            (string.IsNullOrWhiteSpace(result.Error) ? string.Empty : $" · 错误 {TrimForInline(result.Error, 42)}");

        ClientApiDetail = BuildClientApiDetail(result);

        ClientApiStatusRows.Clear();
        foreach (var row in result.Checks
                     .OrderBy(static check => check.Provider, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static check => check.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(BuildClientApiStatusRow))
        {
            ClientApiStatusRows.Add(row);
        }

        OnPropertyChanged(nameof(HasClientApiStatusRows));
    }

    private static string BuildClientApiDetail(ClientApiDiagnosticsResult result)
    {
        StringBuilder builder = new();
        foreach (var check in result.Checks
                     .OrderBy(static item => item.Provider, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(BuildClientApiCheckDetail(check));
            builder.AppendLine();
        }

        return builder.Length == 0
            ? "尚未扫描应用接入。"
            : builder.ToString().TrimEnd();
    }

    private static ClientApiStatusRow BuildClientApiStatusRow(ClientApiCheck check)
    {
        var statusTone = ResolveClientApiTone(check.AccessPathLabel);
        var availabilityText = check.AccessPathLabel switch
        {
            "本地代理接管" => "本地代理接管",
            "直连第三方" => "直连第三方",
            "直连官方" => "直连官方",
            _ => "待复核"
        };
        var briefResultText =
            $"{availabilityText} · {(check.Reachable ? "API 可达" : "API 待复核")} · HTTP {check.StatusCode?.ToString() ?? "--"} · {FormatMilliseconds(check.Latency)}";
        var briefDetailText =
            $"{check.Kind} · {check.ConfigOriginLabel} · 入口 {CompactEndpointLabel(check.EndpointLabel)}";

        return new ClientApiStatusRow(
            check.Provider,
            check.Name,
            availabilityText,
            briefResultText,
            briefDetailText,
            BuildClientApiCheckDetail(check),
            statusTone,
            check.RestoreSupported,
            check.RestoreHint);
    }

    private static string BuildClientApiCheckDetail(ClientApiCheck check)
    {
        StringBuilder builder = new();
        builder.AppendLine($"{check.Name} [{check.Provider}]");
        builder.AppendLine($"类型：{check.Kind}");
        builder.AppendLine($"探测地址：{check.ProbeUrl}");
        builder.AppendLine($"探测方法：{check.ProbeMethod}");
        builder.AppendLine($"已安装：{(check.Installed ? "是" : "否")}");
        builder.AppendLine($"已发现配置：{(check.ConfigDetected ? "是" : "否")}");
        builder.AppendLine($"安装线索：{check.InstallEvidence}");
        builder.AppendLine($"配置来源：{check.ConfigSource}");
        builder.AppendLine($"代理来源：{check.ProxySource}");
        builder.AppendLine($"接入方式：{check.AccessPathLabel}");
        builder.AppendLine($"注入方式：{check.ConfigOriginLabel}");
        builder.AppendLine($"当前入口：{check.EndpointLabel}");
        builder.AppendLine($"备注：{check.RoutingNote}");
        builder.AppendLine($"支持还原：{(check.RestoreSupported ? "是" : "否")}");
        builder.AppendLine($"还原说明：{check.RestoreHint}");
        builder.AppendLine($"API 可达：{(check.Reachable ? "是" : "否")}");
        builder.AppendLine($"状态码：{check.StatusCode?.ToString() ?? "--"}");
        builder.AppendLine($"延迟：{FormatMilliseconds(check.Latency)}");
        builder.AppendLine($"结论：{check.Verdict}");
        builder.AppendLine($"摘要：{check.Summary}");
        builder.AppendLine($"证据：{check.Evidence ?? "无"}");
        builder.AppendLine($"错误：{check.Error ?? "无"}");
        return builder.ToString().TrimEnd();
    }

    private static string ResolveClientApiTone(string accessPathLabel)
        => accessPathLabel switch
        {
            "本地代理接管" => ApplicationAccessTones.Warning,
            "直连第三方" => ApplicationAccessTones.Accent,
            "直连官方" => ApplicationAccessTones.Healthy,
            _ => ApplicationAccessTones.Accent
        };

    private static string FormatMilliseconds(TimeSpan? latency)
        => latency is null ? "--" : $"{latency.Value.TotalMilliseconds:F0} ms";

    private static string CompactEndpointLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "--";
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }

        return TrimForInline(trimmed, 36);
    }

    private static string TrimForInline(string value, int maxLength)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..Math.Max(0, maxLength - 3)] + "...";
    }

    private async Task RecordClientApiRestoreHistoryAsync(ClientApiStatusRow row, ClientApiConfigRestoreResult result)
    {
        try
        {
            var payload = new
            {
                Schema = "application-access-v1",
                CapturedAtUtc = DateTime.UtcNow,
                Action = "restore-client-api",
                TargetId = row.Name,
                TargetName = row.Name,
                ProtocolKind = "Restore",
                Endpoint = BaseUrl.Trim(),
                BaseUrl = BaseUrl.Trim(),
                Model = Model.Trim(),
                ApiKeyMasked = MaskApiKey(ApiKey),
                AvailableModelCount = Math.Max(0, AvailableModels.Count),
                PreferredWireApi = PreferredProtocol,
                LocalProxy = IsLocalEndpoint(BaseUrl),
                Succeeded = result.Succeeded,
                Summary = result.Summary,
                Error = result.Error ?? string.Empty,
                ChangedFileCount = result.ChangedFiles.Count,
                BackupFileCount = result.BackupFiles.Count,
                ChangedFiles = result.ChangedFiles,
                BackupFiles = result.BackupFiles,
                TargetSnapshot = new
                {
                    TargetCount = Math.Max(0, Targets.Count),
                    SelectedTargetCount = Math.Max(0, SelectedRestoreTargetCount),
                    SelectableTargetCount = Math.Max(0, Targets.Count(static item => item.IsSelectable)),
                    InstalledTargetCount = Math.Max(0, Targets.Count(static item => item.Installed)),
                    LastChangedFileCount = Math.Max(0, result.ChangedFiles.Count),
                    LastBackupFileCount = Math.Max(0, result.BackupFiles.Count)
                }
            };

            await RunHistoryRecorder.RecordAsync(
                "\u5E94\u7528\u63A5\u5165",
                row.Name,
                result.Summary,
                result.Succeeded ? 100 : 40,
                null,
                JsonSerializer.Serialize(payload));
        }
        catch
        {
            // Restore history is observational and must not block local config recovery.
        }
    }
}

public sealed record ClientApiStatusRow(
    string Provider,
    string Name,
    string AvailabilityText,
    string BriefResultText,
    string BriefDetailText,
    string DetailText,
    string StatusTone,
    bool RestoreSupported,
    string RestoreHint)
{
    public Visibility StatusAccentToneVisibility => ApplicationAccessToneVisibility.Accent(StatusTone);
    public Visibility StatusHealthyToneVisibility => ApplicationAccessToneVisibility.Healthy(StatusTone);
    public Visibility StatusWarningToneVisibility => ApplicationAccessToneVisibility.Warning(StatusTone);
    public Visibility StatusDangerToneVisibility => ApplicationAccessToneVisibility.Danger(StatusTone);
}
