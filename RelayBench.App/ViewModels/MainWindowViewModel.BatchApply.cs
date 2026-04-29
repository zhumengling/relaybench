using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool CanApplyRankingRowToCodexApps(ProxyBatchRankingRowViewModel? row)
        => !IsBusy &&
           row is not null &&
           !string.IsNullOrWhiteSpace(row.BaseUrl) &&
           !string.IsNullOrWhiteSpace(row.ApiKey) &&
           !string.IsNullOrWhiteSpace(row.Model);

    private async Task ApplyRankingRowToCodexAppsAsync(ProxyBatchRankingRowViewModel? row)
    {
        if (row is null)
        {
            StatusMessage = "没有可应用的软件入口。";
            return;
        }

        if (string.IsNullOrWhiteSpace(row.BaseUrl) ||
            string.IsNullOrWhiteSpace(row.ApiKey) ||
            string.IsNullOrWhiteSpace(row.Model))
        {
            StatusMessage = $"“{row.EntryName}”缺少地址、密钥或模型，暂时不能应用。";
            return;
        }

        var settings = BuildProxySettings(row.BaseUrl, row.ApiKey, row.Model);
        var protocolProbeResult = await ProbeEndpointProtocolBeforeApplyAsync(settings, row.EntryName);
        if (protocolProbeResult is null)
        {
            return;
        }

        var selectedTargets = await ChooseClientApplyTargetsAsync(
            $"应用“{row.EntryName}”到软件",
            settings,
            protocolProbeResult);
        if (selectedTargets.Count == 0)
        {
            StatusMessage = $"已取消“{row.EntryName}”的应用。";
            return;
        }

        await ExecuteBusyActionAsync(
            $"正在应用“{row.EntryName}”...",
            async () =>
            {
                var cachedApplyInfo = await ResolveCachedCodexApplyInfoAsync(
                    row.BaseUrl,
                    row.ApiKey,
                    row.Model);
                var endpoint = new ClientApplyEndpoint(
                    row.BaseUrl,
                    row.ApiKey,
                    row.Model,
                    CodexOpenAiProviderDisplayName,
                    cachedApplyInfo.ContextWindow,
                    cachedApplyInfo.PreferredWireApi);
                var result = await _clientAppConfigApplyService.ApplyAsync(endpoint, selectedTargets);
                CodexChatMergeResult? mergeResult = null;
                if (ShouldAskCodexChatMerge(selectedTargets, result))
                {
                    var shouldMergeChats = await ConfirmCodexChatMergeAsync(
                        CodexChatMergeTarget.ThirdPartyCustom,
                        $"切到第三方（{row.EntryName}）");
                    mergeResult = await MergeCodexChatsIfRequestedAsync(
                        shouldMergeChats,
                        CodexChatMergeTarget.ThirdPartyCustom);
                }

                StatusMessage = BuildClientApplyStatusMessage(result, mergeResult);

                AppendModuleOutput(
                    "排行榜入口应用到软件",
                    BuildClientApplyResultSummary(result, mergeResult),
                    BuildClientApplyResultDetail(
                        $"排行榜入口：{row.EntryName}",
                        row.BaseUrl,
                        row.ApiKey,
                        row.Model,
                        result,
                        mergeResult));

                if (HasSucceededTarget(result))
                {
                    ProxyBaseUrl = row.BaseUrl;
                    ProxyApiKey = row.ApiKey;
                    ProxyModel = row.Model;
                    SaveState();
                    ShowBatchRankingApplyToast(BuildBatchRankingApplyToastMessage(result, mergeResult));
                }
            });
    }

    private static string BuildBatchRankingApplyToastMessage(
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
        => result.AppliedTargets.Count == 0
            ? "未发现可应用的 Codex 软件"
            : mergeResult is { Succeeded: true }
                ? $"已应用到：{string.Join("、", result.AppliedTargets)}；聊天已整理"
                : $"已应用到：{string.Join("、", result.AppliedTargets)}";

    private void ShowBatchRankingApplyToast(string message)
    {
        _batchRankingApplyToastCancellationSource?.Cancel();
        _batchRankingApplyToastCancellationSource?.Dispose();
        _batchRankingApplyToastCancellationSource = new CancellationTokenSource();

        BatchRankingApplyToastMessage = message;
        IsBatchRankingApplyToastVisible = true;

        _ = HideBatchRankingApplyToastLaterAsync(_batchRankingApplyToastCancellationSource.Token);
    }

    private async Task HideBatchRankingApplyToastLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1600, cancellationToken);
            IsBatchRankingApplyToastVisible = false;
            await Task.Delay(180, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                BatchRankingApplyToastMessage = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
