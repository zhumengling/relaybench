using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed class ExitIpRiskReviewService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private const string IpInfoTokenEnvironmentVariable = "IPINFO_TOKEN";
    private const string RelayBenchIpInfoTokenEnvironmentVariable = "RELAYBENCH_IPINFO_TOKEN";

    public async Task<ExitIpRiskReviewResult> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("正在识别当前出口 IP...");
        var origin = await ResolveCurrentOriginAsync(cancellationToken);
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
        var successfulRiskSources = sources.Count(source => source.Succeeded && !string.Equals(source.Key, "current-origin", StringComparison.Ordinal));

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

        var summary =
            $"当前出口 {origin.PublicIp}，来源 {origin.DetectSource}，成功查询 {successfulSources}/{sources.Count} 个源。" +
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

    private static async Task<ExitIpRiskSourceResult> ProbeIpApiIsAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "ipapi.is";

        try
        {
            using var document = await GetJsonDocumentAsync($"https://api.ipapi.is/?q={Uri.EscapeDataString(ip)}", cancellationToken);
            var root = document.RootElement;
            var isDatacenter = GetBool(root, "is_datacenter");
            var isProxy = GetBool(root, "is_proxy");
            var isVpn = GetBool(root, "is_vpn");
            var isTor = GetBool(root, "is_tor");
            var isAbuser = GetBool(root, "is_abuser");
            var location = GetProperty(root, "location");
            var company = GetProperty(root, "company");
            var asn = GetProperty(root, "asn");
            var verdict = BuildRiskVerdict(
                isDatacenter,
                isProxy,
                isVpn,
                isTor,
                isAbuser,
                riskScore: null);

            var summary =
                $"机房={FormatYesNo(isDatacenter)}，代理={FormatYesNo(isProxy)}，VPN={FormatYesNo(isVpn)}，Tor={FormatYesNo(isTor)}，滥用={FormatYesNo(isAbuser)}";
            var detail =
                $"IP：{GetString(root, "ip") ?? ip}\n" +
                $"地区：{JoinNonEmpty(" / ", GetString(location, "country"), GetString(location, "city"))}\n" +
                $"公司：{GetString(company, "name") ?? "--"}\n" +
                $"类型：{GetString(company, "type") ?? "--"}\n" +
                $"网段：{GetString(company, "network") ?? "--"}\n" +
                $"ASN：{FormatAsn(GetString(asn, "asn"))}\n" +
                $"ASN 组织：{GetString(asn, "org") ?? "--"}\n" +
                $"ASN 类型：{GetString(asn, "type") ?? "--"}\n" +
                $"滥用评分：{GetString(company, "abuser_score") ?? "--"}";

            return new ExitIpRiskSourceResult(
                "ipapi-is",
                sourceName,
                "风险源",
                true,
                verdict,
                summary,
                detail,
                isDatacenter,
                isProxy,
                isVpn,
                isTor,
                isAbuser,
                Country: GetString(location, "country"),
                City: GetString(location, "city"),
                Asn: FormatAsn(GetString(asn, "asn")),
                Organization: GetString(company, "name"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("ipapi-is", sourceName, ex);
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeProxyCheckAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "proxycheck.io";

        try
        {
            using var document = await GetJsonDocumentAsync(
                $"https://proxycheck.io/v2/{Uri.EscapeDataString(ip)}?vpn=1&asn=1&risk=1",
                cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty(ip, out var entry))
            {
                throw new JsonException("proxycheck.io 未返回目标 IP 节点。");
            }

            var proxy = IsYes(GetString(entry, "proxy"));
            var type = GetString(entry, "type");
            var riskScore = GetDouble(entry, "risk");
            var isVpn = type?.Equals("VPN", StringComparison.OrdinalIgnoreCase) == true;
            var isTor = type?.Equals("TOR", StringComparison.OrdinalIgnoreCase) == true;
            var verdict = BuildRiskVerdict(
                isDatacenter: null,
                isProxy: proxy,
                isVpn: isVpn,
                isTor: isTor,
                isAbuse: riskScore is >= 75d,
                riskScore);

            var summary =
                $"代理={FormatYesNo(proxy)}，类型={type ?? "--"}，风险分={FormatRiskScore(riskScore)}";
            var detail =
                $"IP：{ip}\n" +
                $"代理：{FormatYesNo(proxy)}\n" +
                $"类型：{type ?? "--"}\n" +
                $"风险分：{FormatRiskScore(riskScore)}\n" +
                $"ASN：{GetString(entry, "asn") ?? "--"}\n" +
                $"提供商：{GetString(entry, "provider") ?? "--"}\n" +
                $"组织：{GetString(entry, "organisation") ?? "--"}\n" +
                $"地区：{JoinNonEmpty(" / ", GetString(entry, "country"), GetString(entry, "city"))}";

            return new ExitIpRiskSourceResult(
                "proxycheck",
                sourceName,
                "风险源",
                true,
                verdict,
                summary,
                detail,
                IsProxy: proxy,
                IsVpn: isVpn,
                IsTor: isTor,
                IsAbuse: riskScore is >= 75d,
                RiskScore: riskScore,
                Country: GetString(entry, "country"),
                City: GetString(entry, "city"),
                Asn: GetString(entry, "asn"),
                Organization: GetString(entry, "provider"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("proxycheck", sourceName, ex);
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeIpApiComAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "ip-api.com";

        try
        {
            using var document = await GetJsonDocumentAsync(
                $"http://ip-api.com/json/{Uri.EscapeDataString(ip)}?fields=status,message,country,countryCode,regionName,city,isp,org,as,hosting,proxy,mobile,query",
                cancellationToken);
            var root = document.RootElement;
            var status = GetString(root, "status");
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException(GetString(root, "message") ?? "ip-api.com 返回失败。");
            }

            var hosting = GetBool(root, "hosting");
            var proxy = GetBool(root, "proxy");
            var mobile = GetBool(root, "mobile");
            var verdict = BuildRiskVerdict(
                isDatacenter: hosting,
                isProxy: proxy,
                isVpn: null,
                isTor: null,
                isAbuse: null,
                riskScore: null);
            var summary =
                $"hosting={FormatYesNo(hosting)}，proxy={FormatYesNo(proxy)}，mobile={FormatYesNo(mobile)}";
            var detail =
                $"IP：{GetString(root, "query") ?? ip}\n" +
                $"地区：{JoinNonEmpty(" / ", GetString(root, "country"), GetString(root, "city"))}\n" +
                $"组织：{GetString(root, "org") ?? "--"}\n" +
                $"运营商：{GetString(root, "isp") ?? "--"}\n" +
                $"ASN：{GetString(root, "as") ?? "--"}\n" +
                $"hosting：{FormatYesNo(hosting)}\n" +
                $"proxy：{FormatYesNo(proxy)}\n" +
                $"mobile：{FormatYesNo(mobile)}";

            return new ExitIpRiskSourceResult(
                "ip-api",
                sourceName,
                "风险源",
                true,
                verdict,
                summary,
                detail,
                IsDatacenter: hosting,
                IsProxy: proxy,
                Country: GetString(root, "country"),
                City: GetString(root, "city"),
                Asn: GetString(root, "as"),
                Organization: GetString(root, "org"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("ip-api", sourceName, ex);
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeIpWhoIsAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "ipwho.is";

        try
        {
            using var document = await GetJsonDocumentAsync($"https://ipwho.is/{Uri.EscapeDataString(ip)}", cancellationToken);
            var root = document.RootElement;
            if (GetBool(root, "success") == false)
            {
                throw new JsonException(GetString(root, "message") ?? "ipwho.is 返回失败。");
            }

            var connection = GetProperty(root, "connection");
            var summary = $"地区={JoinNonEmpty(" / ", GetString(root, "country"), GetString(root, "city"))}，组织={GetString(connection, "org") ?? "--"}";
            var detail =
                $"IP：{GetString(root, "ip") ?? ip}\n" +
                $"地区：{JoinNonEmpty(" / ", GetString(root, "country"), GetString(root, "region"), GetString(root, "city"))}\n" +
                $"坐标：{FormatCoordinate(root)}\n" +
                $"ASN：{FormatAsn(GetString(connection, "asn"))}\n" +
                $"组织：{GetString(connection, "org") ?? "--"}\n" +
                $"运营商：{GetString(connection, "isp") ?? "--"}\n" +
                $"域名：{GetString(connection, "domain") ?? "--"}";

            return new ExitIpRiskSourceResult(
                "ipwhois",
                sourceName,
                "地理源",
                true,
                "信息",
                summary,
                detail,
                Country: GetString(root, "country"),
                City: GetString(root, "city"),
                Asn: FormatAsn(GetString(connection, "asn")),
                Organization: GetString(connection, "org"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("ipwhois", sourceName, ex);
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeIpInfoAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "country.is";

        try
        {
            using var document = await GetJsonDocumentAsync(
                $"https://api.country.is/{Uri.EscapeDataString(ip)}?fields=city,continent,subdivision,postal,location,asn",
                cancellationToken);
            var root = document.RootElement;
            var org = GetString(root, "org");
            var summary = $"地区={JoinNonEmpty(" / ", GetString(root, "country"), GetString(root, "city"))}，组织={org ?? "--"}";
            var detail =
                $"IP：{GetString(root, "ip") ?? ip}\n" +
                $"地区：{JoinNonEmpty(" / ", GetString(root, "country"), GetString(root, "region"), GetString(root, "city"))}\n" +
                $"组织：{org ?? "--"}\n" +
                $"邮编：{GetString(root, "postal") ?? "--"}\n" +
                $"时区：{GetString(root, "timezone") ?? "--"}\n" +
                $"坐标：{GetString(root, "loc") ?? "--"}";

            return new ExitIpRiskSourceResult(
                "ipinfo",
                sourceName,
                "地理源",
                true,
                "信息",
                summary,
                detail,
                Country: GetString(root, "country"),
                City: GetString(root, "city"),
                Asn: org is null ? null : org.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                Organization: org);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new ExitIpRiskSourceResult(
                "ipinfo",
                sourceName,
                "地理源",
                false,
                "限流",
                "ipinfo.io 匿名查询触发 429 限流，本轮未返回数据。",
                "当前出口对 ipinfo.io 的匿名配额已耗尽。如需稳定使用，可在环境变量里配置 IPINFO_TOKEN 或 RELAYBENCH_IPINFO_TOKEN 后再重试。",
                Error: ex.Message);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ExitIpRiskSourceResult(
                "ipinfo",
                sourceName,
                "地理源",
                false,
                "需鉴权",
                "ipinfo.io 当前请求被拒绝，需要有效令牌或更高权限。",
                "请检查 IPINFO_TOKEN / RELAYBENCH_IPINFO_TOKEN 是否已配置、是否有效，以及其用量是否充足。",
                Error: ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return new ExitIpRiskSourceResult(
                "ipinfo",
                sourceName,
                "地理源",
                false,
                "超时",
                "ipinfo.io 查询超时，本轮未返回结果。",
                "当前更像是网络链路缓慢、被拦截，或对方响应过慢。可稍后重试，或结合其他免配额来源一起判断。",
                Error: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return BuildFailureSourceResult("ipinfo", sourceName, ex);
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeCountryIsAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "country.is";

        try
        {
            using var document = await GetJsonDocumentAsync(
                $"https://api.country.is/{Uri.EscapeDataString(ip)}?fields=city,continent,subdivision,postal,location,asn",
                cancellationToken);
            var root = document.RootElement;
            var asn = GetProperty(root, "asn");
            var location = GetProperty(root, "location");
            var asnNumber = GetString(asn, "number");
            var organization = GetString(asn, "organization");
            var summary = $"地区={JoinNonEmpty(" / ", GetString(root, "country"), GetString(root, "city"))}，组织={organization ?? "--"}";
            var detail =
                $"IP：{GetString(root, "ip") ?? ip}\n" +
                $"地区：{JoinNonEmpty(" / ", GetString(root, "country"), GetString(root, "subdivision"), GetString(root, "city"))}\n" +
                $"组织：{organization ?? "--"}\n" +
                $"ASN：{(string.IsNullOrWhiteSpace(asnNumber) ? "--" : $"AS{asnNumber}")}\n" +
                $"邮编：{GetString(root, "postal") ?? "--"}\n" +
                $"时区：{GetString(location, "time_zone") ?? "--"}\n" +
                $"坐标：{BuildCoordinateText(location)}";

            return new ExitIpRiskSourceResult(
                "country-is",
                sourceName,
                "地理源",
                true,
                "信息",
                summary,
                detail,
                Country: GetString(root, "country"),
                City: GetString(root, "city"),
                Asn: string.IsNullOrWhiteSpace(asnNumber) ? null : $"AS{asnNumber}",
                Organization: organization);
        }
        catch (TaskCanceledException ex)
        {
            return new ExitIpRiskSourceResult(
                "country-is",
                sourceName,
                "地理源",
                false,
                "超时",
                "country.is 查询超时，本轮未返回结果。",
                "当前更像是网络链路缓慢、被拦截，或对方响应过慢。可稍后重试，或结合其他免配额来源一起判断。",
                Error: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return BuildFailureSourceResult("country-is", sourceName, ex);
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeIp2LocationAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "IP2Location.io";

        try
        {
            using var document = await GetJsonDocumentAsync($"https://api.ip2location.io/?ip={Uri.EscapeDataString(ip)}", cancellationToken);
            var root = document.RootElement;
            var isProxy = GetBool(root, "is_proxy");
            var asn = GetString(root, "asn");
            var organization = GetString(root, "as");
            var note = GetString(root, "message");
            var summary =
                $"代理={FormatYesNo(isProxy)}，地区={JoinNonEmpty(" / ", GetString(root, "country_name"), GetString(root, "city_name"))}，ASN={FormatAsn(asn) ?? "--"}";
            var detail =
                $"IP：{GetString(root, "ip") ?? ip}\n" +
                $"地区：{JoinNonEmpty(" / ", GetString(root, "country_name"), GetString(root, "region_name"), GetString(root, "city_name"))}\n" +
                $"坐标：{JoinNonEmpty(", ", GetString(root, "latitude"), GetString(root, "longitude"))}\n" +
                $"邮编：{GetString(root, "zip_code") ?? "--"}\n" +
                $"时区：{GetString(root, "time_zone") ?? "--"}\n" +
                $"ASN：{FormatAsn(asn) ?? "--"}\n" +
                $"组织：{organization ?? "--"}\n" +
                $"代理：{FormatYesNo(isProxy)}\n" +
                $"备注：{note ?? "--"}";

            return new ExitIpRiskSourceResult(
                "ip2location",
                sourceName,
                "风险源",
                true,
                BuildRiskVerdict(
                    isDatacenter: null,
                    isProxy: isProxy,
                    isVpn: null,
                    isTor: null,
                    isAbuse: null,
                    riskScore: null),
                summary,
                detail,
                IsProxy: isProxy,
                Country: GetString(root, "country_name"),
                City: GetString(root, "city_name"),
                Asn: FormatAsn(asn),
                Organization: organization);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("ip2location", sourceName, ex);
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeGreyNoiseCommunityAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "GreyNoise Community";

        try
        {
            using var response = await HttpClient.GetAsync(
                $"https://api.greynoise.io/v3/community/{Uri.EscapeDataString(ip)}",
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ExitIpRiskSourceResult(
                    "greynoise-community",
                    sourceName,
                    "威胁源",
                    true,
                    "信息",
                    "GreyNoise Community 未收录当前出口的社区画像。",
                    $"IP：{ip}\n状态：未收录\n数据源：GreyNoise Community API");
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var noise = GetBool(root, "noise");
            var riot = GetBool(root, "riot");
            var classification = GetString(root, "classification");
            var classificationNormalized = classification?.Trim();
            var isMalicious = string.Equals(classificationNormalized, "malicious", StringComparison.OrdinalIgnoreCase);
            var isObservedNoise = noise == true && riot != true && !string.Equals(classificationNormalized, "benign", StringComparison.OrdinalIgnoreCase);
            double? riskScore = isMalicious
                ? 88d
                : isObservedNoise
                    ? 45d
                    : null;
            var verdict = BuildRiskVerdict(
                isDatacenter: null,
                isProxy: null,
                isVpn: null,
                isTor: null,
                isAbuse: isMalicious,
                riskScore: riskScore);
            var summary =
                $"分类={classificationNormalized ?? "--"}，noise={FormatYesNo(noise)}，riot={FormatYesNo(riot)}，名称={GetString(root, "name") ?? "--"}";
            var detail =
                $"IP：{GetString(root, "ip") ?? ip}\n" +
                $"分类：{classificationNormalized ?? "--"}\n" +
                $"Noise：{FormatYesNo(noise)}\n" +
                $"RIOT：{FormatYesNo(riot)}\n" +
                $"名称：{GetString(root, "name") ?? "--"}\n" +
                $"最近出现：{GetString(root, "last_seen") ?? "--"}\n" +
                $"详情：{GetString(root, "link") ?? "--"}\n" +
                $"消息：{GetString(root, "message") ?? "--"}";

            return new ExitIpRiskSourceResult(
                "greynoise-community",
                sourceName,
                "威胁源",
                true,
                verdict,
                summary,
                detail,
                IsAbuse: isMalicious,
                RiskScore: riskScore);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("greynoise-community", sourceName, ex, "威胁源");
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeSpamhausDropAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "Spamhaus DROP";

        try
        {
            if (!IPAddress.TryParse(ip, out var targetAddress))
            {
                throw new JsonException("无效的目标 IP。");
            }

            var feedUrl = targetAddress.AddressFamily == AddressFamily.InterNetworkV6
                ? "https://www.spamhaus.org/drop/drop_v6.json"
                : "https://www.spamhaus.org/drop/drop_v4.json";

            using var response = await HttpClient.GetAsync(feedUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            string? matchedCidr = null;
            string? matchedSblId = null;
            string? matchedRir = null;

            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var lineDocument = JsonDocument.Parse(line);
                var root = lineDocument.RootElement;
                var cidr = GetString(root, "cidr");
                if (string.IsNullOrWhiteSpace(cidr) || !IsAddressInCidr(targetAddress, cidr))
                {
                    continue;
                }

                matchedCidr = cidr;
                matchedSblId = GetString(root, "sblid");
                matchedRir = GetString(root, "rir");
                break;
            }

            var matched = !string.IsNullOrWhiteSpace(matchedCidr);
            var summary = matched
                ? $"当前出口命中 Spamhaus DROP：{matchedCidr}"
                : "当前出口未命中 Spamhaus DROP。";
            var detail =
                $"IP：{ip}\n" +
                $"命中：{FormatYesNo(matched)}\n" +
                $"CIDR：{matchedCidr ?? "--"}\n" +
                $"SBL：{matchedSblId ?? "--"}\n" +
                $"RIR：{matchedRir ?? "--"}\n" +
                $"数据源：{feedUrl}";

            return new ExitIpRiskSourceResult(
                "spamhaus-drop",
                sourceName,
                "威胁源",
                true,
                matched ? "高风险" : "通过",
                summary,
                detail,
                IsAbuse: matched,
                RiskScore: matched ? 96d : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("spamhaus-drop", sourceName, ex, "威胁源");
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeSpamhausAsnDropAsync(string? asnText, CancellationToken cancellationToken)
    {
        const string sourceName = "Spamhaus ASN-DROP";

        try
        {
            var asnNumber = TryExtractAsnNumber(asnText);
            if (!asnNumber.HasValue)
            {
                return new ExitIpRiskSourceResult(
                    "spamhaus-asndrop",
                    sourceName,
                    "威胁源",
                    true,
                    "信息",
                    "当前结果未拿到可用于比对的 ASN，跳过 ASN-DROP 命中判断。",
                    $"ASN：{asnText ?? "--"}\n状态：未提供 ASN");
            }

            using var response = await HttpClient.GetAsync("https://www.spamhaus.org/drop/asndrop.json", cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            JsonElement? matchedEntry = null;
            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var lineDocument = JsonDocument.Parse(line);
                var root = lineDocument.RootElement;
                var candidateAsn = GetDouble(root, "asn");
                if (candidateAsn.HasValue && Math.Abs(candidateAsn.Value - asnNumber.Value) < 0.5d)
                {
                    matchedEntry = root.Clone();
                    break;
                }
            }

            var matched = matchedEntry.HasValue;
            var matchedValue = matchedEntry.GetValueOrDefault();
            var summary = matched
                ? $"当前 ASN 命中 Spamhaus ASN-DROP：AS{asnNumber.Value}"
                : $"当前 ASN 未命中 Spamhaus ASN-DROP：AS{asnNumber.Value}";
            var detail =
                $"ASN：AS{asnNumber.Value}\n" +
                $"命中：{FormatYesNo(matched)}\n" +
                $"名称：{(matched ? GetString(matchedValue, "asname") ?? "--" : "--")}\n" +
                $"域名：{(matched ? GetString(matchedValue, "domain") ?? "--" : "--")}\n" +
                $"国家：{(matched ? GetString(matchedValue, "cc") ?? "--" : "--")}\n" +
                $"RIR：{(matched ? GetString(matchedValue, "rir") ?? "--" : "--")}\n" +
                "数据源：https://www.spamhaus.org/drop/asndrop.json";

            return new ExitIpRiskSourceResult(
                "spamhaus-asndrop",
                sourceName,
                "威胁源",
                true,
                matched ? "高风险" : "通过",
                summary,
                detail,
                IsAbuse: matched,
                RiskScore: matched ? 90d : null,
                Asn: $"AS{asnNumber.Value}",
                Organization: matched ? GetString(matchedValue, "asname") : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("spamhaus-asndrop", sourceName, ex, "威胁源");
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeAlienVaultOtxAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "AlienVault OTX";

        try
        {
            using var document = await GetJsonDocumentAsync(
                $"https://otx.alienvault.com/api/v1/indicators/IPv4/{Uri.EscapeDataString(ip)}/general",
                cancellationToken);

            var root = document.RootElement;
            var pulseInfo = GetProperty(root, "pulse_info");
            var validation = GetProperty(root, "validation");
            var pulseCount = GetDouble(pulseInfo, "count") ?? 0d;
            var reputation = GetDouble(root, "reputation") ?? 0d;
            var hasWhitelist = ArrayContainsString(validation, "source", "whitelist");
            var hasKnownFalsePositive = ArrayContainsString(validation, "name", "Known False Positive");

            bool isAbuse;
            double? riskScore;
            if (hasWhitelist || hasKnownFalsePositive)
            {
                isAbuse = false;
                riskScore = null;
            }
            else if (pulseCount > 0d || reputation > 0d)
            {
                isAbuse = true;
                riskScore = Math.Min(90d, 35d + (pulseCount * 6d) + (reputation * 4d));
            }
            else
            {
                isAbuse = false;
                riskScore = null;
            }

            var verdict = isAbuse
                ? BuildRiskVerdict(
                    isDatacenter: null,
                    isProxy: null,
                    isVpn: null,
                    isTor: null,
                    isAbuse: true,
                    riskScore: riskScore)
                : "信息";
            var summary =
                $"Pulse={pulseCount.ToString("F0", CultureInfo.InvariantCulture)}，信誉={reputation.ToString("F0", CultureInfo.InvariantCulture)}，白名单={FormatYesNo(hasWhitelist)}，误报豁免={FormatYesNo(hasKnownFalsePositive)}";
            var detail =
                $"IP：{GetString(root, "indicator") ?? ip}\n" +
                $"Pulse 数：{pulseCount.ToString("F0", CultureInfo.InvariantCulture)}\n" +
                $"信誉分：{reputation.ToString("F0", CultureInfo.InvariantCulture)}\n" +
                $"白名单：{FormatYesNo(hasWhitelist)}\n" +
                $"误报豁免：{FormatYesNo(hasKnownFalsePositive)}\n" +
                $"ASN：{GetString(root, "asn") ?? "--"}\n" +
                $"国家：{GetString(root, "country_name") ?? "--"}\n" +
                $"详情：{GetString(root, "whois") ?? "--"}";

            return new ExitIpRiskSourceResult(
                "alienvault-otx",
                sourceName,
                "威胁源",
                true,
                verdict,
                summary,
                detail,
                IsAbuse: isAbuse,
                RiskScore: riskScore,
                Country: GetString(root, "country_name"),
                Asn: FormatAsn(GetString(root, "asn")));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("alienvault-otx", sourceName, ex, "威胁源");
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeShodanInternetDbAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "Shodan InternetDB";

        try
        {
            using var document = await GetJsonDocumentAsync(
                $"https://internetdb.shodan.io/{Uri.EscapeDataString(ip)}",
                cancellationToken);

            var root = document.RootElement;
            var ports = GetArrayValues(root, "ports");
            var vulns = GetArrayValues(root, "vulns");
            var tags = GetArrayValues(root, "tags");
            var hostnames = GetArrayValues(root, "hostnames");
            var hasVulns = vulns.Length > 0;
            var verdict = hasVulns
                ? BuildRiskVerdict(
                    isDatacenter: null,
                    isProxy: null,
                    isVpn: null,
                    isTor: null,
                    isAbuse: true,
                    riskScore: 58d)
                : "信息";
            var summary =
                $"开放端口={ports.Length}，漏洞={vulns.Length}，标签={tags.Length}，主机名={hostnames.Length}";
            var detail =
                $"IP：{GetString(root, "ip") ?? ip}\n" +
                $"端口：{(ports.Length == 0 ? "--" : string.Join(", ", ports))}\n" +
                $"漏洞：{(vulns.Length == 0 ? "--" : string.Join(", ", vulns))}\n" +
                $"标签：{(tags.Length == 0 ? "--" : string.Join(", ", tags))}\n" +
                $"主机名：{(hostnames.Length == 0 ? "--" : string.Join(", ", hostnames))}";

            return new ExitIpRiskSourceResult(
                "shodan-internetdb",
                sourceName,
                "暴露源",
                true,
                verdict,
                summary,
                detail,
                IsAbuse: hasVulns,
                RiskScore: hasVulns ? 58d : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("shodan-internetdb", sourceName, ex, "暴露源");
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeFeodoTrackerAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "abuse.ch Feodo Tracker";

        try
        {
            using var document = await GetJsonDocumentAsync(
                "https://feodotracker.abuse.ch/downloads/ipblocklist_recommended.json",
                cancellationToken);

            JsonElement? matchedEntry = null;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!string.Equals(GetString(entry, "ip_address"), ip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchedEntry = entry;
                break;
            }

            var matched = matchedEntry.HasValue;
            var matchedValue = matchedEntry.GetValueOrDefault();
            var summary = matched
                ? $"当前出口命中 Feodo Tracker：{GetString(matchedValue, "malware") ?? "--"}"
                : "当前出口未命中 Feodo Tracker 推荐封禁列表。";
            var detail =
                $"IP：{ip}\n" +
                $"命中：{FormatYesNo(matched)}\n" +
                $"状态：{(matched ? GetString(matchedValue, "status") ?? "--" : "--")}\n" +
                $"恶意家族：{(matched ? GetString(matchedValue, "malware") ?? "--" : "--")}\n" +
                $"端口：{(matched ? GetString(matchedValue, "port") ?? "--" : "--")}\n" +
                $"ASN：{(matched ? FormatAsn(GetString(matchedValue, "as_number")) ?? "--" : "--")}\n" +
                $"组织：{(matched ? GetString(matchedValue, "as_name") ?? "--" : "--")}\n" +
                $"国家：{(matched ? GetString(matchedValue, "country") ?? "--" : "--")}\n" +
                $"首次出现：{(matched ? GetString(matchedValue, "first_seen") ?? "--" : "--")}\n" +
                $"最近在线：{(matched ? GetString(matchedValue, "last_online") ?? "--" : "--")}";

            return new ExitIpRiskSourceResult(
                "feodo-tracker",
                sourceName,
                "威胁源",
                true,
                matched ? "高风险" : "通过",
                summary,
                detail,
                IsAbuse: matched,
                RiskScore: matched ? 99d : null,
                Country: matched ? GetString(matchedValue, "country") : null,
                Asn: matched ? FormatAsn(GetString(matchedValue, "as_number")) : null,
                Organization: matched ? GetString(matchedValue, "as_name") : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return BuildFailureSourceResult("feodo-tracker", sourceName, ex, "威胁源");
        }
    }

    private static async Task<ExitIpRiskSourceResult> ProbeTorProjectAsync(string ip, CancellationToken cancellationToken)
    {
        const string sourceName = "Tor Project";

        try
        {
            using var response = await HttpClient.GetAsync("https://check.torproject.org/exit-addresses", cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var isTor = content.Contains($"\nExitAddress {ip} ", StringComparison.Ordinal) ||
                        content.StartsWith($"ExitAddress {ip} ", StringComparison.Ordinal);
            var summary = isTor ? "当前出口出现在 Tor 出口列表中。" : "当前出口未出现在 Tor 出口列表中。";
            var detail =
                $"IP：{ip}\n" +
                $"Tor 出口：{FormatYesNo(isTor)}\n" +
                "数据源：Tor Project exit-addresses";

            return new ExitIpRiskSourceResult(
                "tor-project",
                sourceName,
                "风险源",
                true,
                isTor ? "高风险" : "通过",
                summary,
                detail,
                IsTor: isTor);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return BuildFailureSourceResult("tor-project", sourceName, ex);
        }
    }

    private static ExitIpRiskSourceResult BuildFailureSourceResult(string key, string displayName, Exception ex, string category = "风险源")
        => new(
            key,
            displayName,
            category,
            false,
            "失败",
            $"{displayName} 查询失败。",
            ex.Message,
            Error: ex.Message);

    private static string BuildRiskVerdict(
        bool? isDatacenter,
        bool? isProxy,
        bool? isVpn,
        bool? isTor,
        bool? isAbuse,
        double? riskScore)
    {
        if (isTor == true || isAbuse == true || riskScore is >= 70d)
        {
            return "高风险";
        }

        if (isProxy == true || isVpn == true || isDatacenter == true || riskScore is >= 35d)
        {
            return "注意";
        }

        return "通过";
    }

    private static string? FormatAsn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.StartsWith("AS", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"AS{value}";
    }

    private static string FormatCoordinate(JsonElement root)
    {
        var latitude = GetDouble(root, "latitude");
        var longitude = GetDouble(root, "longitude");
        if (latitude is null || longitude is null)
        {
            return "--";
        }

        return $"{latitude.Value.ToString("F4", CultureInfo.InvariantCulture)}, {longitude.Value.ToString("F4", CultureInfo.InvariantCulture)}";
    }

    private static string FormatRiskScore(double? value)
        => value is null ? "--" : value.Value.ToString("F0", CultureInfo.InvariantCulture);

    private static string FormatYesNo(bool? value)
        => value switch
        {
            true => "是",
            false => "否",
            _ => "--"
        };

    private static bool IsYes(string? value)
        => string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static int? TryExtractAsnNumber(string? asnText)
    {
        if (string.IsNullOrWhiteSpace(asnText))
        {
            return null;
        }

        var digits = new string(asnText.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsAddressInCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var networkAddress) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefixLength))
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length)
        {
            return false;
        }

        var totalBits = addressBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > totalBits)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var index = 0; index < fullBytes; index++)
        {
            if (addressBytes[index] != networkBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static string JoinNonEmpty(string separator, params string?[] values)
        => string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string? FirstNonEmpty(string? first, params string?[] rest)
        => new[] { first }
            .Concat(rest)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static JsonElement GetProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var property)
            ? property
            : default;

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static bool? GetBool(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsedBool) => parsedBool,
            JsonValueKind.String when string.Equals(property.GetString(), "yes", StringComparison.OrdinalIgnoreCase) => true,
            JsonValueKind.String when string.Equals(property.GetString(), "no", StringComparison.OrdinalIgnoreCase) => false,
            JsonValueKind.Number when property.TryGetInt32(out var parsedInt) => parsedInt != 0,
            _ => null
        };
    }

    private static double? GetDouble(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var parsedDouble) => parsedDouble,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble) => parsedDouble,
            _ => null
        };
    }

    private static string BuildCoordinateText(JsonElement root)
    {
        var latitude = GetDouble(root, "latitude");
        var longitude = GetDouble(root, "longitude");
        if (latitude is null || longitude is null)
        {
            return "--";
        }

        return $"{latitude.Value.ToString("F4", CultureInfo.InvariantCulture)}, {longitude.Value.ToString("F4", CultureInfo.InvariantCulture)}";
    }

    private static bool ArrayContainsString(JsonElement root, string propertyName, string expected)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in root.EnumerateArray())
        {
            var value = GetString(item, propertyName);
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] GetArrayValues(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property
            .EnumerateArray()
            .Select(static item => item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray()!;
    }

    private static async Task<JsonDocument> GetJsonDocumentAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string BuildIpInfoUrl(string ip)
    {
        var baseUrl = $"https://ipinfo.io/{Uri.EscapeDataString(ip)}/json";
        var token = GetIpInfoToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return baseUrl;
        }

        return $"{baseUrl}?token={Uri.EscapeDataString(token)}";
    }

    private static string? GetIpInfoToken()
        => FirstNonEmpty(
            Environment.GetEnvironmentVariable(IpInfoTokenEnvironmentVariable),
            Environment.GetEnvironmentVariable(RelayBenchIpInfoTokenEnvironmentVariable));

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/0.15 (Windows desktop diagnostics)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private sealed record ResolvedOrigin(
        string? PublicIp,
        string DetectSource,
        string? Country,
        string? City,
        string? Asn,
        string? Organization,
        string? CloudflareColo,
        ExitIpRiskSourceResult? SourceResult,
        string? Error = null);
}
