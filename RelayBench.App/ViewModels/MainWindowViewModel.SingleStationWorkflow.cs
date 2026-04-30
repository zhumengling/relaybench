namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool IsSingleStationQuickMode
        => string.Equals(SelectedSingleStationModeKey, "quick", StringComparison.Ordinal);

    public bool IsSingleStationStabilityMode
        => string.Equals(SelectedSingleStationModeKey, "stability", StringComparison.Ordinal);

    public bool IsSingleStationDeepMode
        => string.Equals(SelectedSingleStationModeKey, "deep", StringComparison.Ordinal);

    public bool IsSingleStationConcurrencyMode
        => string.Equals(SelectedSingleStationModeKey, "concurrency", StringComparison.Ordinal);

    public string SingleStationModeDescription
        => SelectedSingleStationModeKey switch
        {
            "stability" => "多轮看成功率和波动",
            "deep" => "测兼容、缓存、多模态、对照",
            "concurrency" => "测并发上限、限流点、P95、tok/s",
            _ => "测可用性、延迟、TTFT、tok/s"
        };

    public string SingleStationPrimaryButtonText
        => SelectedSingleStationModeKey switch
        {
            "stability" => "\u5F00\u59CB\u7A33\u5B9A\u6027\u6D4B\u8BD5",
            "deep" => "\u5F00\u59CB\u6DF1\u5EA6\u6D4B\u8BD5",
            "concurrency" => "\u5F00\u59CB\u5E76\u53D1\u538B\u6D4B",
            _ => "\u5F00\u59CB\u5FEB\u901F\u6D4B\u8BD5"
        };

    public string SingleStationResultSummary
        => SelectedSingleStationModeKey switch
        {
            "stability" => ProxyStabilitySummary,
            "concurrency" => ProxyConcurrencySummary,
            _ => ProxySummary
        };

    public string SingleStationResultDetail
        => SelectedSingleStationModeKey switch
        {
            "stability" => ProxyStabilityDetail,
            "concurrency" => ProxyConcurrencyDetail,
            _ => ProxyDetail
        };

    public string SingleStationKpiVerdictText
        => ExtractMetricValue(SingleStationResultSummary, "总判定") ??
           ExtractMetricValue(SingleStationResultSummary, "健康度") ??
           ExtractMetricValue(SingleStationResultSummary, "摘要") ??
           DashboardCards.ElementAtOrDefault(3)?.Status ??
           "待运行";

    public string SingleStationKpiLatencyText
        => ExtractMetricValue(ProxyKeyMetricsSummary, "普通对话延迟") ??
           ExtractMetricValue(SingleStationResultSummary, "平均普通对话延迟") ??
           ExtractMetricValue(SingleStationResultSummary, "普通对话成功率") ??
           "--";

    public string SingleStationKpiTtftText
        => ExtractMetricValue(ProxyKeyMetricsSummary, "流式 TTFT") ??
           ExtractMetricValue(SingleStationResultSummary, "平均首 Token 时间") ??
           ExtractMetricValue(SingleStationResultSummary, "流式成功率") ??
           "--";

    public string SingleStationKpiThroughputText
        => ExtractMetricValue(ProxyKeyMetricsSummary, "独立吞吐") ??
           ExtractMetricValue(ProxyKeyMetricsSummary, "流式探针输出速率") ??
           "待测试";

    public string SingleStationKpiProtocolText
        => BuildSingleStationProtocolKpiText();

    private Task RunSelectedSingleStationModeAsync()
        => SelectedSingleStationModeKey switch
        {
            "stability" => RunProxySeriesWithValidationAsync(),
            "deep" => RunProxyDeepWithValidationAsync(),
            "concurrency" => RunProxyConcurrencyWithValidationAsync(),
            _ => RunProxyWithValidationAsync()
        };

    private void NotifySingleStationModeStateChanged()
    {
        OnPropertyChanged(nameof(IsSingleStationQuickMode));
        OnPropertyChanged(nameof(IsSingleStationStabilityMode));
        OnPropertyChanged(nameof(IsSingleStationDeepMode));
        OnPropertyChanged(nameof(IsSingleStationConcurrencyMode));
        OnPropertyChanged(nameof(SingleStationModeDescription));
        OnPropertyChanged(nameof(SingleStationPrimaryButtonText));
        OnPropertyChanged(nameof(SingleStationResultSummary));
        OnPropertyChanged(nameof(SingleStationResultDetail));
        NotifySingleStationDashboardStateChanged();
    }

    private void NotifySingleStationDashboardStateChanged()
    {
        OnPropertyChanged(nameof(SingleStationKpiVerdictText));
        OnPropertyChanged(nameof(SingleStationKpiLatencyText));
        OnPropertyChanged(nameof(SingleStationKpiTtftText));
        OnPropertyChanged(nameof(SingleStationKpiThroughputText));
        OnPropertyChanged(nameof(SingleStationKpiProtocolText));
    }

    private string BuildSingleStationProtocolKpiText()
    {
        var summary = ProxyCapabilityMatrixSummary;
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "待探测";
        }

        var supported = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(static line => line.Contains("支持", StringComparison.Ordinal) ||
                                  line.Contains("成功", StringComparison.Ordinal) ||
                                  line.Contains("通过", StringComparison.Ordinal));

        return supported > 0 ? $"{supported} 项可用" : "看矩阵";
    }

    private static string? ExtractMetricValue(string? source, string label)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        foreach (var rawLine in source.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(label, StringComparison.Ordinal))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('：');
            if (separatorIndex < 0)
            {
                separatorIndex = line.IndexOf(':');
            }

            if (separatorIndex >= 0 && separatorIndex + 1 < line.Length)
            {
                return line[(separatorIndex + 1)..].Trim();
            }
        }

        return null;
    }
}
