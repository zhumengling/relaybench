using System.Collections.ObjectModel;
using System.Text;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ExitIpRiskReviewService _exitIpRiskReviewService = new();
    private string _ipRiskTargetAddress = string.Empty;
    private string _ipRiskOverviewSummary = "运行后显示出口 IP、地区和 ASN。";
    private string _ipRiskAssessmentSummary = "暂无风险结论。";
    private string _ipRiskSourceDetailSummary = "暂无来源明细。";
    private string _ipRiskSummaryMetaText = "运行后显示检测时间和识别来源。";

    public string IpRiskTargetAddress
    {
        get => _ipRiskTargetAddress;
        set => SetProperty(ref _ipRiskTargetAddress, value);
    }

    public string IpRiskOverviewSummary
    {
        get => _ipRiskOverviewSummary;
        private set => SetProperty(ref _ipRiskOverviewSummary, value);
    }

    public string IpRiskAssessmentSummary
    {
        get => _ipRiskAssessmentSummary;
        private set => SetProperty(ref _ipRiskAssessmentSummary, value);
    }

    public string IpRiskSourceDetailSummary
    {
        get => _ipRiskSourceDetailSummary;
        private set => SetProperty(ref _ipRiskSourceDetailSummary, value);
    }

    public string IpRiskSummaryMetaText
    {
        get => _ipRiskSummaryMetaText;
        private set => SetProperty(ref _ipRiskSummaryMetaText, value);
    }

    private async Task RunIpRiskReviewCoreAsync()
    {
        UpdateGlobalTaskProgress("准备中", 12d);
        IProgress<string> progress = new Progress<string>(message =>
        {
            StatusMessage = message;
            UpdateGlobalTaskProgressForIpRiskMessage(message);
        });

        var targetAddress = string.IsNullOrWhiteSpace(IpRiskTargetAddress) ? null : IpRiskTargetAddress.Trim();
        var result = await _exitIpRiskReviewService.RunAsync(targetAddress, progress);
        UpdateGlobalTaskProgress("汇总中", 94d);
        ApplyIpRiskReviewResult(result);
        StatusMessage = result.Summary;
        AppendHistory(
            "IP风险",
            string.IsNullOrWhiteSpace(targetAddress) ? "当前出口 IP 风险复核" : $"指定 IP 风险复核：{targetAddress}",
            $"{IpRiskOverviewSummary}\n\n{IpRiskAssessmentSummary}");
    }

    private void ApplyIpRiskReviewResult(ExitIpRiskReviewResult result)
    {
        IpRiskSourceResults.Clear();
        foreach (var source in result.Sources)
        {
            IpRiskSourceResults.Add(source);
        }

        var successfulSources = result.Sources.Count(source => source.Succeeded);
        var riskCount = result.Sources.Count(source => string.Equals(source.Verdict, "高风险", StringComparison.Ordinal));
        var warnCount = result.Sources.Count(source => string.Equals(source.Verdict, "注意", StringComparison.Ordinal));
        var passCount = result.Sources.Count(source => string.Equals(source.Verdict, "通过", StringComparison.Ordinal));

        var subjectLabel = IsSpecifiedIpRiskResult(result) ? "目标 IP" : "当前出口 IP";
        IpRiskOverviewSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"{subjectLabel}：{result.PublicIp ?? "--"}\n" +
            $"识别来源：{result.DetectSource}\n" +
            $"地区：{JoinText(" / ", result.Country, result.City) ?? "--"}\n" +
            $"网络：{JoinText(" / ", result.Asn, result.Organization) ?? "--"}\n" +
            $"Cloudflare 节点：{result.CloudflareColo ?? "--"}\n" +
            $"成功源：{successfulSources}/{result.Sources.Count}\n" +
            $"综合结论：{result.Verdict}\n" +
            $"摘要：{result.Summary}\n" +
            $"错误：{result.Error ?? "无"}";

        StringBuilder assessmentBuilder = new();
        assessmentBuilder.AppendLine($"综合结论：{result.Verdict}");
        assessmentBuilder.AppendLine($"高风险命中：{riskCount}");
        assessmentBuilder.AppendLine($"注意命中：{warnCount}");
        assessmentBuilder.AppendLine($"通过命中：{passCount}");
        assessmentBuilder.AppendLine();
        assessmentBuilder.AppendLine("风险信号：");
        if (result.RiskSignals.Count == 0)
        {
            assessmentBuilder.AppendLine("- 暂未发现明确风险信号。");
        }
        else
        {
            foreach (var signal in result.RiskSignals)
            {
                assessmentBuilder.AppendLine($"- {signal}");
            }
        }

        assessmentBuilder.AppendLine();
        assessmentBuilder.AppendLine("正向信号：");
        if (result.PositiveSignals.Count == 0)
        {
            assessmentBuilder.AppendLine("- 暂无。");
        }
        else
        {
            foreach (var signal in result.PositiveSignals)
            {
                assessmentBuilder.AppendLine($"- {signal}");
            }
        }

        assessmentBuilder.AppendLine();
        assessmentBuilder.AppendLine("建议：");
        assessmentBuilder.AppendLine(BuildIpRiskRecommendation(result));
        IpRiskAssessmentSummary = assessmentBuilder.ToString().TrimEnd();

        IpRiskSourceDetailSummary = result.Sources.Count == 0
            ? "尚无多源复核明细。"
            : string.Join(
                "\n\n",
                result.Sources.Select(source =>
                    $"[{source.DisplayName}] {source.Verdict}\n" +
                    $"类别：{source.Category}\n" +
                    $"摘要：{source.Summary}\n" +
                    $"详情：{source.Detail}\n" +
                    $"错误：{source.Error ?? "无"}"));

        IpRiskSummaryMetaText = BuildIpRiskSummaryMetaText(result);
        ReplaceCollection(IpRiskSummaryBadges, BuildIpRiskSummaryBadges(result, successfulSources));
        ReplaceCollection(IpRiskIndicatorCards, BuildIpRiskIndicatorCards(result));
        ReplaceCollection(
            IpRiskSourceRows,
            result.Sources.Count == 0
                ? [CreatePlaceholderSourceRow()]
                : result.Sources.Select(BuildIpRiskSourceRow));

        AppendModuleOutput("IP 风险复核返回", IpRiskOverviewSummary, IpRiskAssessmentSummary, IpRiskSourceDetailSummary);
    }

    private void ResetIpRiskPresentation()
    {
        IpRiskOverviewSummary = "运行后显示出口 IP、地区和 ASN。";
        IpRiskAssessmentSummary = "暂无风险结论。";
        IpRiskSourceDetailSummary = "暂无来源明细。";
        IpRiskSummaryMetaText = "运行后显示检测时间和识别来源。";

        ReplaceCollection(
            IpRiskSummaryBadges,
            [
                new IpRiskSummaryBadgeViewModel("出口 IP", "未检测", "等待识别当前出口", IpRiskToneViewModel.Info),
                new IpRiskSummaryBadgeViewModel("地区", "未检测", "等待返回地区信息", IpRiskToneViewModel.Neutral),
                new IpRiskSummaryBadgeViewModel("网络", "未检测", "等待返回 ASN 与组织", IpRiskToneViewModel.Neutral),
                new IpRiskSummaryBadgeViewModel("成功源", "0/0", "尚未查询来源", IpRiskToneViewModel.Neutral),
                new IpRiskSummaryBadgeViewModel("综合结论", "待检测", "运行后给出结论", IpRiskToneViewModel.Warning)
            ]);

        ReplaceCollection(
            IpRiskIndicatorCards,
            [
                new IpRiskIndicatorCardViewModel("机房 / 住宅", "等待检测", "统计是否被标记为机房", IpRiskToneViewModel.Neutral),
                new IpRiskIndicatorCardViewModel("代理", "等待检测", "统计是否被标记为代理", IpRiskToneViewModel.Neutral),
                new IpRiskIndicatorCardViewModel("VPN", "等待检测", "统计是否被标记为 VPN", IpRiskToneViewModel.Neutral),
                new IpRiskIndicatorCardViewModel("Tor", "等待检测", "统计是否命中 Tor", IpRiskToneViewModel.Neutral),
                new IpRiskIndicatorCardViewModel("滥用", "等待检测", "统计是否命中滥用情报", IpRiskToneViewModel.Neutral),
                new IpRiskIndicatorCardViewModel("综合风险", "待检测", "根据风险和正向信号归类", IpRiskToneViewModel.Warning)
            ]);

        ReplaceCollection(IpRiskSourceRows, [CreatePlaceholderSourceRow()]);
    }

    private static string BuildIpRiskRecommendation(ExitIpRiskReviewResult result)
    {
        return result.Verdict switch
        {
            "高风险" => "当前出口更像高风险节点。若要做注册、养号、长期会话或风控敏感业务，建议直接换出口。",
            "需复核" => "当前出口至少有一类风险信号，适合继续结合目标站点实测、分流复核和账号侧行为一起判断。",
            "较干净" => "当前出口没有明显风险标记，后续可继续做目标接口或站点侧实测验证。",
            _ => "当前可用源不足，建议稍后重试，或继续结合 IP 与分流、NAT / STUN 一起复核。"
        };
    }

    private static string BuildIpRiskSummaryMetaText(ExitIpRiskReviewResult result)
    {
        List<string> parts =
        [
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}",
            $"识别来源：{result.DetectSource}"
        ];

        if (!string.IsNullOrWhiteSpace(result.CloudflareColo))
        {
            parts.Add($"Cloudflare 节点：{result.CloudflareColo}");
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            parts.Add($"错误：{result.Error}");
        }

        return string.Join("  ·  ", parts);
    }

    private static IReadOnlyList<IpRiskSummaryBadgeViewModel> BuildIpRiskSummaryBadges(ExitIpRiskReviewResult result, int successfulSources)
    {
        var subjectLabel = IsSpecifiedIpRiskResult(result) ? "目标 IP" : "出口 IP";
        var locationText = JoinText(" / ", result.Country, result.City) ?? "--";
        var networkText = JoinText(" / ", result.Asn, result.Organization) ?? "--";
        var totalSources = result.Sources.Count;
        var coverageText = totalSources == 0
            ? "0/0"
            : $"{successfulSources}/{totalSources}";

        var networkDetail = string.IsNullOrWhiteSpace(result.CloudflareColo)
            ? "未返回 Cloudflare 边缘节点。"
            : $"Cloudflare 节点：{result.CloudflareColo}";

        var coverageDetail = totalSources == 0
            ? "尚未发起来源查询。"
            : successfulSources == totalSources
                ? "全部来源成功返回。"
                : $"仍有 {totalSources - successfulSources} 个来源失败或未返回。";

        return
        [
            new IpRiskSummaryBadgeViewModel(
                subjectLabel,
                FormatOptional(result.PublicIp, "未识别"),
                $"来源：{result.DetectSource}",
                string.IsNullOrWhiteSpace(result.PublicIp) ? IpRiskToneViewModel.Warning : IpRiskToneViewModel.Info),
            new IpRiskSummaryBadgeViewModel(
                "地区",
                locationText,
                string.Equals(locationText, "--", StringComparison.Ordinal) ? "当前结果未返回地区信息。" : "按多源结果汇总地区和城市。",
                string.Equals(locationText, "--", StringComparison.Ordinal) ? IpRiskToneViewModel.Neutral : IpRiskToneViewModel.Success),
            new IpRiskSummaryBadgeViewModel(
                "网络",
                networkText,
                string.Equals(networkText, "--", StringComparison.Ordinal) ? "当前结果未返回 ASN / 组织。" : networkDetail,
                string.Equals(networkText, "--", StringComparison.Ordinal) ? IpRiskToneViewModel.Neutral : IpRiskToneViewModel.Info),
            new IpRiskSummaryBadgeViewModel(
                "成功源",
                coverageText,
                coverageDetail,
                GetSourceCoverageTone(successfulSources, totalSources)),
            new IpRiskSummaryBadgeViewModel(
                "综合结论",
                result.Verdict,
                result.Error ?? result.Summary,
                GetVerdictTone(result.Verdict))
        ];
    }

    private static IReadOnlyList<IpRiskIndicatorCardViewModel> BuildIpRiskIndicatorCards(ExitIpRiskReviewResult result)
    {
        var riskSources = result.Sources
            .Where(source => !string.Equals(source.Key, "current-origin", StringComparison.Ordinal))
            .ToArray();

        return
        [
            BuildBooleanIndicator("机房 / 住宅", riskSources, source => source.IsDatacenter, "机房 / Hosting", "住宅倾向", "标记为机房 / hosting", IpRiskToneViewModel.Warning),
            BuildBooleanIndicator("代理", riskSources, source => source.IsProxy, "发现代理", "未发现", "识别为代理", IpRiskToneViewModel.Warning),
            BuildBooleanIndicator("VPN", riskSources, source => source.IsVpn, "发现 VPN", "未发现", "识别为 VPN", IpRiskToneViewModel.Warning),
            BuildBooleanIndicator("Tor", riskSources, source => source.IsTor, "命中 Tor", "未发现", "命中 Tor", IpRiskToneViewModel.Danger),
            BuildBooleanIndicator("滥用", riskSources, source => source.IsAbuse, "存在滥用", "未发现", "命中滥用 / 威胁情报", IpRiskToneViewModel.Danger),
            BuildOverallIndicator(result, riskSources)
        ];
    }

    private static IpRiskIndicatorCardViewModel BuildBooleanIndicator(
        string title,
        IReadOnlyList<ExitIpRiskSourceResult> sources,
        Func<ExitIpRiskSourceResult, bool?> selector,
        string positiveStatus,
        string negativeStatus,
        string metricLabel,
        IpRiskToneViewModel positiveTone)
    {
        var stats = ComputeBooleanSignalStats(sources.Select(selector));
        if (stats.AvailableCount == 0)
        {
            return new IpRiskIndicatorCardViewModel(
                title,
                "未提供",
                $"当前可用来源没有返回“{metricLabel}”字段。",
                IpRiskToneViewModel.Neutral);
        }

        if (stats.PositiveCount > 0)
        {
            return new IpRiskIndicatorCardViewModel(
                title,
                positiveStatus,
                $"{stats.PositiveCount}/{stats.AvailableCount} 源{metricLabel}。",
                positiveTone);
        }

        return new IpRiskIndicatorCardViewModel(
            title,
            negativeStatus,
            $"{stats.NegativeCount}/{stats.AvailableCount} 源未{metricLabel}。",
            IpRiskToneViewModel.Success);
    }

    private static IpRiskIndicatorCardViewModel BuildOverallIndicator(
        ExitIpRiskReviewResult result,
        IReadOnlyList<ExitIpRiskSourceResult> riskSources)
    {
        var successfulRiskSources = riskSources.Count(source => source.Succeeded);
        var detail = riskSources.Count == 0
            ? "当前只有出口识别结果，尚未拿到可用于判断风险的来源。"
            : $"风险信号 {result.RiskSignals.Count} 个，正向信号 {result.PositiveSignals.Count} 个；风险源成功 {successfulRiskSources}/{riskSources.Count}。";

        return new IpRiskIndicatorCardViewModel(
            "综合风险",
            result.Verdict,
            result.Error ?? detail,
            GetVerdictTone(result.Verdict));
    }

    private static IpRiskSourceRowViewModel BuildIpRiskSourceRow(ExitIpRiskSourceResult source)
    {
        return new IpRiskSourceRowViewModel(
            source.DisplayName,
            BuildSourceMetaText(source),
            string.IsNullOrWhiteSpace(source.Summary) ? source.Error ?? "--" : source.Summary,
            source.Verdict,
            GetVerdictTone(source.Verdict),
            FormatDatacenterText(source.IsDatacenter),
            GetRiskFlagTone(source.IsDatacenter),
            FormatRiskFlagText(source.IsProxy),
            GetRiskFlagTone(source.IsProxy),
            FormatRiskFlagText(source.IsVpn),
            GetRiskFlagTone(source.IsVpn),
            FormatRiskFlagText(source.IsTor),
            GetRiskFlagTone(source.IsTor, highSeverity: true),
            FormatRiskFlagText(source.IsAbuse),
            GetRiskFlagTone(source.IsAbuse, highSeverity: true),
            FormatRiskScore(source.RiskScore),
            GetRiskScoreTone(source.RiskScore));
    }

    private static IpRiskSourceRowViewModel CreatePlaceholderSourceRow()
    {
        return new IpRiskSourceRowViewModel(
            "等待检测",
            "来源矩阵占位",
            "运行一次 IP 风险复核后，这里会按来源拆开显示机房、代理、VPN、Tor、滥用和风险分。",
            "未开始",
            IpRiskToneViewModel.Neutral,
            "—",
            IpRiskToneViewModel.Neutral,
            "—",
            IpRiskToneViewModel.Neutral,
            "—",
            IpRiskToneViewModel.Neutral,
            "—",
            IpRiskToneViewModel.Neutral,
            "—",
            IpRiskToneViewModel.Neutral,
            "—",
            IpRiskToneViewModel.Neutral);
    }

    private static string BuildSourceMetaText(ExitIpRiskSourceResult source)
    {
        List<string> parts = [source.Category];
        var locationText = JoinText(" / ", source.Country, source.City);
        if (!string.IsNullOrWhiteSpace(locationText))
        {
            parts.Add(locationText);
        }

        if (!string.IsNullOrWhiteSpace(source.Asn))
        {
            parts.Add(source.Asn);
        }

        return string.Join(" · ", parts);
    }

    private static BooleanSignalStats ComputeBooleanSignalStats(IEnumerable<bool?> values)
    {
        var normalized = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();

        return new BooleanSignalStats(
            normalized.Length,
            normalized.Count(static value => value),
            normalized.Count(static value => !value));
    }

    private static IpRiskToneViewModel GetVerdictTone(string? verdict)
    {
        return verdict switch
        {
            "较干净" or "通过" => IpRiskToneViewModel.Success,
            "需复核" or "注意" => IpRiskToneViewModel.Warning,
            "高风险" or "失败" => IpRiskToneViewModel.Danger,
            "信息" => IpRiskToneViewModel.Info,
            _ => IpRiskToneViewModel.Neutral
        };
    }

    private static IpRiskToneViewModel GetSourceCoverageTone(int successfulSources, int totalSources)
    {
        if (totalSources <= 0)
        {
            return IpRiskToneViewModel.Neutral;
        }

        if (successfulSources <= 0)
        {
            return IpRiskToneViewModel.Danger;
        }

        return successfulSources == totalSources
            ? IpRiskToneViewModel.Success
            : IpRiskToneViewModel.Warning;
    }

    private static IpRiskToneViewModel GetRiskFlagTone(bool? value, bool highSeverity = false)
    {
        return value switch
        {
            true when highSeverity => IpRiskToneViewModel.Danger,
            true => IpRiskToneViewModel.Warning,
            false => IpRiskToneViewModel.Success,
            _ => IpRiskToneViewModel.Neutral
        };
    }

    private static IpRiskToneViewModel GetRiskScoreTone(double? riskScore)
    {
        if (!riskScore.HasValue)
        {
            return IpRiskToneViewModel.Neutral;
        }

        if (riskScore.Value >= 70d)
        {
            return IpRiskToneViewModel.Danger;
        }

        if (riskScore.Value >= 35d)
        {
            return IpRiskToneViewModel.Warning;
        }

        return IpRiskToneViewModel.Success;
    }

    private static string FormatRiskFlagText(bool? value)
    {
        return value switch
        {
            true => "是",
            false => "否",
            _ => "—"
        };
    }

    private static string FormatDatacenterText(bool? value)
    {
        return value switch
        {
            true => "机房",
            false => "住宅",
            _ => "—"
        };
    }

    private static string FormatRiskScore(double? riskScore)
        => riskScore.HasValue ? riskScore.Value.ToString("F0") : "—";

    private static string FormatOptional(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static bool IsSpecifiedIpRiskResult(ExitIpRiskReviewResult result)
        => string.Equals(result.DetectSource, "用户指定 IP", StringComparison.Ordinal) ||
           string.Equals(result.DetectSource, "用户输入", StringComparison.Ordinal);

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static string? JoinText(string separator, params string?[] values)
    {
        var normalized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return normalized.Length == 0
            ? null
            : string.Join(separator, normalized);
    }

    private sealed record BooleanSignalStats(int AvailableCount, int PositiveCount, int NegativeCount);
}
