using NetTest.App.Infrastructure;
using NetTest.App.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string BuildConclusionSummary(
        RelayRecommendationSnapshot relayRecommendation,
        TrendSummarySnapshot trendSnapshot,
        UnlockSemanticSnapshot unlockSnapshot,
        NatReviewSnapshot natSnapshot)
    {
        return
            $"{relayRecommendation.Summary}\n\n" +
            $"{trendSnapshot.Summary}\n\n" +
            $"{unlockSnapshot.Summary}\n\n" +
            $"{natSnapshot.Summary}";
    }

    private RelayRecommendationSnapshot BuildRelayRecommendationSnapshot()
    {
        if (_proxyBatchChartRuns.Count > 0)
        {
            var bestRow = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns))
                .First();

            return new RelayRecommendationSnapshot(
                "入口组对比",
                bestRow.Entry.Name,
                bestRow.Entry.BaseUrl,
                bestRow.CompositeScore,
                $"本次推荐接口：{bestRow.Entry.Name}（{bestRow.Entry.BaseUrl}），综合分 {bestRow.CompositeScore:F1}，平均普通对话 {FormatMillisecondsValue(bestRow.AverageChatLatencyMs)}，平均 TTFT {FormatMillisecondsValue(bestRow.AverageTtftMs)}，独立吞吐 {FormatTokensPerSecond(bestRow.AverageBenchmarkTokensPerSecond)}。");
        }

        if (_lastProxyStabilityResult is not null)
        {
            return new RelayRecommendationSnapshot(
                "稳定性序列",
                TryBuildRelayDisplayName(_lastProxyStabilityResult.BaseUrl),
                _lastProxyStabilityResult.BaseUrl,
                _lastProxyStabilityResult.HealthScore,
                $"当前可参考接口：{_lastProxyStabilityResult.BaseUrl}，完整成功率 {_lastProxyStabilityResult.FullSuccessRate:F1}%，平均普通对话 {FormatMilliseconds(_lastProxyStabilityResult.AverageChatLatency)}，平均 TTFT {FormatMilliseconds(_lastProxyStabilityResult.AverageStreamFirstTokenLatency)}。");
        }

        if (_lastProxySingleResult is not null)
        {
            var readyText = IsFullSuccess(_lastProxySingleResult) ? "当前探测通过" : "当前探测仍需复核";
            return new RelayRecommendationSnapshot(
                "单次探测",
                TryBuildRelayDisplayName(_lastProxySingleResult.BaseUrl),
                _lastProxySingleResult.BaseUrl,
                null,
                $"最新单次探测目标：{_lastProxySingleResult.BaseUrl}，{readyText}，普通对话 {FormatMilliseconds(_lastProxySingleResult.ChatLatency)}，TTFT {FormatMilliseconds(_lastProxySingleResult.StreamFirstTokenLatency)}。");
        }

        return new RelayRecommendationSnapshot(
            "无数据",
            null,
            null,
            null,
            "本次还没有可用于推荐的接口样本。");
    }

    private TrendSummarySnapshot BuildTrendSummarySnapshot()
    {
        var target = ResolveProxyTrendTarget(null);
        var records = string.IsNullOrWhiteSpace(target)
            ? Array.Empty<ProxyTrendEntry>()
            : (_lastProxyTrendRecords.Count == 0
                ? _proxyTrendStore.GetEntriesSince(target, DateTimeOffset.Now.AddHours(-24), limit: 240)
                : _lastProxyTrendRecords);

        if (records.Count == 0)
        {
            return new TrendSummarySnapshot(
                string.IsNullOrWhiteSpace(target) ? "未指定目标" : ProxyTrendStore.NormalizeBaseUrl(target),
                0,
                "过去 24 小时稳定性变化：样本不足，暂时无法形成趋势结论。",
                null,
                null,
                null,
                null,
                null,
                null);
        }

        var comparison = AnalyzeProxyTrend(records);
        return new TrendSummarySnapshot(
            ProxyTrendStore.NormalizeBaseUrl(target),
            records.Count,
            $"过去 24 小时稳定性变化：{comparison.Summary}",
            comparison.EarlierStability,
            comparison.LaterStability,
            comparison.EarlierChatLatency,
            comparison.LaterChatLatency,
            comparison.EarlierTtft,
            comparison.LaterTtft);
    }

    private UnlockSemanticSnapshot BuildUnlockSemanticSnapshot()
    {
        if (_lastUnlockCatalogResult is null)
        {
            return new UnlockSemanticSnapshot(
                "扩展可用性目录：暂无样本。",
                null,
                null,
                null,
                null,
                null);
        }

        return new UnlockSemanticSnapshot(
            $"扩展可用性目录：业务就绪 {_lastUnlockCatalogResult.SemanticReadyCount}/{_lastUnlockCatalogResult.Checks.Count}，需鉴权 {_lastUnlockCatalogResult.AuthenticationRequiredCount}，疑似地区限制 {_lastUnlockCatalogResult.RegionRestrictedCount}，待复核 {_lastUnlockCatalogResult.ReviewRequiredCount}。",
            _lastUnlockCatalogResult.SemanticReadyCount,
            _lastUnlockCatalogResult.AuthenticationRequiredCount,
            _lastUnlockCatalogResult.RegionRestrictedCount,
            _lastUnlockCatalogResult.ReviewRequiredCount,
            _lastUnlockCatalogResult.Checks.Count);
    }

    private NatReviewSnapshot BuildNatReviewSnapshot()
    {
        if (_lastStunResult is null)
        {
            return new NatReviewSnapshot(
                "NAT 判断：暂无 STUN 样本。",
                null,
                null,
                null,
                null);
        }

        return new NatReviewSnapshot(
            $"NAT 判断：{_lastStunResult.NatType ?? "未归类"}，可信度 {_lastStunResult.ClassificationConfidence}。复核建议：{_lastStunResult.ReviewRecommendation}",
            _lastStunResult.NatType,
            _lastStunResult.ClassificationConfidence,
            _lastStunResult.CoverageSummary,
            _lastStunResult.ReviewRecommendation);
    }
}
