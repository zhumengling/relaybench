using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using NetTest.Core.Models;
using NetTest.Core.Support;

namespace NetTest.Core.Services;

public sealed partial class SplitRoutingDiagnosticsService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly IReadOnlyList<string> DefaultHosts =
    [
        "chatgpt.com",
        "api.openai.com",
        "github.com",
        "cloudflare.com",
        "speed.cloudflare.com"
    ];

    public IReadOnlyList<string> GetDefaultHosts() => DefaultHosts;

    public async Task<SplitRoutingDiagnosticsResult> RunAsync(
        IEnumerable<string>? hosts,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedHosts = NormalizeHosts(hosts);
        var adapters = GetAdapters();

        progress?.Report("正在检查网页 API、Cloudflare 与测速端点的公网出口视角...");
        List<SplitRoutingExitCheck> exitChecks =
        [
            await RunTraceExitCheckAsync("网页 API Trace 出口", "https://chatgpt.com/cdn-cgi/trace", cancellationToken),
            await RunTraceExitCheckAsync("Cloudflare Trace 出口", "https://www.cloudflare.com/cdn-cgi/trace", cancellationToken),
            await RunSpeedExitCheckAsync(cancellationToken)
        ];

        progress?.Report("正在对比系统 DNS 与公共 DoH 解析结果...");
        List<SplitRoutingDnsView> dnsViews = new(normalizedHosts.Count);
        foreach (var host in normalizedHosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dnsViews.Add(await RunDnsViewAsync(host, cancellationToken));
        }

        progress?.Report("正在检查所选域名的 HTTPS 可达性...");
        List<SplitRoutingReachabilityCheck> reachabilityChecks = new(normalizedHosts.Count);
        foreach (var host in normalizedHosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reachabilityChecks.Add(await RunReachabilityCheckAsync(host, cancellationToken));
        }

        var distinctPublicIps = exitChecks
            .Where(check => check.Succeeded && !string.IsNullOrWhiteSpace(check.PublicIp))
            .Select(check => check.PublicIp!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var distinctColos = exitChecks
            .Where(check => check.Succeeded && !string.IsNullOrWhiteSpace(check.CloudflareColo))
            .Select(check => check.CloudflareColo!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var multiExitSuspected = distinctPublicIps > 1 || distinctColos > 1;
        var dnsSplitSuspected = dnsViews.Any(view => IndicatesDnsSplit(view));
        var reachabilityIssuesDetected = reachabilityChecks.Any(check => !check.Succeeded);
        var error = exitChecks.All(check => !check.Succeeded) &&
                    dnsViews.All(view => !view.SystemAddresses.Any() && !view.CloudflareAddresses.Any() && !view.GoogleAddresses.Any())
            ? "所有外部分流检查均失败。"
            : null;

        var summary =
            $"检测域名数 {normalizedHosts.Count}。 " +
            $"成功出口检查 {exitChecks.Count(check => check.Succeeded)}/{exitChecks.Count}。 " +
            $"不同公网 IP 数 {distinctPublicIps}，不同 Cloudflare 节点数 {distinctColos}。 " +
            $"存在 DNS 分歧的域名 {dnsViews.Count(IndicatesDnsSplit)}/{dnsViews.Count}。 " +
            $"存在可达性问题的域名 {reachabilityChecks.Count(check => !check.Succeeded)}/{reachabilityChecks.Count}。 " +
            $"多出口判断：{(multiExitSuspected ? "疑似存在" : "暂不明显")}；DNS 分流判断：{(dnsSplitSuspected ? "疑似存在" : "暂不明显")}。";

        return new SplitRoutingDiagnosticsResult(
            DateTimeOffset.Now,
            normalizedHosts,
            adapters,
            exitChecks,
            dnsViews,
            reachabilityChecks,
            multiExitSuspected,
            dnsSplitSuspected,
            reachabilityIssuesDetected,
            summary,
            error);
    }
}
