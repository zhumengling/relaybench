using System.Text;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public string ProxyUnifiedOutput
        => BuildProxyUnifiedOutput();

    private void RefreshProxyUnifiedOutput()
        => OnPropertyChanged(nameof(ProxyUnifiedOutput));

    private string BuildProxyUnifiedOutput()
    {
        StringBuilder builder = new();
        var singleExecutionDisplayName = GetSingleProxyExecutionDisplayName();

        AppendUnifiedSection(builder, "运行过程", LiveOutput);
        AppendUnifiedSection(builder, "总判定与建议", ProxyVerdictSummary);
        AppendUnifiedSection(builder, "能力矩阵", ProxyCapabilityMatrixSummary);
        AppendUnifiedSection(builder, $"{singleExecutionDisplayName}能力明细", ProxySingleCapabilityDetailSummary);
        AppendUnifiedSection(builder, "核心指标", ProxyKeyMetricsSummary);
        AppendUnifiedSection(builder, "长流稳定简测", ProxyLongStreamingSummary);
        AppendUnifiedSection(builder, "可追溯性", ProxyTraceabilitySummary);
        AppendUnifiedSection(builder, "已管理入口参照", ProxyManagedEntryAssessmentSummary);
        AppendUnifiedSection(builder, "问题定位", ProxyIssueSummary);
        AppendUnifiedSection(builder, "关键响应头", ProxyHeadersSummary);
        AppendUnifiedSection(builder, "稳定性结论", ProxyStabilityInsightSummary);
        AppendUnifiedSection(builder, "稳定性摘要", ProxyStabilitySummary);
        AppendUnifiedSection(builder, "稳定性逐轮明细", ProxyStabilityDetail);
        AppendUnifiedSection(builder, "趋势摘要", ProxyTrendSummary);
        AppendUnifiedSection(builder, "趋势图状态", ProxyTrendChartStatusSummary);
        AppendUnifiedSection(builder, "趋势明细", ProxyTrendDetail);
        AppendUnifiedSection(builder, "入口组推荐", ProxyBatchRecommendationSummary);
        AppendUnifiedSection(builder, "入口组摘要", ProxyBatchSummary);
        AppendUnifiedSection(builder, "入口组明细", ProxyBatchDetail);
        AppendUnifiedSection(builder, $"{singleExecutionDisplayName}原始摘要", ProxySummary);
        AppendUnifiedSection(builder, $"{singleExecutionDisplayName}原始明细", ProxyDetail);

        if (builder.Length == 0)
        {
            return "运行基础单次诊断、深度单次诊断、稳定性巡检或入口组检测后，这里会集中显示完整输出。";
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendUnifiedSection(StringBuilder builder, string title, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.AppendLine($"【{title}】");
        builder.AppendLine(normalized);
    }
}
