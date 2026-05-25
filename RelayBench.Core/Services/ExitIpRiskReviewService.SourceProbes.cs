using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ExitIpRiskReviewService
{
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
        const string sourceName = "ipinfo.io";

        try
        {
            using var document = await GetJsonDocumentAsync(BuildIpInfoUrl(ip), cancellationToken);
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
}
