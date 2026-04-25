using System.Text;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void LoadProxyBatchRankingState(AppStateSnapshot snapshot)
    {
        var state = snapshot.ProxyBatchRankingState;
        if (state?.Rows is not { Count: > 0 })
        {
            return;
        }

        foreach (var existingRow in ProxyBatchRankingRows)
        {
            existingRow.PropertyChanged -= OnProxyBatchRankingRowPropertyChanged;
        }

        ProxyBatchRankingRows.Clear();
        foreach (var item in state.Rows
                     .Where(row => !string.IsNullOrWhiteSpace(row.BaseUrl))
                     .OrderBy(row => row.Rank <= 0 ? int.MaxValue : row.Rank))
        {
            var row = new ProxyBatchRankingRowViewModel
            {
                IsSelected = item.IsSelected,
                Rank = item.Rank <= 0 ? ProxyBatchRankingRows.Count + 1 : item.Rank,
                EntryName = string.IsNullOrWhiteSpace(item.EntryName) ? item.BaseUrl : item.EntryName,
                BaseUrl = item.BaseUrl,
                Model = item.Model,
                QuickVerdict = string.IsNullOrWhiteSpace(item.QuickVerdict) ? "已恢复" : item.QuickVerdict,
                QuickMetrics = string.IsNullOrWhiteSpace(item.QuickMetrics) ? "--" : item.QuickMetrics,
                CapabilitySummary = string.IsNullOrWhiteSpace(item.CapabilitySummary) ? "--" : item.CapabilitySummary,
                DeepStatus = string.IsNullOrWhiteSpace(item.DeepStatus) ? "未开始" : item.DeepStatus,
                DeepSummary = item.DeepSummary,
                DeepCheckedAt = string.IsNullOrWhiteSpace(item.DeepCheckedAt) ? "--" : item.DeepCheckedAt,
                CompositeScore = item.CompositeScore,
                StabilityRatio = item.StabilityRatio,
                TtftMs = item.TtftMs,
                ChatLatencyMs = item.ChatLatencyMs,
                TokensPerSecond = item.TokensPerSecond,
                Verdict = item.Verdict,
                SecondaryText = item.SecondaryText,
                RunCount = item.RunCount,
                ApiKey = item.ApiKey
            };

            row.PropertyChanged += OnProxyBatchRankingRowPropertyChanged;
            ProxyBatchRankingRows.Add(row);
        }

        if (ProxyBatchRankingRows.Count == 0)
        {
            return;
        }

        ProxyBatchSummary = string.IsNullOrWhiteSpace(state.Summary)
            ? BuildRestoredProxyBatchSummary(ProxyBatchRankingRows)
            : state.Summary;
        ProxyBatchDetail = string.IsNullOrWhiteSpace(state.Detail)
            ? BuildRestoredProxyBatchDetail(ProxyBatchRankingRows)
            : state.Detail;
        ProxyBatchRecommendationSummary = BuildRestoredProxyBatchRecommendation(ProxyBatchRankingRows);
        ProxyBatchQuickCompareCompleted = true;
        BatchDeepTestSummary = "已恢复上次入口组快测候选项，可继续勾选深测。";

        RestoreProxyBatchComparisonChartSnapshot(state);
        RefreshBatchSelectionState();
        RefreshProxyUnifiedOutput();
    }

    private void ApplyProxyBatchRankingStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.ProxyBatchRankingState = new ProxyBatchRankingStateSnapshot
        {
            UpdatedAt = DateTimeOffset.Now,
            Summary = ProxyBatchSummary,
            Detail = ProxyBatchDetail,
            ChartStatusSummary = BatchComparisonChartStatusSummary,
            Rows = ProxyBatchRankingRows
                .OrderBy(row => row.Rank <= 0 ? int.MaxValue : row.Rank)
                .Select(row => new ProxyBatchRankingRowSnapshot
                {
                    IsSelected = row.IsSelected,
                    Rank = row.Rank,
                    EntryName = row.EntryName,
                    BaseUrl = row.BaseUrl,
                    ApiKey = row.ApiKey,
                    Model = row.Model,
                    QuickVerdict = row.QuickVerdict,
                    QuickMetrics = row.QuickMetrics,
                    CapabilitySummary = row.CapabilitySummary,
                    DeepStatus = row.DeepStatus,
                    DeepSummary = row.DeepSummary,
                    DeepCheckedAt = row.DeepCheckedAt,
                    CompositeScore = row.CompositeScore,
                    StabilityRatio = row.StabilityRatio,
                    TtftMs = row.TtftMs,
                    ChatLatencyMs = row.ChatLatencyMs,
                    TokensPerSecond = row.TokensPerSecond,
                    Verdict = row.Verdict,
                    SecondaryText = row.SecondaryText,
                    RunCount = row.RunCount
                })
                .ToList()
        };
    }

    private void RestoreProxyBatchComparisonChartSnapshot(ProxyBatchRankingStateSnapshot state)
    {
        var chartItems = state.Rows
            .Where(row => !string.IsNullOrWhiteSpace(row.BaseUrl))
            .OrderBy(row => row.Rank <= 0 ? int.MaxValue : row.Rank)
            .Select(row => new ProxyBatchComparisonChartItem(
                row.Rank <= 0 ? 1 : row.Rank,
                string.IsNullOrWhiteSpace(row.EntryName) ? row.BaseUrl : row.EntryName,
                row.BaseUrl,
                row.CompositeScore,
                row.CompositeScore > 0d ? $"{row.CompositeScore:F1} 分" : "--",
                row.StabilityRatio,
                string.IsNullOrWhiteSpace(row.QuickVerdict) ? "已恢复" : row.QuickVerdict,
                row.TtftMs,
                row.ChatLatencyMs,
                row.TokensPerSecond,
                string.IsNullOrWhiteSpace(row.Verdict) ? row.QuickVerdict : row.Verdict,
                string.IsNullOrWhiteSpace(row.SecondaryText) ? row.CapabilitySummary : row.SecondaryText,
                row.RunCount))
            .ToArray();

        if (chartItems.Length == 0)
        {
            return;
        }

        var chartResult = _proxyBatchComparisonChartRenderService.Render(
            chartItems,
            ResolvePreferredBatchChartWidth());
        var best = chartItems.OrderByDescending(item => item.CompositeScore).FirstOrDefault() ?? chartItems[0];
        var summary = string.IsNullOrWhiteSpace(state.ChartStatusSummary)
            ? chartResult.HasChart ? chartResult.Summary : chartResult.Error ?? chartResult.Summary
            : state.ChartStatusSummary;

        SetProxyChartSnapshot(
            ProxyChartViewMode.BatchComparison,
            new ProxyChartDialogSnapshot(
                "接口入口组累计对比图",
                "这里恢复上次入口组测试的累计排行榜，关闭软件后仍保留最后一次结果。",
                $"已恢复上次入口组结果，共 {chartItems.Length} 个 URL。\n当前推荐：{best.Name}\n推荐地址：{best.BaseUrl}\n综合分：{best.CompositeText}\n\n{BuildRestoredProxyBatchTopSummary(ProxyBatchRankingRows)}",
                "恢复的候选列表会继续显示上次长期汇总统计中排序最靠前的结果；重新运行入口组检测后会用新累计结果覆盖。",
                BuildRestoredProxyBatchDetail(ProxyBatchRankingRows),
                "蓝条：平均普通对话延迟；紫条：独立吞吐；橙条：平均 TTFT；绿条：综合分。",
                summary,
                "正在等待入口组累计图表生成。",
                chartResult.ChartImage,
                BatchComparisonItems: chartItems),
            activate: false);
    }

    private static string BuildRestoredProxyBatchSummary(IEnumerable<ProxyBatchRankingRowViewModel> rows)
    {
        var ordered = rows.OrderBy(row => row.Rank).ToArray();
        var best = ordered.FirstOrDefault();
        if (best is null)
        {
            return "入口组检测尚未采集到有效结果。";
        }

        return
            $"已恢复上次入口组结果。\n" +
            $"URL 数：{ordered.Length}\n" +
            $"累计整组轮次：{ordered.Max(row => row.RunCount)}\n" +
            $"当前推荐：{best.EntryName}\n" +
            $"推荐地址：{best.BaseUrl}\n" +
            $"推荐模型：{best.Model}\n" +
            $"推荐理由：平均普通对话 {FormatMillisecondsValue(best.ChatLatencyMs)}，平均 TTFT {FormatMillisecondsValue(best.TtftMs)}，平均独立吞吐 {FormatTokensPerSecond(best.TokensPerSecond)}，综合分 {best.CompositeScore:F1}。";
    }

    private static string BuildRestoredProxyBatchDetail(IEnumerable<ProxyBatchRankingRowViewModel> rows)
    {
        StringBuilder builder = new();
        foreach (var item in rows.OrderBy(row => row.Rank))
        {
            builder.AppendLine($"#{item.Rank} {item.EntryName}");
            builder.AppendLine($"地址：{item.BaseUrl}");
            builder.AppendLine($"请求模型：{item.Model}");
            builder.AppendLine($"累计轮次：{item.RunCount}");
            builder.AppendLine($"稳定性：{item.QuickVerdict}");
            builder.AppendLine($"综合分：{item.CompositeScore:F1}");
            builder.AppendLine($"平均普通对话：{FormatMillisecondsValue(item.ChatLatencyMs)}");
            builder.AppendLine($"平均 TTFT：{FormatMillisecondsValue(item.TtftMs)}");
            builder.AppendLine($"平均独立吞吐：{FormatTokensPerSecond(item.TokensPerSecond)}");
            builder.AppendLine($"能力摘要：{item.CapabilitySummary}");
            builder.AppendLine($"最近结论：{(string.IsNullOrWhiteSpace(item.Verdict) ? item.QuickVerdict : item.Verdict)}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildRestoredProxyBatchRecommendation(IEnumerable<ProxyBatchRankingRowViewModel> rows)
    {
        var best = rows.OrderBy(row => row.Rank).FirstOrDefault();
        if (best is null)
        {
            return "入口组检测尚未运行。";
        }

        return
            $"当前推荐项：{best.EntryName}\n" +
            $"节点地址：{best.BaseUrl}\n" +
            $"密钥：{MaskApiKey(best.ApiKey)}\n" +
            $"模型：{best.Model}\n" +
            $"累计整组轮次：{best.RunCount}\n" +
            $"稳定性：{best.QuickVerdict}\n" +
            $"综合分：{best.CompositeScore:F1}\n" +
            $"平均普通对话：{FormatMillisecondsValue(best.ChatLatencyMs)}\n" +
            $"平均 TTFT：{FormatMillisecondsValue(best.TtftMs)}\n" +
            $"平均独立吞吐：{FormatTokensPerSecond(best.TokensPerSecond)}\n" +
            $"说明：这是从上次入口组长期汇总统计恢复的候选列表，重新检测后会刷新为最新累计结果。";
    }

    private static string BuildRestoredProxyBatchTopSummary(IEnumerable<ProxyBatchRankingRowViewModel> rows)
        => string.Join(
            "\n",
            rows
                .OrderBy(row => row.Rank)
                .Take(5)
                .Select(row =>
                    $"TOP {row.Rank}  {row.EntryName}  |  平均普通 {FormatMillisecondsValue(row.ChatLatencyMs)}  |  独立吞吐 {FormatTokensPerSecond(row.TokensPerSecond)}  |  平均 TTFT {FormatMillisecondsValue(row.TtftMs)}  |  综合分 {row.CompositeScore:F1}"));
}
