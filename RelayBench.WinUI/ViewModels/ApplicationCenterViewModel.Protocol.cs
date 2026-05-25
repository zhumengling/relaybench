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
    private async Task<ProxyEndpointProtocolProbeResult?> EnsureProtocolProbeAsync()
    {
        if (_lastProbeResult is not null &&
            string.Equals(_lastProbeResult.BaseUrl.TrimEnd('/'), BaseUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_lastProbeResult.ProbeModel, Model.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return _lastProbeResult;
        }

        return await ProbeProtocolCoreAsync(forceProbe: false, useCache: true);
    }

    private async Task<ProxyEndpointProtocolProbeResult?> ProbeProtocolCoreAsync(bool forceProbe, bool useCache)
    {
        if (!TryBuildSettings(out var settings, requireModel: true))
        {
            StatusText = "请填写入口 URL、API 密钥和模型";
            return null;
        }

        IsProbing = true;
        StatusText = forceProbe ? "正在强制探测协议..." : "正在复核协议...";
        try
        {
            var resolution = await _probeService.ResolveAsync(
                settings,
                new ProxyEndpointProtocolProbeOptions(
                    ForceProbe: forceProbe,
                    UseCache: useCache,
                    SaveResult: true));

            var result = resolution.Result;
            _lastProbeResult = result;
            ResponsesSupported = result.ResponsesSupported;
            AnthropicSupported = result.AnthropicMessagesSupported;
            ChatSupported = result.ChatCompletionsSupported;
            PreferredProtocol = result.PreferredWireApi ?? "--";
            ProbeResult = BuildProbeSummary(result, resolution.FromCache);
            UpdateProbeRows(result);
            RefreshTargets();
            RefreshTemplateRows();

            await GlobalEndpointProtocolProbeCoordinator.Instance.RecordEndpointAsync(BaseUrl, ApiKey, Model, AvailableModels);
            GlobalEndpointProtocolProbeCoordinator.Instance.EnqueueEndpointProbe(
                BaseUrl,
                ApiKey,
                Model,
                AvailableModels,
                force: forceProbe);
            if (forceProbe)
            {
                await RecordApplicationAccessHistoryAsync(
                    "protocol-probe",
                    null,
                    string.IsNullOrWhiteSpace(result.Error),
                    result.Summary,
                    [],
                    [],
                    result,
                    null,
                    result.Error);
            }
            StatusText = resolution.FromCache
                ? $"已使用协议缓存：{PreferredProtocol}"
                : $"协议探测完成：{PreferredProtocol}";
            return result;
        }
        catch (Exception ex)
        {
            StatusText = $"协议探测失败: {ex.Message}";
            ProbeResult = $"Error: {ex.Message}";
            return null;
        }
        finally
        {
            IsProbing = false;
        }
    }

    private async Task RecordApplicationAccessHistoryAsync(
        string action,
        AppTargetItem? target,
        bool succeeded,
        string summary,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> backupFiles,
        ProxyEndpointProtocolProbeResult? probeResult,
        int? durationMs,
        string? error = null)
    {
        try
        {
            var targetName = target?.Name ?? "\u534F\u8BAE\u590D\u6838";
            await RunHistoryRecorder.RecordAsync(
                "\u5E94\u7528\u63A5\u5165",
                targetName,
                summary,
                succeeded ? 100 : 40,
                durationMs,
                BuildApplicationAccessHistoryPayloadJson(
                    action,
                    target,
                    BaseUrl,
                    ApiKey,
                    Model,
                    AvailableModels.Count,
                    PreferredProtocol,
                    succeeded,
                    summary,
                    changedFiles,
                    backupFiles,
                    probeResult,
                    error,
                    Targets.Count,
                    SelectedRestoreTargetCount,
                    Targets.Count(static item => item.IsSelectable),
                    Targets.Count(static item => item.Installed),
                    LastChangedFileCount,
                    LastBackupFileCount));
        }
        catch
        {
            // Application history must not interrupt config writes or restore flows.
        }
    }

    private async Task RecordApplicationAccessBatchHistoryAsync(
        string action,
        IReadOnlyList<ApplicationAccessOperationReceipt> receipts)
    {
        if (receipts.Count == 0)
        {
            return;
        }

        try
        {
            var succeeded = receipts.Count(static item => item.Succeeded);
            var changedFiles = receipts.Sum(static item => item.ChangedFileCount);
            var summary = $"{succeeded}/{receipts.Count} targets succeeded, {changedFiles} files changed";
            await RunHistoryRecorder.RecordAsync(
                "\u5E94\u7528\u63A5\u5165",
                action.Equals("restore-selected", StringComparison.OrdinalIgnoreCase)
                    ? "\u6279\u91CF\u8FD8\u539F"
                    : "\u6279\u91CF\u5199\u5165",
                summary,
                succeeded == receipts.Count ? 100 : succeeded == 0 ? 40 : 70,
                null,
                BuildApplicationAccessBatchHistoryPayloadJson(
                    action,
                    BaseUrl,
                    ApiKey,
                    Model,
                    AvailableModels.Count,
                    PreferredProtocol,
                    receipts,
                    Targets.Count,
                    SelectedRestoreTargetCount,
                    Targets.Count(static item => item.IsSelectable),
                    Targets.Count(static item => item.Installed),
                    LastChangedFileCount,
                    LastBackupFileCount));
        }
        catch
        {
            // Batch history is observational only and must not block config operations.
        }
    }

    internal static string BuildApplicationAccessHistoryPayloadJson(
        string action,
        AppTargetItem? target,
        string baseUrl,
        string apiKey,
        string model,
        int availableModelCount,
        string preferredProtocol,
        bool succeeded,
        string summary,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> backupFiles,
        ProxyEndpointProtocolProbeResult? probeResult,
        string? error = null,
        int targetCount = 0,
        int selectedTargetCount = 0,
        int selectableTargetCount = 0,
        int installedTargetCount = 0,
        int lastChangedFileCount = 0,
        int lastBackupFileCount = 0)
    {
        var payload = new
        {
            Schema = "application-access-v1",
            CapturedAtUtc = DateTime.UtcNow,
            Action = action,
            TargetId = target?.TargetId ?? "protocol-probe",
            TargetName = target?.Name ?? "\u534F\u8BAE\u590D\u6838",
            ProtocolKind = target?.ProtocolKind.ToString() ?? "Probe",
            Endpoint = baseUrl.Trim(),
            BaseUrl = baseUrl.Trim(),
            Model = model.Trim(),
            ApiKeyMasked = MaskApiKey(apiKey),
            AvailableModelCount = Math.Max(0, availableModelCount),
            PreferredWireApi = string.IsNullOrWhiteSpace(probeResult?.PreferredWireApi)
                ? preferredProtocol
                : probeResult.PreferredWireApi,
            ResponsesSupported = probeResult?.ResponsesSupported ?? false,
            ChatCompletionsSupported = probeResult?.ChatCompletionsSupported ?? false,
            AnthropicMessagesSupported = probeResult?.AnthropicMessagesSupported ?? false,
            LocalProxy = IsLocalEndpoint(baseUrl),
            TargetSnapshot = new
            {
                TargetCount = Math.Max(0, targetCount),
                SelectedTargetCount = Math.Max(0, selectedTargetCount),
                SelectableTargetCount = Math.Max(0, selectableTargetCount),
                InstalledTargetCount = Math.Max(0, installedTargetCount),
                LastChangedFileCount = Math.Max(0, lastChangedFileCount),
                LastBackupFileCount = Math.Max(0, lastBackupFileCount)
            },
            Succeeded = succeeded,
            Summary = summary,
            Error = error ?? string.Empty,
            ChangedFileCount = changedFiles.Count,
            BackupFileCount = backupFiles.Count,
            ChangedFiles = changedFiles,
            BackupFiles = backupFiles,
            Probe = probeResult is null
                ? null
                : new
                {
                    probeResult.CheckedAt,
                    probeResult.BaseUrl,
                    probeResult.ProbeModel,
                    probeResult.PreferredWireApi,
                    probeResult.Summary,
                    probeResult.Error,
                    probeResult.ResponsesSupported,
                    probeResult.ChatCompletionsSupported,
                    probeResult.AnthropicMessagesSupported
                }
        };

        return JsonSerializer.Serialize(payload);
    }

    internal static string BuildApplicationAccessBatchHistoryPayloadJson(
        string action,
        string baseUrl,
        string apiKey,
        string model,
        int availableModelCount,
        string preferredProtocol,
        IReadOnlyList<ApplicationAccessOperationReceipt> receipts,
        int targetCount = 0,
        int selectedTargetCount = 0,
        int selectableTargetCount = 0,
        int installedTargetCount = 0,
        int lastChangedFileCount = 0,
        int lastBackupFileCount = 0)
    {
        var succeeded = receipts.Count(static item => item.Succeeded);
        var changedFiles = receipts.Sum(static item => item.ChangedFileCount);
        var backupFiles = receipts.Sum(static item => item.BackupFileCount);
        var payload = new
        {
            Schema = "application-access-batch-v1",
            CapturedAtUtc = DateTime.UtcNow,
            Action = action,
            TargetId = "selected-targets",
            TargetName = action.Equals("restore-selected", StringComparison.OrdinalIgnoreCase)
                ? "\u6279\u91CF\u8FD8\u539F"
                : "\u6279\u91CF\u5199\u5165",
            ProtocolKind = "Batch",
            Endpoint = baseUrl.Trim(),
            BaseUrl = baseUrl.Trim(),
            Model = model.Trim(),
            ApiKeyMasked = MaskApiKey(apiKey),
            AvailableModelCount = Math.Max(0, availableModelCount),
            PreferredWireApi = preferredProtocol,
            LocalProxy = IsLocalEndpoint(baseUrl),
            TargetSnapshot = new
            {
                TargetCount = Math.Max(0, targetCount),
                SelectedTargetCount = Math.Max(0, selectedTargetCount),
                SelectableTargetCount = Math.Max(0, selectableTargetCount),
                InstalledTargetCount = Math.Max(0, installedTargetCount),
                LastChangedFileCount = Math.Max(0, lastChangedFileCount),
                LastBackupFileCount = Math.Max(0, lastBackupFileCount)
            },
            Succeeded = succeeded == receipts.Count,
            TargetCount = receipts.Count,
            SucceededTargetCount = succeeded,
            FailedTargetCount = receipts.Count - succeeded,
            ChangedFileCount = changedFiles,
            BackupFileCount = backupFiles,
            ChangedFiles = receipts
                .SelectMany(static item => item.ChangedFiles ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            BackupFiles = receipts
                .SelectMany(static item => item.BackupFiles ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Summary = $"{succeeded}/{receipts.Count} targets succeeded, {changedFiles} files changed",
            Targets = receipts.Select(static item => new
            {
                item.TargetId,
                item.TargetName,
                item.Succeeded,
                item.Summary,
                item.ChangedFileCount,
                item.BackupFileCount,
                ChangedFiles = item.ChangedFiles ?? Array.Empty<string>(),
                BackupFiles = item.BackupFiles ?? Array.Empty<string>(),
                item.Error
            }).ToArray()
        };

        return JsonSerializer.Serialize(payload);
    }

}
