using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool TryBuildManagedEntryAssessment(
        ManagedEntryReference entry,
        out ManagedEntryAssessment assessment)
    {
        var batchRow = _lastProxyBatchRows.FirstOrDefault(row =>
            string.Equals(
                ProxyTrendStore.NormalizeBaseUrl(row.Result.BaseUrl),
                entry.NormalizedBaseUrl,
                StringComparison.OrdinalIgnoreCase));

        if (batchRow is not null)
        {
            assessment = BuildBatchManagedEntryAssessment(batchRow);
            return true;
        }

        var recentRecords = _proxyTrendStore.GetEntriesSince(entry.BaseUrl, DateTimeOffset.Now.AddHours(-48), limit: 12);
        if (recentRecords.Count == 0)
        {
            recentRecords = _proxyTrendStore.GetRecentEntries(entry.BaseUrl, limit: 6);
        }

        if (recentRecords.Count == 0)
        {
            assessment = default!;
            return false;
        }

        assessment = BuildTrendManagedEntryAssessment(entry, recentRecords);
        return true;
    }

    private static ManagedEntryAssessment BuildCurrentManagedEntryAssessment(
        ProxyDiagnosticsResult result,
        string displayName,
        string baseUrl,
        bool fromManagedList,
        string sourceSummary)
    {
        var passedCount = ResolveManagedEntryPassedCapabilityCount(result);
        var stabilityScore = Math.Round(passedCount * 100d / ManagedEntryCapabilityTotal, 1);
        var chatLatencyMs = result.ChatLatency?.TotalMilliseconds;
        var ttftMs = result.StreamFirstTokenLatency?.TotalMilliseconds;

        return new ManagedEntryAssessment(
            displayName,
            baseUrl,
            ProxyTrendStore.NormalizeBaseUrl(baseUrl),
            true,
            fromManagedList,
            stabilityScore,
            chatLatencyMs,
            ttftMs,
            ResolveManagedEntryStabilityLabel(stabilityScore, chatLatencyMs, ttftMs),
            sourceSummary);
    }

    private static ManagedEntryAssessment BuildBatchManagedEntryAssessment(ProxyBatchProbeRow row)
    {
        var stabilityScore = Math.Round(ResolveBatchPassedCapabilityCount(row) * 100d / ManagedEntryCapabilityTotal, 1);
        var chatLatencyMs = row.Result.ChatLatency?.TotalMilliseconds;
        var ttftMs = row.Result.StreamFirstTokenLatency?.TotalMilliseconds;

        return new ManagedEntryAssessment(
            row.Entry.Name,
            row.Result.BaseUrl,
            ProxyTrendStore.NormalizeBaseUrl(row.Result.BaseUrl),
            false,
            true,
            stabilityScore,
            chatLatencyMs,
            ttftMs,
            ResolveManagedEntryStabilityLabel(stabilityScore, chatLatencyMs, ttftMs),
            "最近一次入口组检测");
    }

    private static ManagedEntryAssessment BuildTrendManagedEntryAssessment(
        ManagedEntryReference entry,
        IReadOnlyList<ProxyTrendEntry> recentRecords)
    {
        var ordered = recentRecords
            .OrderByDescending(record => record.Timestamp)
            .Take(6)
            .OrderBy(record => record.Timestamp)
            .ToArray();

        var stabilityScore = Average(ordered.Select(record => (double?)ResolveSuccessRate(record))) ?? 0;
        var chatLatencyMs = Average(ordered.Select(record => record.ChatLatencyMs));
        var ttftMs = Average(ordered.Select(record => record.StreamFirstTokenLatencyMs));
        var sourceSummary = ordered.Length <= 1
            ? "最近一次历史记录"
            : $"近 48 小时 {ordered.Length} 条历史记录";

        return new ManagedEntryAssessment(
            entry.DisplayName,
            entry.BaseUrl,
            entry.NormalizedBaseUrl,
            false,
            true,
            Math.Round(stabilityScore, 1),
            chatLatencyMs,
            ttftMs,
            ResolveManagedEntryStabilityLabel(stabilityScore, chatLatencyMs, ttftMs),
            sourceSummary);
    }

    private static int ResolveManagedEntryPassedCapabilityCount(ProxyDiagnosticsResult result)
        => new[]
        {
            result.ModelsRequestSucceeded,
            result.ChatRequestSucceeded,
            result.StreamRequestSucceeded,
            FindScenario(GetScenarioResults(result), ProxyProbeScenarioKind.Responses)?.Success == true,
            FindScenario(GetScenarioResults(result), ProxyProbeScenarioKind.StructuredOutput)?.Success == true
        }.Count(value => value);

    private static string ResolveManagedEntryStabilityLabel(double stabilityScore, double? chatLatencyMs, double? ttftMs)
    {
        if (stabilityScore >= 95 &&
            (!chatLatencyMs.HasValue || chatLatencyMs.Value <= 1800) &&
            (!ttftMs.HasValue || ttftMs.Value <= 1800))
        {
            return "稳定";
        }

        if (stabilityScore >= 60 &&
            (!chatLatencyMs.HasValue || chatLatencyMs.Value <= 4500) &&
            (!ttftMs.HasValue || ttftMs.Value <= 3200))
        {
            return "可用";
        }

        return "待复核";
    }
}
