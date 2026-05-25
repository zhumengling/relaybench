using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ExitIpRiskReviewService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private const string IpInfoTokenEnvironmentVariable = "IPINFO_TOKEN";
    private const string RelayBenchIpInfoTokenEnvironmentVariable = "RELAYBENCH_IPINFO_TOKEN";

    public async Task<ExitIpRiskReviewResult> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => await RunAsync(null, progress, cancellationToken);

    public async Task<ExitIpRiskReviewResult> RunAsync(
        string? targetIp,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeTargetIp(targetIp, out var normalizedTargetIp, out var targetError))
        {
            return BuildInvalidTargetResult(targetIp, targetError);
        }

        var hasTargetIp = !string.IsNullOrWhiteSpace(normalizedTargetIp);
        progress?.Report(hasTargetIp
            ? $"正在准备复核指定 IP：{normalizedTargetIp}..."
            : "正在识别当前出口 IP...");
        var origin = hasTargetIp
            ? ResolveSpecifiedOrigin(normalizedTargetIp!)
            : await ResolveCurrentOriginAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(origin.PublicIp))
        {
            return new ExitIpRiskReviewResult(
                DateTimeOffset.Now,
                null,
                origin.DetectSource,
                null,
                null,
                null,
                null,
                null,
                origin.SourceResult is null ? Array.Empty<ExitIpRiskSourceResult>() : [origin.SourceResult],
                Array.Empty<string>(),
                Array.Empty<string>(),
                "待复核",
                "未能识别当前公网出口 IP，暂时无法继续进行 IP 风险复核。",
                origin.Error ?? "当前公网出口 IP 识别失败。");
        }

        List<ExitIpRiskSourceResult> sources = [];
        if (origin.SourceResult is not null)
        {
            sources.Add(origin.SourceResult);
        }

        var probes = new (string Name, Func<string, CancellationToken, Task<ExitIpRiskSourceResult>> Handler)[]
        {
            ("ipapi.is", ProbeIpApiIsAsync),
            ("proxycheck.io", ProbeProxyCheckAsync),
            ("ip-api.com", ProbeIpApiComAsync),
            ("ipwho.is", ProbeIpWhoIsAsync),
            ("ipinfo.io", ProbeIpInfoAsync),
            ("country.is", ProbeCountryIsAsync),
            ("IP2Location.io", ProbeIp2LocationAsync),
            ("GreyNoise Community", ProbeGreyNoiseCommunityAsync),
            ("Spamhaus DROP", ProbeSpamhausDropAsync),
            ("Spamhaus ASN-DROP", (_, ct) => ProbeSpamhausAsnDropAsync(origin.Asn, ct)),
            ("AlienVault OTX", ProbeAlienVaultOtxAsync),
            ("Shodan InternetDB", ProbeShodanInternetDbAsync),
            ("abuse.ch Feodo Tracker", ProbeFeodoTrackerAsync),
            ("Tor Project", ProbeTorProjectAsync)
        };

        for (var index = 0; index < probes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在查询 IP 风险源 {index + 1}/{probes.Length}：{probes[index].Name}");
            sources.Add(await probes[index].Handler(origin.PublicIp, cancellationToken));
        }

        progress?.Report("正在汇总 IP 风险复核结果...");
        return BuildResult(origin, sources);
    }

    private static ExitIpRiskReviewResult BuildResult(ResolvedOrigin origin, IReadOnlyList<ExitIpRiskSourceResult> sources)
    {
        var successfulSources = sources.Count(source => source.Succeeded);
        var successfulRiskSources = sources.Count(source => source.Succeeded && IsRiskProbeSource(source));

        var country = FirstNonEmpty(origin.Country, sources.Select(source => source.Country).ToArray());
        var city = FirstNonEmpty(origin.City, sources.Select(source => source.City).ToArray());
        var asn = FirstNonEmpty(origin.Asn, sources.Select(source => source.Asn).ToArray());
        var organization = FirstNonEmpty(origin.Organization, sources.Select(source => source.Organization).ToArray());

        var datacenterDetected = sources.Any(source => source.IsDatacenter == true);
        var proxyDetected = sources.Any(source => source.IsProxy == true);
        var vpnDetected = sources.Any(source => source.IsVpn == true);
        var torDetected = sources.Any(source => source.IsTor == true);
        var abuseDetected = sources.Any(source => source.IsAbuse == true);
        var maxRiskScore = sources
            .Where(source => source.RiskScore.HasValue)
            .Select(source => source.RiskScore!.Value)
            .DefaultIfEmpty(0d)
            .Max();

        List<string> riskSignals = [];
        List<string> positiveSignals = [];

        if (torDetected)
        {
            riskSignals.Add("至少一个来源将当前出口识别为 Tor 出口。");
        }

        if (abuseDetected)
        {
            riskSignals.Add("至少一个来源标记存在滥用或威胁情报风险。");
        }

        if (vpnDetected)
        {
            riskSignals.Add("至少一个来源将当前出口识别为 VPN。");
        }

        if (proxyDetected)
        {
            riskSignals.Add("至少一个来源将当前出口识别为代理。");
        }

        if (datacenterDetected)
        {
            riskSignals.Add("至少一个来源将当前出口识别为机房 / hosting 网络。");
        }

        if (maxRiskScore >= 70d)
        {
            riskSignals.Add($"风险分最高达到 {maxRiskScore.ToString("F0", CultureInfo.InvariantCulture)}。");
        }
        else if (maxRiskScore >= 35d)
        {
            riskSignals.Add($"风险分达到中等区间：{maxRiskScore.ToString("F0", CultureInfo.InvariantCulture)}。");
        }

        if (!datacenterDetected)
        {
            positiveSignals.Add("未发现明确的机房 / hosting 标记。");
        }

        if (!proxyDetected)
        {
            positiveSignals.Add("未发现明确的代理标记。");
        }

        if (!vpnDetected)
        {
            positiveSignals.Add("未发现明确的 VPN 标记。");
        }

        if (!torDetected)
        {
            positiveSignals.Add("未发现 Tor 出口标记。");
        }

        if (!abuseDetected)
        {
            positiveSignals.Add("未发现明确的滥用 / 威胁情报命中。");
        }

        string verdict;
        if (successfulRiskSources == 0)
        {
            verdict = "待复核";
            riskSignals.Add("风险源全部失败，当前只能拿到基础出口信息。");
        }
        else if (torDetected || abuseDetected || maxRiskScore >= 70d)
        {
            verdict = "高风险";
        }
        else if (proxyDetected || vpnDetected || datacenterDetected || maxRiskScore >= 35d)
        {
            verdict = "需复核";
        }
        else
        {
            verdict = "较干净";
        }

        var locationText = JoinNonEmpty(" / ", country, city);
        var networkText = JoinNonEmpty(" / ", asn, organization);
        var coloText = string.IsNullOrWhiteSpace(origin.CloudflareColo)
            ? null
            : $"边缘节点 {origin.CloudflareColo}";

        var subjectLabel = IsSpecifiedOrigin(origin) ? "目标 IP" : "当前出口";
        var summary =
            $"{subjectLabel} {origin.PublicIp}，来源 {origin.DetectSource}，成功查询 {successfulSources}/{sources.Count} 个源。" +
            $"{(string.IsNullOrWhiteSpace(locationText) ? string.Empty : $" 地区 {locationText}。")}" +
            $"{(string.IsNullOrWhiteSpace(networkText) ? string.Empty : $" 网络 {networkText}。")}" +
            $"{(string.IsNullOrWhiteSpace(coloText) ? string.Empty : $" {coloText}。")}" +
            $" 综合结论：{verdict}。";

        return new ExitIpRiskReviewResult(
            DateTimeOffset.Now,
            origin.PublicIp,
            origin.DetectSource,
            country,
            city,
            asn,
            organization,
            origin.CloudflareColo,
            sources,
            riskSignals,
            positiveSignals,
            verdict,
            summary,
            null);
    }

    internal static bool TryNormalizeTargetIp(string? targetIp, out string? normalizedTargetIp, out string? error)
    {
        normalizedTargetIp = null;
        error = null;
        if (string.IsNullOrWhiteSpace(targetIp))
        {
            return true;
        }

        var trimmed = targetIp.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
            trimmed.EndsWith("]", StringComparison.Ordinal) &&
            trimmed.Length > 2)
        {
            trimmed = trimmed[1..^1];
        }

        if (!IPAddress.TryParse(trimmed, out var address))
        {
            error = "请输入有效的 IPv4 或 IPv6 地址；留空则检测本机当前出口。";
            return false;
        }

        normalizedTargetIp = address.ToString();
        return true;
    }

    private static ExitIpRiskReviewResult BuildInvalidTargetResult(string? targetIp, string? error)
        => new(
            DateTimeOffset.Now,
            string.IsNullOrWhiteSpace(targetIp) ? null : targetIp.Trim(),
            "用户输入",
            null,
            null,
            null,
            null,
            null,
            Array.Empty<ExitIpRiskSourceResult>(),
            ["目标 IP 格式无效，未发起多源查询。"],
            Array.Empty<string>(),
            "待复核",
            error ?? "目标 IP 格式无效。",
            error ?? "目标 IP 格式无效。");

    private static ResolvedOrigin ResolveSpecifiedOrigin(string ip)
        => new(
            ip,
            "用户指定 IP",
            null,
            null,
            null,
            null,
            null,
            null);

    private static bool IsSpecifiedOrigin(ResolvedOrigin origin)
        => string.Equals(origin.DetectSource, "用户指定 IP", StringComparison.Ordinal);

    private static bool IsRiskProbeSource(ExitIpRiskSourceResult source)
        => !string.Equals(source.Key, "current-origin", StringComparison.Ordinal);

    private static async Task<ResolvedOrigin> ResolveCurrentOriginAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonDocumentAsync("https://iprisk.top/api/myip", cancellationToken);
            var root = document.RootElement;
            var publicIp = GetString(root, "ip");
            if (string.IsNullOrWhiteSpace(publicIp))
            {
                throw new InvalidOperationException("当前出口 IP 为空。");
            }

            var geo = GetProperty(root, "geo");
            var network = GetProperty(root, "network");
            var tls = GetProperty(root, "tls");
            var httpProtocol = GetString(network, "httpProtocol");
            var tlsVersion = GetString(tls, "version");
            var tlsCipher = GetString(tls, "cipher");
            var detail =
                $"IP：{publicIp}\n" +
                $"地区：{JoinNonEmpty(" / ", GetString(geo, "country"), GetString(geo, "city"))}\n" +
                $"ASN：{FormatAsn(GetString(network, "asn"))}\n" +
                $"组织：{GetString(network, "asOrganization") ?? "--"}\n" +
                $"Cloudflare 节点：{GetString(network, "colo") ?? "--"}\n" +
                $"协议：{httpProtocol ?? "--"} / {tlsVersion ?? "--"}\n" +
                $"TLS Cipher：{tlsCipher ?? "--"}";

            return new ResolvedOrigin(
                publicIp,
                "iprisk.top /api/myip",
                GetString(geo, "country"),
                GetString(geo, "city"),
                FormatAsn(GetString(network, "asn")),
                GetString(network, "asOrganization"),
                GetString(network, "colo"),
                new ExitIpRiskSourceResult(
                    "current-origin",
                    "当前出口",
                    "出口识别",
                    true,
                    "信息",
                    $"出口 {publicIp}，{JoinNonEmpty(" / ", GetString(geo, "country"), GetString(geo, "city"))}",
                    detail,
                    Country: GetString(geo, "country"),
                    City: GetString(geo, "city"),
                    Asn: FormatAsn(GetString(network, "asn")),
                    Organization: GetString(network, "asOrganization")));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var fallbackUrls = new[]
            {
                "https://api.ipify.org?format=json",
                "https://api.ip.sb/jsonip"
            };

            foreach (var fallbackUrl in fallbackUrls)
            {
                try
                {
                    using var fallbackDocument = await GetJsonDocumentAsync(fallbackUrl, cancellationToken);
                    var publicIp = GetString(fallbackDocument.RootElement, "ip");
                    if (string.IsNullOrWhiteSpace(publicIp))
                    {
                        continue;
                    }

                    return new ResolvedOrigin(
                        publicIp,
                        fallbackUrl,
                        null,
                        null,
                        null,
                        null,
                        null,
                        new ExitIpRiskSourceResult(
                            "current-origin",
                            "当前出口",
                            "出口识别",
                            true,
                            "信息",
                            $"出口 {publicIp}，来源 {fallbackUrl}",
                            $"IP：{publicIp}\n来源：{fallbackUrl}",
                            Country: null,
                            City: null,
                            Asn: null,
                            Organization: null));
                }
                catch (Exception innerEx) when (innerEx is HttpRequestException or TaskCanceledException or JsonException)
                {
                    ex = innerEx;
                }
            }

            return new ResolvedOrigin(
                null,
                "出口识别失败",
                null,
                null,
                null,
                null,
                null,
                new ExitIpRiskSourceResult(
                    "current-origin",
                    "当前出口",
                    "出口识别",
                    false,
                    "失败",
                    "未能识别当前公网出口 IP。",
                    ex.Message,
                    Error: ex.Message),
                ex.Message);
        }
    }


}
