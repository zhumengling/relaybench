using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _proxyManagedEntryAssessmentSummary = "基础或深度单次诊断完成后，这里会结合已管理入口给出当前 URL 在入口组中的参照位置。";

    public string ProxyManagedEntryAssessmentSummary
    {
        get => _proxyManagedEntryAssessmentSummary;
        private set => SetProperty(ref _proxyManagedEntryAssessmentSummary, value);
    }

    private void RefreshProxyManagedEntryAssessment(ProxyDiagnosticsResult result)
    {
        var managedEntries = ProxyBatchEditorItems
            .Where(item => !string.IsNullOrWhiteSpace(item.BaseUrl))
            .GroupBy(item => ProxyTrendStore.NormalizeBaseUrl(item.BaseUrl))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var preferred = group
                    .OrderByDescending(item => item.DisplayTitle.Length)
                    .ThenBy(item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                    .First();

                return new ManagedEntryReference(
                    preferred.DisplayTitle,
                    preferred.BaseUrl.Trim(),
                    group.Key);
            })
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (managedEntries.Length == 0)
        {
            ProxyManagedEntryAssessmentSummary =
                "已管理入口参照：当前还没有录入入口组，单次诊断只能说明这个 URL 自身状态。" +
                "如果你想直接判断多个 URL 哪个更稳，请先点“管理入口组”录入多个地址。";
            return;
        }

        var currentNormalizedBaseUrl = ProxyTrendStore.NormalizeBaseUrl(result.BaseUrl);
        var currentManagedEntry = managedEntries.FirstOrDefault(entry =>
            string.Equals(entry.NormalizedBaseUrl, currentNormalizedBaseUrl, StringComparison.OrdinalIgnoreCase));
        var currentSourceSummary = GetSingleProxyExecutionDisplayName();

        List<ManagedEntryAssessment> assessments = [];

        foreach (var managedEntry in managedEntries)
        {
            if (string.Equals(managedEntry.NormalizedBaseUrl, currentNormalizedBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                assessments.Add(BuildCurrentManagedEntryAssessment(
                    result,
                    managedEntry.DisplayName,
                    managedEntry.BaseUrl,
                    fromManagedList: true,
                    sourceSummary: currentSourceSummary));
                continue;
            }

            if (TryBuildManagedEntryAssessment(managedEntry, out var assessment))
            {
                assessments.Add(assessment);
            }
        }

        if (currentManagedEntry is null)
        {
            assessments.Add(BuildCurrentManagedEntryAssessment(
                result,
                "当前单次 URL",
                result.BaseUrl,
                fromManagedList: false,
                sourceSummary: currentSourceSummary));
        }

        var observedManagedCount = assessments.Count(item => item.FromManagedList);
        if (observedManagedCount == 0 && !assessments.Any(item => item.FromManagedList))
        {
            var currentOnly = assessments[0];
            ProxyManagedEntryAssessmentSummary =
                $"已管理入口共 {managedEntries.Length} 个，但这些入口还没有可比记录。\n" +
                $"当前结果：{currentOnly.StabilityLabel}；普通延迟 {FormatMillisecondsValue(currentOnly.ChatLatencyMs)}；TTFT {FormatMillisecondsValue(currentOnly.TtftMs)}；依据 {currentOnly.SourceSummary}。\n" +
                "建议先运行一次入口组检测，这样才能直接判断哪个 URL 更稳。";
            return;
        }

        if (currentManagedEntry is not null && observedManagedCount <= 1)
        {
            var currentOnly = assessments.First(item => item.IsCurrentUrl);
            ProxyManagedEntryAssessmentSummary =
                $"已管理入口共 {managedEntries.Length} 个，但目前只有当前 URL 拿到了有效检测记录。\n" +
                $"当前结果：{currentOnly.StabilityLabel}；普通延迟 {FormatMillisecondsValue(currentOnly.ChatLatencyMs)}；TTFT {FormatMillisecondsValue(currentOnly.TtftMs)}；依据 {currentOnly.SourceSummary}。\n" +
                "如果要判断同组里哪个入口更稳，建议现在直接运行一次入口组检测。";
            return;
        }

        var ranked = assessments
            .OrderByDescending(item => item.StabilityScore)
            .ThenBy(item => item.ChatLatencyMs ?? double.MaxValue)
            .ThenBy(item => item.TtftMs ?? double.MaxValue)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var current = ranked.First(item => item.IsCurrentUrl);
        var currentRank = Array.IndexOf(ranked, current) + 1;
        var comparableCount = current.FromManagedList ? observedManagedCount : ranked.Length;
        var missingDataCount = managedEntries.Count(entry =>
            !ranked.Any(item =>
                item.FromManagedList &&
                string.Equals(item.NormalizedBaseUrl, entry.NormalizedBaseUrl, StringComparison.OrdinalIgnoreCase)));

        var scopeSummary = current.FromManagedList
            ? $"当前 URL 已在入口组中，在 {comparableCount} 个已观测入口里排第 {currentRank}。"
            : $"当前 URL 还未加入入口组；已拿 {observedManagedCount} 个已管理入口做参照，当前估计排第 {currentRank}。";

        var placementAdvice = BuildManagedEntryPlacementAdvice(currentRank, comparableCount, current.FromManagedList);
        var topLines = string.Join(
            "\n",
            ranked.Take(Math.Min(3, ranked.Length)).Select((item, index) => BuildManagedEntryTopLine(item, index + 1)));

        ProxyManagedEntryAssessmentSummary =
            $"已管理入口参照：{scopeSummary}\n" +
            $"当前判断：{current.StabilityLabel}；普通延迟 {FormatMillisecondsValue(current.ChatLatencyMs)}；TTFT {FormatMillisecondsValue(current.TtftMs)}；依据 {current.SourceSummary}。\n" +
            $"{placementAdvice}\n" +
            $"已观测入口 TOP：\n{topLines}" +
            (missingDataCount > 0
                ? $"\n未纳入排序：{missingDataCount} 个已管理 URL 还没有批量或趋势记录，建议后续补跑入口组检测。"
                : string.Empty);
    }
}
