using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class ApplicationCenterViewModel : ObservableObject
{
    [RelayCommand]
    private async Task ProbeProtocolAsync()
        => await ProbeProtocolCoreAsync(forceProbe: true, useCache: false);

    [RelayCommand]
    private Task PreviewTargetAsync(AppTargetItem? target)
    {
        if (target is null)
        {
            return Task.CompletedTask;
        }

        SelectedTargetName = target.Name;
        RefreshTemplateRows(target);
        StatusMessage = target.TargetId switch
        {
            "codex" => BuildCodexPreview(),
            "claude-cli" => BuildClaudePreview(),
            "antigravity" => BuildAntigravityPreview(),
            "vscode-codex" => _vsCodeSettingsService.Preview(BaseUrl.Trim()).PreviewText,
            _ => $"{target.Name}: 暂无可写入规则预览。"
        };
        SetTracePreview(target);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ApplyTargetAsync(AppTargetItem? target)
    {
        if (target is null)
        {
            return;
        }

        if (!target.IsSelectable)
        {
            StatusMessage = target.DisabledReason ?? $"{target.Name}: 不可写入";
            return;
        }

        var receipt = await ApplyTargetCoreAsync(target, refreshDiagnostics: true);
        await RequestCodexHistoryMergeReviewIfNeededAsync(receipt);
    }

    [RelayCommand]
    private async Task RestoreTargetAsync(AppTargetItem? target)
    {
        if (target is null)
        {
            return;
        }

        var receipt = await RestoreTargetCoreAsync(target, refreshDiagnostics: true);
        await RequestCodexHistoryMergeReviewIfNeededAsync(receipt);
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        var selected = Targets.Where(static target => target.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "请至少选择一个目标";
            return;
        }

        IsApplying = true;
        try
        {
            List<ApplicationAccessOperationReceipt> receipts = [];
            foreach (var target in selected)
            {
                receipts.Add(await RestoreTargetCoreAsync(target, refreshDiagnostics: false));
            }

            UpdateLastFileOperationCounts(receipts);
            await RunClientApiDiagnosticsAsync();
            await RecordApplicationAccessBatchHistoryAsync("restore-selected", receipts);
            await RequestCodexHistoryMergeReviewIfNeededAsync(receipts);
            StatusText = $"已还原 {selected.Count} 个目标";
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private async Task ApplyToSelectedAsync()
    {
        var selected = Targets.Where(static target => target.IsSelected && target.IsSelectable).ToList();
        if (selected.Count == 0)
        {
            StatusText = "请至少选择一个可写目标";
            return;
        }

        var protocolBackedTargets = selected
            .Where(static target => !IsVsCodeTarget(target))
            .ToList();
        var requireApiKey = protocolBackedTargets.Any(RequiresApiKeyForTarget);
        if (protocolBackedTargets.Count > 0 &&
            !TryBuildSettings(out _, requireModel: true, requireApiKey: requireApiKey))
        {
            StatusText = requireApiKey ? "请补全入口 URL、API 密钥和模型" : "请补全入口 URL 和模型";
            return;
        }

        if (protocolBackedTargets.Count > 0)
        {
            var probeResult = await EnsureProtocolProbeAsync();
            if (probeResult is null)
            {
                return;
            }
        }

        selected = selected.Where(static target => target.IsSelectable).ToList();
        if (selected.Count == 0)
        {
            StatusText = "协议探测后没有可写目标";
            return;
        }

        StatusText = $"正在写入 {selected.Count} 个目标...";
        List<ApplicationAccessOperationReceipt> receipts = [];
        foreach (var target in selected)
        {
            receipts.Add(await ApplyTargetCoreAsync(target, refreshDiagnostics: false));
        }

        UpdateLastFileOperationCounts(receipts);
        await RunClientApiDiagnosticsAsync();
        await RecordApplicationAccessBatchHistoryAsync("apply-selected", receipts);
        await RequestCodexHistoryMergeReviewIfNeededAsync(receipts);
        StatusText = $"已写入 {selected.Count} 个所选目标";
    }

    [RelayCommand]
    private async Task ConfirmClientApplyTargetDialogAsync()
        => await ApplyToSelectedAsync();

    [RelayCommand]
    private void CancelClientApplyTargetDialog()
    {
        StatusText = "客户端应用目标对话框已取消。";
    }

    [RelayCommand]
    private async Task ApplyCurrentInterfaceToCodexAppsAsync()
    {
        var target = Targets.FirstOrDefault(static item =>
            string.Equals(item.TargetId, "codex", StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            StatusText = "未找到 Codex 接入目标";
            return;
        }

        target.IsSelected = true;
        NotifyTargetSelectionChanged();
        await ApplyTargetAsync(target);
    }

    [RelayCommand]
    private void SelectAllClientApplyTargets()
    {
        foreach (var target in Targets.Where(static item => item.IsSelectable))
        {
            target.IsSelected = true;
        }

        NotifyTargetSelectionChanged();
    }

    [RelayCommand]
    private void InvertClientApplyTargets()
    {
        foreach (var target in Targets.Where(static item => item.IsSelectable))
        {
            target.IsSelected = !target.IsSelected;
        }

        NotifyTargetSelectionChanged();
    }

    [RelayCommand]
    private void ToggleClientApplyTargetSelection(AppTargetItem? target)
    {
        if (target is null || !target.IsSelectable)
        {
            return;
        }

        target.IsSelected = !target.IsSelected;
        NotifyTargetSelectionChanged();
    }

    private async Task<ApplicationAccessOperationReceipt> ApplyTargetCoreAsync(AppTargetItem target, bool refreshDiagnostics)
    {
        if (IsVsCodeTarget(target))
        {
            if (!IsLocalEndpoint(BaseUrl))
            {
                StatusText = "VS Code 接管需要使用本地透明代理入口";
                return ApplicationAccessOperationReceipt.Failed(target, StatusText, StatusText);
            }
        }
        else if (!TryBuildSettings(out _, requireModel: true, requireApiKey: RequiresApiKeyForTarget(target)))
        {
            StatusText = RequiresApiKeyForTarget(target) ? "请补全入口 URL、API 密钥和模型" : "请补全入口 URL 和模型";
            return ApplicationAccessOperationReceipt.Failed(target, StatusText, StatusText);
        }

        IsApplying = true;
        SelectedTargetName = target.Name;
        StatusMessage = "";
        var started = Stopwatch.StartNew();
        try
        {
            if (IsVsCodeTarget(target))
            {
                var endpoint = BuildVsCodeClientApplyEndpoint();
                var selection = new ClientApplyTargetSelection(
                    target.TargetId,
                    target.ProtocolKind,
                    target.CodexConfigTemplate);
                var result = await _clientAppConfigApplyService.ApplyAsync(endpoint, [selection]);
                SetTraceFromApplyResult(target, result);
                StatusMessage = result.Summary;
                LastWriteTime = DateTime.Now.ToString("HH:mm:ss");
                RefreshTargets();
                await RecordApplicationAccessHistoryAsync(
                    "apply",
                    target,
                    result.Succeeded,
                    result.Summary,
                    result.ChangedFiles,
                    result.BackupFiles,
                    _lastProbeResult,
                    (int)started.ElapsedMilliseconds,
                    result.Error);
                if (refreshDiagnostics)
                {
                    await RunClientApiDiagnosticsAsync();
                }
                UpdateLastFileOperationCounts(result.ChangedFiles.Count, result.BackupFiles.Count);
                return ApplicationAccessOperationReceipt.FromFiles(target, result.Succeeded, result.Summary, result.ChangedFiles, result.BackupFiles, result.Error);
            }

            if (target.TargetId is "codex" or "claude-cli" or "antigravity")
            {
                if (!TryBuildSettings(out var settings, requireModel: true, requireApiKey: RequiresApiKeyForTarget(target)))
                {
                    StatusText = RequiresApiKeyForTarget(target) ? "请补全入口 URL、API 密钥和模型" : "请补全入口 URL 和模型";
                    return ApplicationAccessOperationReceipt.Failed(target, StatusText, StatusText);
                }

                var probeResult = await EnsureProtocolProbeAsync();
                if (probeResult is null)
                {
                    return ApplicationAccessOperationReceipt.Failed(target, StatusText, StatusText);
                }

                var endpoint = BuildClientApplyEndpoint(probeResult, target.ProtocolKind);
                var selection = new ClientApplyTargetSelection(
                    target.TargetId,
                    target.ProtocolKind,
                    target.CodexConfigTemplate);
                var anthropicEndpointOverride = await ResolveClaudeRelayEndpointOverrideAsync(
                    target,
                    settings,
                    probeResult);
                var result = await _clientAppConfigApplyService.ApplyAsync(
                    endpoint,
                    [selection],
                    anthropicEndpointOverride);
                SetTraceFromApplyResult(target, result);
                StatusMessage = result.Summary;
                LastWriteTime = DateTime.Now.ToString("HH:mm:ss");
                RefreshTargets();
                await SharedEndpointStore.SaveAsync(BaseUrl, ApiKey, Model);
                await RecordApplicationAccessHistoryAsync(
                    "apply",
                    target,
                    result.Succeeded,
                    result.Summary,
                    result.ChangedFiles,
                    result.BackupFiles,
                    probeResult,
                    (int)started.ElapsedMilliseconds,
                    result.Error);
                if (refreshDiagnostics)
                {
                    await RunClientApiDiagnosticsAsync();
                }
                UpdateLastFileOperationCounts(result.ChangedFiles.Count, result.BackupFiles.Count);
                return ApplicationAccessOperationReceipt.FromFiles(target, result.Succeeded, result.Summary, result.ChangedFiles, result.BackupFiles, result.Error);
            }

            StatusMessage = $"{target.Name}: 暂无可写配置适配器。";
            SetTraceState(target.Name, string.Empty, false, StatusMessage);
            await RecordApplicationAccessHistoryAsync(
                "apply",
                target,
                false,
                StatusMessage,
                [],
                [],
                _lastProbeResult,
                (int)started.ElapsedMilliseconds,
                StatusMessage);
            return ApplicationAccessOperationReceipt.Failed(target, StatusMessage, StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{target.Name} 写入错误：{ex.Message}";
            SetTraceState(target.Name, string.Empty, false, StatusMessage);
            await RecordApplicationAccessHistoryAsync(
                "apply",
                target,
                false,
                StatusMessage,
                [],
                [],
                _lastProbeResult,
                (int)started.ElapsedMilliseconds,
                ex.Message);
            return ApplicationAccessOperationReceipt.Failed(target, StatusMessage, ex.Message);
        }
        finally
        {
            IsApplying = false;
        }
    }

    private async Task<ClientApplyEndpoint?> ResolveClaudeRelayEndpointOverrideAsync(
        AppTargetItem target,
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult probeResult)
    {
        if (!RequiresClaudeRelayEndpoint(target, probeResult))
        {
            return null;
        }

        if (_claudeRelayEndpointResolver is null)
        {
            throw new InvalidOperationException(
                "此 OpenAI Chat 入口需要 RelayBench 本地透明代理转换后才能写入 Claude CLI，但透明代理上下文尚未连接。");
        }

        var endpoint = await _claudeRelayEndpointResolver(settings, probeResult, target.Name);
        if (endpoint is null)
        {
            throw new InvalidOperationException(
                "此 OpenAI Chat 入口需要 RelayBench 本地透明代理转换后才能写入 Claude CLI，但尚未准备本地中继入口。");
        }

        return endpoint;
    }

    internal static bool RequiresClaudeRelayEndpoint(
        AppTargetItem target,
        ProxyEndpointProtocolProbeResult probeResult)
        => string.Equals(target.TargetId, "claude-cli", StringComparison.OrdinalIgnoreCase) &&
           target.ProtocolKind == ClientApplyProtocolKind.Anthropic &&
           !probeResult.AnthropicMessagesSupported &&
           probeResult.ChatCompletionsSupported;

    private bool RequiresApiKeyForTarget(AppTargetItem target)
        => !string.Equals(target.TargetId, "codex", StringComparison.OrdinalIgnoreCase) ||
           !CodexFamilyConfigApplyService.ShouldUseOpenAiBaseUrlMode(BaseUrl);

    private async Task RequestCodexHistoryMergeReviewIfNeededAsync(
        IReadOnlyList<ApplicationAccessOperationReceipt> receipts)
    {
        var receipt = receipts.FirstOrDefault(ShouldOfferCodexHistoryMerge);
        if (receipt is not null)
        {
            await RequestCodexHistoryMergeReviewIfNeededAsync(receipt);
        }
    }

    private async Task RequestCodexHistoryMergeReviewIfNeededAsync(
        ApplicationAccessOperationReceipt receipt)
    {
        var handler = CodexHistoryMergeReviewRequested;
        if (handler is null || !ShouldOfferCodexHistoryMerge(receipt))
        {
            return;
        }

        await RefreshCodexHistoryStatusAsync();
        StatusMessage = "Codex 配置已变更。修改本地 Codex 对话记录前请先复核历史合并。";
        handler.Invoke(this, EventArgs.Empty);
    }

    private static bool ShouldOfferCodexHistoryMerge(ApplicationAccessOperationReceipt receipt)
        => receipt.Succeeded &&
           string.Equals(receipt.TargetId, "codex", StringComparison.OrdinalIgnoreCase);

    internal static string BuildCodexHistoryStatusSummary(CodexHistorySyncStatus status)
    {
        var rolloutCount = status.RolloutCounts.Sessions.Values.Sum() +
                           status.RolloutCounts.ArchivedSessions.Values.Sum();
        var sqliteText = status.SqliteCounts is null
            ? "未找到 SQLite"
            : status.SqliteCounts.Unreadable
                ? "SQLite 不可读"
                : $"SQLite {status.SqliteCounts.Sessions.Values.Sum() + status.SqliteCounts.ArchivedSessions.Values.Sum()} 行";
        return $"Codex 提供方 {status.CurrentProvider} | 记录 {rolloutCount} | {sqliteText} | 备份 {status.BackupSummary.Count}";
    }

    internal static string BuildCodexHistoryStatusDetail(CodexHistorySyncStatus status)
    {
        List<string> lines =
        [
            $"Codex 主目录：{status.CodexHome}",
            $"当前提供方：{status.CurrentProvider}{(status.CurrentProviderImplicit ? "（隐式）" : string.Empty)}",
            $"已配置提供方：{FormatProviderList(status.ConfiguredProviders)}",
            $"Rollout 会话：{FormatProviderCounts(status.RolloutCounts.Sessions)}",
            $"归档会话：{FormatProviderCounts(status.RolloutCounts.ArchivedSessions)}",
            $"锁定的 rollout 文件：{status.LockedRolloutFiles.Count}",
            $"加密内容警告：{status.EncryptedContentWarning ?? "无"}",
            $"SQLite 会话：{(status.SqliteCounts is null ? "缺失" : FormatProviderCounts(status.SqliteCounts.Sessions))}",
            $"SQLite 归档：{(status.SqliteCounts is null ? "缺失" : FormatProviderCounts(status.SqliteCounts.ArchivedSessions))}",
            $"SQLite 错误：{status.SqliteCounts?.Error ?? "无"}",
            $"SQLite 修复：user_event={status.SqliteRepairStats?.UserEventRowsNeedingRepair ?? 0}, cwd={status.SqliteRepairStats?.CwdRowsNeedingRepair ?? 0}",
            $"项目可见根：{status.ProjectThreadVisibility.Count}",
            $"备份根目录：{status.BackupSummary.BackupRoot}",
            $"备份数量：{status.BackupSummary.Count}",
            $"备份字节：{status.BackupSummary.TotalBytes}"
        ];

        return string.Join(Environment.NewLine, lines);
    }

    internal static string BuildCodexChatMergeDetail(CodexChatMergeResult result)
    {
        List<string> lines =
        [
            $"显示目标：{CodexChatMergeService.BuildTargetDisplayName(result.Target)}",
            $"是否成功：{result.Succeeded}",
            $"摘要：{result.Summary}",
            $"SQLite 提供方行数：{result.RebuckettedThreadCount}",
            $"已更新会话文件：{result.UpdatedSessionFileCount}",
            $"变更文件：{FormatPathList(result.ChangedFiles)}",
            $"备份目录：{FormatPathList(result.BackupFiles)}",
            $"警告：{result.Warning ?? "无"}",
            $"错误：{result.Error ?? "无"}"
        ];
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatProviderList(IReadOnlyList<string> providers)
        => providers.Count == 0 ? "无" : string.Join(", ", providers);

    private static string FormatProviderCounts(IReadOnlyDictionary<string, int> counts)
        => counts.Count == 0
            ? "无"
            : string.Join(", ", counts.Select(static pair => $"{pair.Key}={pair.Value}"));

    private static string FormatPathList(IReadOnlyList<string> paths)
        => paths.Count == 0 ? "none" : string.Join(Environment.NewLine, paths);

    private async Task<ApplicationAccessOperationReceipt> RestoreTargetCoreAsync(AppTargetItem target, bool refreshDiagnostics)
    {
        IsApplying = true;
        SelectedTargetName = target.Name;
        var started = Stopwatch.StartNew();
        try
        {
            if (target.TargetId == "vscode-codex")
            {
                var vsCodeResult = await Task.Run(() => _vsCodeSettingsService.RestoreLatestBackups());
                SetTraceFromVsCodeResult(target, vsCodeResult.Succeeded, vsCodeResult.Summary, vsCodeResult.ChangedFiles);
                StatusMessage = vsCodeResult.Summary;
                LastWriteTime = DateTime.Now.ToString("HH:mm:ss");
                RefreshTargets();
                await RecordApplicationAccessHistoryAsync(
                    "restore",
                    target,
                    vsCodeResult.Succeeded,
                    vsCodeResult.Summary,
                    vsCodeResult.ChangedFiles,
                    vsCodeResult.BackupFiles,
                    _lastProbeResult,
                    (int)started.ElapsedMilliseconds);
                if (refreshDiagnostics)
                {
                    await RunClientApiDiagnosticsAsync();
                }
                return ApplicationAccessOperationReceipt.FromFiles(target, vsCodeResult.Succeeded, vsCodeResult.Summary, vsCodeResult.ChangedFiles, vsCodeResult.BackupFiles, string.Empty);
            }

            var clientName = target.TargetId switch
            {
                "codex" => "Codex CLI",
                "claude-cli" => "Claude CLI",
                "antigravity" => "Antigravity",
                _ => target.Name
            };
            var result = await _restoreService.RestoreAsync(clientName);
            SetTraceFromRestoreResult(target, result);
            StatusMessage = result.Summary;
            LastWriteTime = DateTime.Now.ToString("HH:mm:ss");
            RefreshTargets();
            await RecordApplicationAccessHistoryAsync(
                "restore",
                target,
                result.Succeeded,
                result.Summary,
                result.ChangedFiles,
                result.BackupFiles,
                _lastProbeResult,
                (int)started.ElapsedMilliseconds,
                result.Error);
            if (refreshDiagnostics)
            {
                await RunClientApiDiagnosticsAsync();
            }
            UpdateLastFileOperationCounts(result.ChangedFiles.Count, result.BackupFiles.Count);
            return ApplicationAccessOperationReceipt.FromFiles(target, result.Succeeded, result.Summary, result.ChangedFiles, result.BackupFiles, result.Error);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{target.Name} restore error: {ex.Message}";
            SetTraceState(target.Name, string.Empty, false, StatusMessage);
            await RecordApplicationAccessHistoryAsync(
                "restore",
                target,
                false,
                StatusMessage,
                [],
                [],
                _lastProbeResult,
                (int)started.ElapsedMilliseconds,
                ex.Message);
            return ApplicationAccessOperationReceipt.Failed(target, StatusMessage, ex.Message);
        }
        finally
        {
            IsApplying = false;
        }
    }

}
