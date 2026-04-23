using System.Text;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly GeoIpLookupService _geoIpLookupService = new();
    private string _splitRoutingHostsText = "chatgpt.com\r\napi.openai.com\r\ngithub.com\r\ncloudflare.com\r\nspeed.cloudflare.com";
    private string _splitRoutingSummary = "运行 IP 与分流检测后，这里会显示网卡、公网出口、DNS 对比与 HTTPS 可达性。";
    private string _splitRoutingIpInsightSummary = "尚无公网 IP 归属洞察。";
    private string _splitRoutingAdapterSummary = "尚无网卡清单。";
    private string _splitRoutingExitSummary = "尚无多出口对比结果。";
    private string _splitRoutingDnsSummary = "尚无 DNS 对比结果。";
    private string _splitRoutingReachabilitySummary = "尚无 HTTPS 可达性结果。";

    public string SplitRoutingHostsText
    {
        get => _splitRoutingHostsText;
        set => SetProperty(ref _splitRoutingHostsText, value);
    }

    public string SplitRoutingSummary
    {
        get => _splitRoutingSummary;
        private set => SetProperty(ref _splitRoutingSummary, value);
    }

    public string SplitRoutingIpInsightSummary
    {
        get => _splitRoutingIpInsightSummary;
        private set => SetProperty(ref _splitRoutingIpInsightSummary, value);
    }

    public string SplitRoutingAdapterSummary
    {
        get => _splitRoutingAdapterSummary;
        private set => SetProperty(ref _splitRoutingAdapterSummary, value);
    }

    public string SplitRoutingExitSummary
    {
        get => _splitRoutingExitSummary;
        private set => SetProperty(ref _splitRoutingExitSummary, value);
    }

    public string SplitRoutingDnsSummary
    {
        get => _splitRoutingDnsSummary;
        private set => SetProperty(ref _splitRoutingDnsSummary, value);
    }

    public string SplitRoutingReachabilitySummary
    {
        get => _splitRoutingReachabilitySummary;
        private set => SetProperty(ref _splitRoutingReachabilitySummary, value);
    }

    private Task RunSplitRoutingAsync()
        => ExecuteBusyActionAsync("正在运行 IP 与分流诊断...", RunSplitRoutingCoreAsync);

    private async Task RunSplitRoutingCoreAsync()
    {
        UpdateGlobalTaskProgress("\u51C6\u5907\u4E2D", 10d);
        IProgress<string> progress = new Progress<string>(message =>
        {
            StatusMessage = message;
            UpdateGlobalTaskProgressForSplitRoutingMessage(message);
        });
        var result = await _splitRoutingDiagnosticsService.RunAsync([SplitRoutingHostsText], progress);
        var publicIpCandidates = result.ExitChecks
            .Select(check => check.PublicIp)
            .Concat(result.ReachabilityChecks.Select(check => check.ResolvedAddress))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();

        progress.Report("正在补充公网 IP 归属、ASN 和网络角色信息...");
        var ipInsights = await _geoIpLookupService.LookupAddressesAsync(publicIpCandidates, progress);
        UpdateGlobalTaskProgress("\u6C47\u603B\u4E2D", 94d);
        ApplySplitRoutingResult(result, ipInsights);

        DashboardCards[7].Status = result.Error is not null
            ? "需复核"
            : result.MultiExitSuspected || result.DnsSplitSuspected || result.ReachabilityIssuesDetected
                ? "需复核"
                : "完成";
        DashboardCards[7].Detail = ipInsights.Count > 0
            ? $"{result.Summary} 已补充 {ipInsights.Count} 个 ASN / IP 归属洞察。"
            : result.Summary;
        StatusMessage = result.Summary;
        AppendHistory("分流", "IP 与分流诊断", SplitRoutingSummary);
    }

    private void LoadSplitRoutingState(AppStateSnapshot snapshot)
    {
        SplitRoutingHostsText = string.IsNullOrWhiteSpace(snapshot.SplitRoutingHostsText)
            ? "chatgpt.com\r\napi.openai.com\r\ngithub.com\r\ncloudflare.com\r\nspeed.cloudflare.com"
            : snapshot.SplitRoutingHostsText
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private void ApplySplitRoutingStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.SplitRoutingHostsText = SplitRoutingHostsText;
    }

    private void ApplySplitRoutingResult(SplitRoutingDiagnosticsResult result, IReadOnlyList<GeoIpInsightResult> ipInsights)
    {
        var insightsByAddress = ipInsights.ToDictionary(insight => insight.Address, StringComparer.OrdinalIgnoreCase);
        var distinctAsnCount = ipInsights
            .Where(insight => insight.Asn is not null)
            .Select(insight => insight.Asn!.Value)
            .Distinct()
            .Count();

        SplitRoutingSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"请求域名：{(result.RequestedHosts.Count == 0 ? "（无）" : string.Join(", ", result.RequestedHosts))}\n" +
            $"活动网卡数：{result.Adapters.Count}\n" +
            $"成功出口检查：{result.ExitChecks.Count(check => check.Succeeded)}/{result.ExitChecks.Count}\n" +
            $"公网 IP 洞察数：{ipInsights.Count}\n" +
            $"不同 ASN 数：{distinctAsnCount}\n" +
            $"疑似多出口：{(result.MultiExitSuspected ? "是" : "否")}\n" +
            $"疑似 DNS 分流：{(result.DnsSplitSuspected ? "是" : "否")}\n" +
            $"可达性异常：{(result.ReachabilityIssuesDetected ? "是" : "否")}\n" +
            $"摘要：{result.Summary}\n" +
            $"错误：{result.Error ?? "无"}";

        SplitRoutingIpInsightSummary = ipInsights.Count == 0
            ? "未采集到公网 IP 归属洞察。"
            : string.Join(
                "\n\n",
                ipInsights.Select(insight =>
                    $"{insight.Address}\n" +
                    $"位置：{insight.LocationLabel}\n" +
                    $"网络归属：{insight.NetworkLabel}\n" +
                    $"洲别：{insight.Continent ?? "--"} / {insight.ContinentCode ?? "--"}\n" +
                    $"坐标：{insight.Latitude:F4}, {insight.Longitude:F4}"));

        SplitRoutingAdapterSummary = result.Adapters.Count == 0
            ? "未采集到活动网卡视图。"
            : string.Join(
                "\n\n",
                result.Adapters.Select(adapter =>
                    $"{adapter.Name} [{adapter.NetworkType}]\n" +
                    $"描述：{adapter.Description}\n" +
                    $"本地 IP：{FormatAddressList(adapter.LocalAddresses)}\n" +
                    $"DNS 服务器：{FormatAddressList(adapter.DnsServers)}"));

        SplitRoutingExitSummary = result.ExitChecks.Count == 0
            ? "未采集到出口检查结果。"
            : string.Join(
                "\n\n",
                result.ExitChecks.Select(check =>
                {
                    insightsByAddress.TryGetValue(check.PublicIp ?? string.Empty, out var insight);
                    return
                    $"{check.Name}\n" +
                    $"端点：{check.Endpoint}\n" +
                    $"是否成功：{(check.Succeeded ? "是" : "否")}\n" +
                    $"公网 IP：{check.PublicIp ?? "--"}\n" +
                    $"地区代码 loc：{check.LocationCode ?? "--"}\n" +
                    $"Cloudflare 节点：{check.CloudflareColo ?? "--"}\n" +
                    $"城市 / 国家：{FormatLocation(check.City, check.Country)}\n" +
                    $"地理 / 归属：{FormatInsightLine(insight)}\n" +
                    $"延迟：{FormatLatency(check.Latency)}\n" +
                    $"摘要：{check.Summary}\n" +
                    $"错误：{check.Error ?? "无"}";
                }));

        StringBuilder dnsBuilder = new();
        if (result.DnsViews.Count == 0)
        {
            dnsBuilder.Append("未采集到 DNS 对比结果。");
        }
        else
        {
            foreach (var view in result.DnsViews)
            {
                dnsBuilder.AppendLine(view.Host);
                dnsBuilder.AppendLine($"系统 DNS：{FormatAddressList(view.SystemAddresses)} ({FormatLatency(view.SystemLatency)})");
                dnsBuilder.AppendLine($"Cloudflare DoH 解析：{FormatAddressList(view.CloudflareAddresses)} ({FormatLatency(view.CloudflareLatency)})");
                dnsBuilder.AppendLine($"Google DoH 解析：{FormatAddressList(view.GoogleAddresses)} ({FormatLatency(view.GoogleLatency)})");
                dnsBuilder.AppendLine($"对比结果：{view.ComparisonSummary}");
                dnsBuilder.AppendLine($"错误：{view.Error ?? "无"}");
                dnsBuilder.AppendLine();
            }
        }

        SplitRoutingDnsSummary = dnsBuilder.ToString().TrimEnd();

        SplitRoutingReachabilitySummary = result.ReachabilityChecks.Count == 0
            ? "未采集到 HTTPS 可达性结果。"
            : string.Join(
                "\n\n",
                result.ReachabilityChecks.Select(check =>
                {
                    insightsByAddress.TryGetValue(check.ResolvedAddress ?? string.Empty, out var insight);
                    return
                    $"{check.Host}\n" +
                    $"URL：{check.Url}\n" +
                    $"是否成功：{(check.Succeeded ? "是" : "否")}\n" +
                    $"状态码：{check.StatusCode?.ToString() ?? "--"}\n" +
                    $"解析 IP：{check.ResolvedAddress ?? "--"}\n" +
                    $"地理 / 归属：{FormatInsightLine(insight)}\n" +
                    $"延迟：{FormatLatency(check.Latency)}\n" +
                    $"摘要：{check.Summary}\n" +
                    $"错误：{check.Error ?? "无"}";
                }));

        AppendModuleOutput("IP 与分流返回", SplitRoutingSummary, SplitRoutingExitSummary, SplitRoutingReachabilitySummary);
        SaveState();
    }

    private static string FormatAddressList(IReadOnlyList<string> addresses)
        => addresses.Count == 0 ? "（无）" : string.Join(", ", addresses);

    private static string FormatLatency(TimeSpan? latency)
        => latency is null ? "--" : $"{latency.Value.TotalMilliseconds:F0} ms";

    private static string FormatLocation(string? city, string? country)
    {
        if (string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(country))
        {
            return "--";
        }

        if (string.IsNullOrWhiteSpace(city))
        {
            return country!;
        }

        if (string.IsNullOrWhiteSpace(country))
        {
            return city;
        }

        return $"{city} / {country}";
    }

    private static string FormatInsightLine(GeoIpInsightResult? insight)
    {
        if (insight is null)
        {
            return "--";
        }

        return $"{insight.LocationLabel} | {insight.NetworkLabel}";
    }
}
