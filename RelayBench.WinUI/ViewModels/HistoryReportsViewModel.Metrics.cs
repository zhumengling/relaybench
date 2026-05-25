using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class HistoryReportsViewModel
{
    private void PopulateMetricTilesFromPayload(JsonElement root)
    {
        if (TryGetProperty(root, "latencies", out var latencies))
        {
            AddMetricTileIfPresent("模型", TryGetDouble(latencies, "modelsMs"), "延迟", HistoryTones.Accent);
            AddMetricTileIfPresent("聊天", TryGetDouble(latencies, "chatMs"), "延迟", HistoryTones.Healthy);
            AddMetricTileIfPresent("TTFT", TryGetDouble(latencies, "ttftMs"), "首 Token", HistoryTones.Warning);
            AddMetricTileIfPresent("流式", TryGetDouble(latencies, "streamMs"), "时长", HistoryTones.Accent);
        }

        if (TryGetProperty(root, "throughput", out var throughput))
        {
            var median = TryGetDouble(throughput, "MedianOutputTokensPerSecond") ??
                         TryGetDouble(throughput, "medianOutputTokensPerSecond");
            if (median.HasValue)
            {
                MetricTiles.Add(new HistoryMetricTile("吞吐", $"{median.Value:F1}", "tokens/s", HistoryTones.Accent));
            }
        }

        if (IsModelChatPayload(root))
        {
            var inputTokens = ReadTotalInputTokens(root) ?? 0;
            var outputTokens = ReadTotalOutputTokens(root) ?? 0;
            var promptCacheTokens = ReadPromptCacheTokens(root) ?? 0;
            var cacheHitRate = ReadCacheHitRate(root, inputTokens, promptCacheTokens);
            MetricTiles.Add(new HistoryMetricTile("输入 Token", FormatCompactNumber(inputTokens), "大模型对话", HistoryTones.Accent));
            MetricTiles.Add(new HistoryMetricTile("输出 Token", FormatCompactNumber(outputTokens), "大模型对话", HistoryTones.Healthy));
            MetricTiles.Add(new HistoryMetricTile("缓存 Token", FormatCompactNumber(promptCacheTokens), "提示缓存", HistoryTones.Warning));
            MetricTiles.Add(new HistoryMetricTile("缓存命中", $"{cacheHitRate:F1}%", "真实 payload", HistoryTones.Accent));
            AddNetworkMetricTile("基础 URL", TryGetString(root, "BaseUrl") ?? TryGetString(root, "baseUrl"), "大模型对话", HistoryTones.Accent);
            AddNetworkMetricTile("接口", TryGetString(root, "EndpointName") ?? TryGetString(root, "endpointName"), "配置档", HistoryTones.Warning);
            AddNetworkMetricTile("路由", TryGetString(root, "RouteSummary") ?? TryGetString(root, "ProtocolSummary"), "代理上下文", HistoryTones.Accent);
            AddMetricTileIfPresent("首 Token", TryGetDouble(root, "FirstTokenLatencyMs") ?? TryGetDouble(root, "firstTokenLatencyMs"), "大模型对话", HistoryTones.Warning);
            AddNumberMetricTileIfPresent("消息", TryGetDouble(root, "MessageCount") ?? TryGetDouble(root, "messageCount"), "对话", HistoryTones.Accent);
            AddNumberMetricTileIfPresent("附件", TryGetDouble(root, "AttachmentCount") ?? TryGetDouble(root, "attachmentCount"), "对话", HistoryTones.Accent);
            AddNumberMetricTileIfPresent("输出字符", TryGetDouble(root, "OutputCharacters") ?? TryGetDouble(root, "outputCharacters"), "响应", HistoryTones.Healthy);
            AddNumberMetricTileIfPresent("模型", TryGetDouble(root, "ResultCount") ?? TryGetDouble(root, "resultCount"), "多模型", HistoryTones.Accent);
            AddNumberMetricTileIfPresent("成功", TryGetDouble(root, "SuccessCount") ?? TryGetDouble(root, "successCount"), "多模型", HistoryTones.Healthy);
            AddNumberMetricTileIfPresent("失败", TryGetDouble(root, "FailedCount") ?? TryGetDouble(root, "failedCount"), "多模型", HistoryTones.Danger);
            if (IsModelChatMultiPayload(root) &&
                TryGetProperty(root, "Results", out var modelResults) &&
                modelResults.ValueKind == JsonValueKind.Array)
            {
                var outputCharacters = modelResults.EnumerateArray()
                    .Sum(static item => TryGetDouble(item, "OutputCharacters") ?? TryGetDouble(item, "outputCharacters") ?? 0);
                MetricTiles.Add(new HistoryMetricTile("输出字符", FormatCompactNumber(outputCharacters), "多模型", HistoryTones.Healthy));
            }
            var usesProxy = TryGetBool(root, "IsTransparentProxyEndpoint") ?? TryGetBool(root, "isTransparentProxyEndpoint");
            if (usesProxy.HasValue)
            {
                MetricTiles.Add(new HistoryMetricTile("本地代理", usesProxy.Value ? "是" : "否", "大模型对话", usesProxy.Value ? HistoryTones.Healthy : HistoryTones.Warning));
            }
        }

        if ((TryGetString(root, "Schema") ?? TryGetString(root, "schema"))?.StartsWith("network-review", StringComparison.OrdinalIgnoreCase) == true &&
            TryGetProperty(root, "Proxy", out var proxyContext))
        {
            MetricTiles.Add(new HistoryMetricTile("代理路由", TryGetString(proxyContext, "RouteSummary") ?? "--", "网络上下文", HistoryTones.Accent));
            MetricTiles.Add(new HistoryMetricTile("代理模型", TryGetString(proxyContext, "ModelPool") ?? "--", "网络上下文", HistoryTones.Healthy));
            MetricTiles.Add(new HistoryMetricTile("代理缓存", TryGetString(proxyContext, "Cache") ?? "--", "网络上下文", HistoryTones.Warning));
            MetricTiles.Add(new HistoryMetricTile("Codex OAuth", TryGetString(proxyContext, "CodexOAuth") ?? "--", "网络上下文", HistoryTones.Accent));
            MetricTiles.Add(new HistoryMetricTile("协议", TryGetString(proxyContext, "ProtocolSummary") ?? "--", "网络上下文", HistoryTones.Accent));
            MetricTiles.Add(new HistoryMetricTile("管理", TryGetString(proxyContext, "Management") ?? "--", "网络上下文", HistoryTones.Healthy));
            AddNetworkMetricTile("操作", TryGetString(root, "Operation") ?? TryGetString(root, "operation"), "网络复核", HistoryTones.Accent);
            AddNetworkMetricTile("运行时", TryGetString(proxyContext, "Runtime"), "代理上下文", HistoryTones.Healthy);
            AddNetworkMetricTile("连接", TryGetString(proxyContext, "Connection"), "代理上下文", HistoryTones.Warning);
            AddNetworkMetricTile("Token 速度", TryGetString(proxyContext, "TokenSpeed"), "代理上下文", HistoryTones.Accent);
            AddNetworkMetricTile("最近错误", TryGetString(proxyContext, "RecentError"), "代理上下文", HistoryTones.Danger);
            if (TryGetProperty(root, "Result", out var networkResult))
            {
                AddNetworkMetricTile("出口 IP", TryGetString(networkResult, "PublicIp") ?? TryGetString(networkResult, "publicIp"), "出口", HistoryTones.Accent);
                AddNetworkMetricTile("CDN 节点", TryGetString(networkResult, "CloudflareColo") ?? TryGetString(networkResult, "cloudflareColo"), "追踪", HistoryTones.Healthy);
                AddNetworkMetricTile("NAT", TryGetString(networkResult, "NatType") ?? TryGetString(networkResult, "natType"), "stun", HistoryTones.Warning);
                AddNetworkMetricTile("风险", TryGetString(networkResult, "Verdict") ?? TryGetString(networkResult, "verdict"), "IP 复核", HistoryTones.Accent);
                AddNetworkMetricTile("映射地址", TryGetString(networkResult, "MappedAddress") ?? TryGetString(networkResult, "mappedAddress"), "STUN", HistoryTones.Accent);
                AddNetworkMetricTile("DNS", TryGetString(networkResult, "DnsServer") ?? TryGetString(networkResult, "dnsServer") ?? TryGetString(networkResult, "Resolver"), "dns", HistoryTones.Healthy);
                AddNetworkMetricTile("解析地址", TryGetString(networkResult, "ResolvedAddress") ?? TryGetString(networkResult, "resolvedAddress"), "DNS", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("路由跳数", TryGetDouble(networkResult, "RouteHopCount") ?? TryGetDouble(networkResult, "routeHopCount") ?? TryGetDouble(networkResult, "HopCount") ?? ReadArrayLength(networkResult, "Hops", "RouteHops"), "路由", HistoryTones.Warning);
                AddNumberMetricTileIfPresent("开放端口", TryGetDouble(networkResult, "OpenPortCount") ?? TryGetDouble(networkResult, "openPortCount") ?? ReadOpenPortCount(networkResult), "端口扫描", HistoryTones.Healthy);
                AddNumberMetricTileIfPresent("网卡", ReadArrayLength(networkResult, "Adapters", "NetworkAdapters"), "网络", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("目标", TryGetDouble(networkResult, "TargetCount") ?? TryGetDouble(networkResult, "targetCount") ?? ReadArrayLength(networkResult, "Targets"), "网络", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("发现项", ReadArrayLength(networkResult, "Findings"), "端口扫描", HistoryTones.Warning);
                AddMetricTileIfPresent("延迟", TryGetDouble(networkResult, "LatencyMs") ?? TryGetDouble(networkResult, "AverageLatencyMs") ?? TryGetDouble(networkResult, "averageLatencyMs"), "网络", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("丢包率", TryGetDouble(networkResult, "PacketLossPercent") ?? TryGetDouble(networkResult, "packetLossPercent"), "网络", HistoryTones.Danger, "%");
            }
        }

        var schema = TryGetString(root, "Schema") ?? TryGetString(root, "schema") ?? string.Empty;
        if (schema.StartsWith("application-access", StringComparison.OrdinalIgnoreCase))
        {
            MetricTiles.Add(new HistoryMetricTile("目标", TryGetString(root, "TargetName") ?? TryGetString(root, "targetName") ?? "--", "应用", HistoryTones.Accent));
            var targetCount = TryGetDouble(root, "TargetCount") ?? TryGetDouble(root, "targetCount");
            if (targetCount.HasValue)
            {
                MetricTiles.Add(new HistoryMetricTile("目标数", FormatCompactNumber(targetCount.Value), "批量", HistoryTones.Accent));
                MetricTiles.Add(new HistoryMetricTile("成功", FormatCompactNumber(TryGetDouble(root, "SucceededTargetCount") ?? TryGetDouble(root, "succeededTargetCount") ?? 0), "批量", HistoryTones.Healthy));
                MetricTiles.Add(new HistoryMetricTile("失败", FormatCompactNumber(TryGetDouble(root, "FailedTargetCount") ?? TryGetDouble(root, "failedTargetCount") ?? 0), "批量", HistoryTones.Danger));
            }
            MetricTiles.Add(new HistoryMetricTile("变更文件", FormatCompactNumber(TryGetDouble(root, "ChangedFileCount") ?? TryGetDouble(root, "changedFileCount") ?? 0), "文件", HistoryTones.Healthy));
            MetricTiles.Add(new HistoryMetricTile("备份", FormatCompactNumber(TryGetDouble(root, "BackupFileCount") ?? TryGetDouble(root, "backupFileCount") ?? 0), "还原点", HistoryTones.Warning));
            MetricTiles.Add(new HistoryMetricTile("本地代理", (TryGetBool(root, "LocalProxy") ?? TryGetBool(root, "localProxy") ?? false) ? "是" : "否", "接口", HistoryTones.Accent));
            MetricTiles.Add(new HistoryMetricTile("Wire API", TryGetString(root, "PreferredWireApi") ?? TryGetString(root, "preferredWireApi") ?? "--", "protocol", HistoryTones.Accent));
            AddNetworkMetricTile("操作", TryGetString(root, "Action") ?? TryGetString(root, "action"), "应用", HistoryTones.Accent);
            AddNetworkMetricTile("协议类型", TryGetString(root, "ProtocolKind") ?? TryGetString(root, "protocolKind"), "应用", HistoryTones.Accent);
            AddNetworkMetricTile("API 密钥", TryGetString(root, "ApiKeyMasked") ?? TryGetString(root, "apiKeyMasked"), "已遮罩", HistoryTones.Warning);
            AddNumberMetricTileIfPresent("模型", TryGetDouble(root, "AvailableModelCount") ?? TryGetDouble(root, "availableModelCount"), "可用", HistoryTones.Healthy);
            AddNetworkMetricTile("摘要", TryGetString(root, "Summary") ?? TryGetString(root, "summary"), "操作", HistoryTones.Healthy);
            AddNetworkMetricTile("错误", TryGetString(root, "Error") ?? TryGetString(root, "error"), "操作", HistoryTones.Danger);
            if (TryGetProperty(root, "Probe", out var probe) && probe.ValueKind == JsonValueKind.Object)
            {
                AddNetworkMetricTile("探测模型", TryGetString(probe, "ProbeModel") ?? TryGetString(probe, "probeModel"), "探测", HistoryTones.Accent);
                AddNetworkMetricTile("探测结果", TryGetString(probe, "Summary") ?? TryGetString(probe, "summary"), "探测", HistoryTones.Healthy);
                AddNetworkMetricTile("探测错误", TryGetString(probe, "Error") ?? TryGetString(probe, "error"), "探测", HistoryTones.Danger);
                AddNetworkMetricTile("探测时间", TryGetString(probe, "CheckedAt") ?? TryGetString(probe, "checkedAt"), "探测", HistoryTones.Accent);
            }
            if (TryGetProperty(root, "Targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
            {
                AddNetworkMetricTile("失败目标", BuildFailedTargetSummary(targets), "批量", HistoryTones.Danger);
            }
            if (TryGetProperty(root, "TargetSnapshot", out var targetSnapshot) ||
                TryGetProperty(root, "targetSnapshot", out targetSnapshot))
            {
                MetricTiles.Add(new HistoryMetricTile(
                    "已检测",
                    $"{FormatCompactNumber(TryGetDouble(targetSnapshot, "InstalledTargetCount") ?? TryGetDouble(targetSnapshot, "installedTargetCount") ?? 0)}/{FormatCompactNumber(TryGetDouble(targetSnapshot, "TargetCount") ?? TryGetDouble(targetSnapshot, "targetCount") ?? 0)}",
                    "应用目标",
                    HistoryTones.Accent));
                MetricTiles.Add(new HistoryMetricTile(
                    "可选择",
                    FormatCompactNumber(TryGetDouble(targetSnapshot, "SelectableTargetCount") ?? TryGetDouble(targetSnapshot, "selectableTargetCount") ?? 0),
                    "可写",
                    HistoryTones.Healthy));
                MetricTiles.Add(new HistoryMetricTile(
                    "已选择",
                    FormatCompactNumber(TryGetDouble(targetSnapshot, "SelectedTargetCount") ?? TryGetDouble(targetSnapshot, "selectedTargetCount") ?? 0),
                    "当前操作",
                    HistoryTones.Accent));
            }
        }

        if (schema.StartsWith("transparent-proxy", StringComparison.OrdinalIgnoreCase) ||
            TryGetProperty(root, "RouteHits", out _) ||
            TryGetProperty(root, "routeHits", out _))
        {
            (double Total, double Success, double Failed) routeCounters = (TryGetProperty(root, "RouteHits", out var routeHits) || TryGetProperty(root, "routeHits", out routeHits))
                ? ReadRouteHitCounters(routeHits)
                : (0, 0, 0);
            var recordedProxyRequests = TryGetDouble(root, "TotalRequests") ?? TryGetDouble(root, "totalRequests");
            var proxyTotalRequests = recordedProxyRequests is > 0 ? recordedProxyRequests.Value : routeCounters.Total;
            var proxySuccessRate = TryGetDouble(root, "SuccessRate") ?? TryGetDouble(root, "successRate") ??
                (proxyTotalRequests > 0 ? routeCounters.Success / proxyTotalRequests * 100.0 : 0);
            MetricTiles.Add(new HistoryMetricTile("代理请求", FormatCompactNumber(proxyTotalRequests), "透明代理", HistoryTones.Accent));
            MetricTiles.Add(new HistoryMetricTile("成功率", $"{proxySuccessRate:F1}%", "真实请求", HistoryTones.Healthy));
            MetricTiles.Add(new HistoryMetricTile("缓存命中", $"{TryGetDouble(root, "CacheHitRate") ?? TryGetDouble(root, "cacheHitRate") ?? 0:F1}%", "真实缓存遥测", HistoryTones.Healthy));
            MetricTiles.Add(new HistoryMetricTile("故障切换", FormatCompactNumber(TryGetDouble(root, "FallbackRequests") ?? TryGetDouble(root, "fallbackRequests") ?? 0), "路由故障切换", HistoryTones.Warning));
            MetricTiles.Add(new HistoryMetricTile("429", FormatCompactNumber(TryGetDouble(root, "RateLimitedRequests") ?? TryGetDouble(root, "rateLimitedRequests") ?? 0), "限流", HistoryTones.Danger));
            MetricTiles.Add(new HistoryMetricTile("路由", FormatCompactNumber(TryGetDouble(root, "RouteCount") ?? TryGetDouble(root, "routeCount") ?? 0), "路由命中", HistoryTones.Warning));
            MetricTiles.Add(new HistoryMetricTile("模型池", FormatCompactNumber(TryGetDouble(root, "ModelPoolCount") ?? TryGetDouble(root, "modelPoolCount") ?? 0), "可用池", HistoryTones.Accent));
            MetricTiles.Add(new HistoryMetricTile("Codex OAuth", FormatCompactNumber(TryGetDouble(root, "ReadyCodexOAuthCredentialCount") ?? TryGetDouble(root, "readyCodexOAuthCredentialCount") ?? 0), "可用凭据", HistoryTones.Accent));
            AddNumberMetricTileIfPresent("活跃", TryGetDouble(root, "ActiveConnections") ?? TryGetDouble(root, "activeConnections"), "连接", HistoryTones.Accent);
            AddNumberMetricTileIfPresent("输入 Token", ReadTotalInputTokens(root), "代理用量", HistoryTones.Accent);
            AddNumberMetricTileIfPresent("输出 Token", ReadTotalOutputTokens(root), "代理用量", HistoryTones.Healthy);
            if (TryGetProperty(root, "CacheBreakdown", out var cacheBreakdown) || TryGetProperty(root, "cacheBreakdown", out cacheBreakdown))
            {
                MetricTiles.Add(new HistoryMetricTile(
                    "响应缓存",
                    $"{FormatCompactNumber(TryGetDouble(cacheBreakdown, "ResponseCacheHits") ?? 0)}/{FormatCompactNumber(TryGetDouble(cacheBreakdown, "ResponseCacheMisses") ?? 0)}",
                    "命中/未命中",
                    HistoryTones.Healthy));
                AddNumberMetricTileIfPresent("缓存写入", TryGetDouble(cacheBreakdown, "ResponseCacheStores"), "响应缓存", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("缓存淘汰", TryGetDouble(cacheBreakdown, "ResponseCacheEvictions"), "响应缓存", HistoryTones.Danger);
                MetricTiles.Add(new HistoryMetricTile(
                    "提示缓存",
                    $"{FormatCompactNumber(TryGetDouble(cacheBreakdown, "PromptSessionCacheHits") ?? 0)}/{FormatCompactNumber(TryGetDouble(cacheBreakdown, "PromptSessionCacheMisses") ?? 0)}",
                    "命中/未命中",
                    HistoryTones.Accent));
                AddNumberMetricTileIfPresent("缓存条目", TryGetDouble(cacheBreakdown, "ResponseCacheEntryCount"), "响应缓存", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("进行中", TryGetDouble(cacheBreakdown, "InFlightKeys"), "缓存租约", HistoryTones.Warning);
                AddNumberMetricTileIfPresent("租约等待", TryGetDouble(cacheBreakdown, "LeaseWaits"), "缓存租约", HistoryTones.Warning);
            }
            if (TryGetProperty(root, "LastStatusCodeByRoute", out var statusCodes) ||
                TryGetProperty(root, "lastStatusCodeByRoute", out statusCodes))
            {
                AddNetworkMetricTile("最近状态", BuildStatusCodeSummary(statusCodes), "路由", HistoryTones.Danger);
            }
            if (TryGetProperty(root, "RouteHits", out var routeHealthHits) || TryGetProperty(root, "routeHits", out routeHealthHits))
            {
                var health = ReadRouteHealthSummary(routeHealthHits);
                MetricTiles.Add(new HistoryMetricTile("路由健康度", $"{health.OpenCircuits} open / {health.CoolingRoutes} cooling", "熔断", health.OpenCircuits > 0 ? HistoryTones.Danger : HistoryTones.Healthy));
                AddNumberMetricTileIfPresent("模型冷却", health.ModelCooldowns, "路由模型", HistoryTones.Warning);
            }
            if (TryGetProperty(root, "ModelPoolSummary", out var modelPoolSummary) ||
                TryGetProperty(root, "modelPoolSummary", out modelPoolSummary))
            {
                var poolHealth = ReadModelPoolHealthSummary(modelPoolSummary);
                MetricTiles.Add(new HistoryMetricTile("池健康度", $"{FormatCompactNumber(poolHealth.HealthyMembers)}/{FormatCompactNumber(poolHealth.MemberCount)}", "健康/总数", poolHealth.OpenCircuitMembers > 0 ? HistoryTones.Warning : HistoryTones.Healthy));
                AddNumberMetricTileIfPresent("池熔断", poolHealth.OpenCircuitMembers, "打开熔断", HistoryTones.Danger);
                AddMetricTileIfPresent("最佳延迟", poolHealth.BestLatencyMs, "模型池", HistoryTones.Accent);
            }
            if (TryGetProperty(root, "Management", out var management) || TryGetProperty(root, "management", out management))
            {
                var allowRemote = TryGetBool(management, "AllowRemote") ?? TryGetBool(management, "allowRemote") ?? false;
                var protectedText = (TryGetBool(management, "SecretConfigured") ?? TryGetBool(management, "secretConfigured") ?? false)
                    ? "有保护"
                    : "仅本地";
                MetricTiles.Add(new HistoryMetricTile("管理", allowRemote ? "远程" : "本地", protectedText, allowRemote ? HistoryTones.Warning : HistoryTones.Healthy));
            }
            if (TryGetProperty(root, "RuntimeConfig", out var runtimeConfig) || TryGetProperty(root, "runtimeConfig", out runtimeConfig))
            {
                AddNumberMetricTileIfPresent("限流", TryGetDouble(runtimeConfig, "RateLimitPerMinute") ?? TryGetDouble(runtimeConfig, "rateLimitPerMinute"), "req/min", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("并发", TryGetDouble(runtimeConfig, "MaxConcurrency") ?? TryGetDouble(runtimeConfig, "maxConcurrency"), "并行", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("缓存 TTL", TryGetDouble(runtimeConfig, "CacheTtlSeconds") ?? TryGetDouble(runtimeConfig, "cacheTtlSeconds"), "秒", HistoryTones.Healthy);
                AddNumberMetricTileIfPresent("上游超时", TryGetDouble(runtimeConfig, "UpstreamTimeoutSeconds") ?? TryGetDouble(runtimeConfig, "upstreamTimeoutSeconds"), "秒", HistoryTones.Warning);
                AddNumberMetricTileIfPresent("请求重试", TryGetDouble(runtimeConfig, "RequestRetry") ?? TryGetDouble(runtimeConfig, "requestRetry"), "故障切换", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("重试间隔", TryGetDouble(runtimeConfig, "MaxRetryIntervalSeconds") ?? TryGetDouble(runtimeConfig, "maxRetryIntervalSeconds"), "秒", HistoryTones.Warning);
                if ((TryGetBool(runtimeConfig, "EnableFallback") ?? TryGetBool(runtimeConfig, "enableFallback")) is { } fallbackEnabled)
                {
                    MetricTiles.Add(new HistoryMetricTile("故障切换模式", fallbackEnabled ? "已启用" : "已禁用", "运行时", fallbackEnabled ? HistoryTones.Healthy : HistoryTones.Warning));
                }
            }
            if (TryGetProperty(root, "ConfiguredRoutes", out var configuredRoutes) ||
                TryGetProperty(root, "configuredRoutes", out configuredRoutes))
            {
                AddNumberMetricTileIfPresent("已配置路由", ReadArrayLength(root, "ConfiguredRoutes", "configuredRoutes"), "保存策略", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("已启用路由", CountConfiguredRoutes(configuredRoutes, enabled: true), "保存策略", HistoryTones.Healthy);
                AddNumberMetricTileIfPresent("重试路由", CountConfiguredRoutesWithRetry(configuredRoutes), "故障切换", HistoryTones.Accent);
                AddNumberMetricTileIfPresent("冷却路由", CountConfiguredRoutesWithCooldown(configuredRoutes), "故障切换", HistoryTones.Warning);
            }
        }

        if (TryGetBatchRankingArray(root, out var sites))
        {
            var siteItems = sites.EnumerateArray().ToArray();
            if (siteItems.Length > 0)
            {
                MetricTiles.Add(new HistoryMetricTile("Sites", siteItems.Length.ToString(), "batch", HistoryTones.Accent));
                var best = siteItems
                    .Select(site => new
                    {
                        Name = TryGetString(site, "Name") ?? TryGetString(site, "name") ?? "--",
                        Score = ReadBatchRankingScore(site) ?? 0
                    })
                    .OrderByDescending(site => site.Score)
                    .First();
                MetricTiles.Add(new HistoryMetricTile("Best site", best.Name, $"{best.Score:F1}", HistoryTones.Healthy));

                var bestThroughput = siteItems
                    .Select(ReadBatchRankingThroughput)
                    .Where(static value => value.HasValue)
                    .Select(static value => value!.Value)
                    .DefaultIfEmpty(0)
                    .Max();
                if (bestThroughput > 0)
                {
                    MetricTiles.Add(new HistoryMetricTile("Best throughput", $"{bestThroughput:F1}", "tok/s", HistoryTones.Accent));
                }
            }
        }

        if (TryGetBatchSection(root, out var batchSection))
        {
            var recommendation = TryGetString(batchSection, "recommendationSummary") ??
                                 TryGetString(batchSection, "RecommendationSummary") ??
                                 TryGetString(batchSection, "summary") ??
                                 TryGetString(batchSection, "Summary");
            if (!string.IsNullOrWhiteSpace(recommendation))
            {
                MetricTiles.Add(new HistoryMetricTile("Batch note", CompactTileDelta(recommendation), "WPF report", HistoryTones.Warning));
            }
        }

        if (TryGetProxyTrendsSection(root, out var proxyTrends))
        {
            var target = ReadReportSectionText(proxyTrends, "trendTarget", "target", "baseUrl");
            var trend24h = ReadReportSectionText(proxyTrends, "trend24h", "summary24h", "summary");
            var detail = ReadReportSectionText(proxyTrends, "detail");
            var hasTrendChart = TryGetBool(proxyTrends, "hasTrendChart");

            AddNetworkMetricTile("Trend target", target, "WPF trend", HistoryTones.Accent);
            AddNetworkMetricTile("Trend 24h", trend24h is null ? null : CompactTileDelta(trend24h), "WPF trend", HistoryTones.Healthy);
            AddNetworkMetricTile("Trend detail", detail is null ? null : CompactTileDelta(detail), "WPF report", HistoryTones.Accent);
            if (hasTrendChart.HasValue)
            {
                MetricTiles.Add(new HistoryMetricTile(
                    "Trend chart",
                    hasTrendChart.Value ? "Available" : "Missing",
                    "WPF artifact",
                    hasTrendChart.Value ? HistoryTones.Healthy : HistoryTones.Warning));
            }
        }

        if (TryGetProxyConcurrencySection(root, out var proxyConcurrency))
        {
            var baseUrl = ReadReportSectionText(proxyConcurrency, "baseUrl", "target");
            var model = ReadReportSectionText(proxyConcurrency, "model");
            var summary = ReadReportSectionText(proxyConcurrency, "summary");
            var detail = ReadReportSectionText(proxyConcurrency, "detail", "error");
            var hasChart = TryGetBool(proxyConcurrency, "hasChart");

            AddNetworkMetricTile("Concurrency target", baseUrl, "WPF concurrency", HistoryTones.Accent);
            AddNetworkMetricTile("Concurrency model", model, "WPF concurrency", HistoryTones.Accent);
            AddNumberMetricTileIfPresent("Stable concurrency", TryGetDouble(proxyConcurrency, "stableConcurrencyLimit"), "safe parallel", HistoryTones.Healthy);
            AddNumberMetricTileIfPresent("Rate-limit at", TryGetDouble(proxyConcurrency, "rateLimitStartConcurrency"), "429 boundary", HistoryTones.Warning);
            AddNumberMetricTileIfPresent("High risk at", TryGetDouble(proxyConcurrency, "highRiskConcurrency"), "review boundary", HistoryTones.Danger);
            AddNetworkMetricTile("Concurrency note", (summary ?? detail) is { } note ? CompactTileDelta(note) : null, "WPF report", HistoryTones.Warning);
            if (hasChart.HasValue)
            {
                MetricTiles.Add(new HistoryMetricTile(
                    "Concurrency chart",
                    hasChart.Value ? "Available" : "Missing",
                    "WPF artifact",
                    hasChart.Value ? HistoryTones.Healthy : HistoryTones.Warning));
            }
        }

        if (TryGetProxyStabilitySection(root, out var proxyStability))
        {
            var healthScore = TryGetDouble(proxyStability, "healthScore");
            var healthLabel = ReadReportSectionText(proxyStability, "healthLabel");
            var insight = ReadReportSectionText(proxyStability, "insightSummary", "summary", "detail");
            AddNumberMetricTileIfPresent("Stability score", healthScore, healthLabel ?? "WPF stability", HistoryTones.Healthy);
            AddNetworkMetricTile("Health", healthLabel, "WPF stability", HistoryTones.Healthy);
            AddPercentMetricTileIfPresent("Full success", TryGetDouble(proxyStability, "fullSuccessRate"), "stability", HistoryTones.Healthy);
            AddPercentMetricTileIfPresent("Chat success", TryGetDouble(proxyStability, "chatSuccessRate"), "stability", HistoryTones.Accent);
            AddPercentMetricTileIfPresent("Stream success", TryGetDouble(proxyStability, "streamSuccessRate"), "stability", HistoryTones.Accent);
            AddPercentMetricTileIfPresent("Semantic", TryGetDouble(proxyStability, "semanticStabilityRate"), "stability", HistoryTones.Warning);
            AddMetricTileIfPresent("Avg chat", TryGetDouble(proxyStability, "averageChatLatencyMs"), "stability", HistoryTones.Accent);
            AddMetricTileIfPresent("Avg TTFT", TryGetDouble(proxyStability, "averageTtftMs"), "stability", HistoryTones.Warning);
            AddNumberMetricTileIfPresent("Max failures", TryGetDouble(proxyStability, "maxConsecutiveFailures"), "stability", HistoryTones.Danger);
            AddNetworkMetricTile("CDN stability", ReadReportSectionText(proxyStability, "cdnStabilitySummary"), "edge", HistoryTones.Accent);
            AddNetworkMetricTile("Stability note", insight is null ? null : CompactTileDelta(insight), "WPF report", HistoryTones.Warning);
            var edgeSignatures = TryGetDouble(proxyStability, "distinctEdgeSignatureCount");
            var edgeSwitches = TryGetDouble(proxyStability, "edgeSwitchCount");
            if (edgeSignatures.HasValue || edgeSwitches.HasValue)
            {
                MetricTiles.Add(new HistoryMetricTile(
                    "Edges",
                    $"{FormatCompactNumber(edgeSignatures ?? 0)}/{FormatCompactNumber(edgeSwitches ?? 0)}",
                    "signatures/switches",
                    (edgeSwitches ?? 0) > 0 ? HistoryTones.Warning : HistoryTones.Healthy));
            }
        }

        if (TryGetProxyModelCatalogSection(root, out var proxyModelCatalog))
        {
            AddNetworkMetricTile("Model catalog", ReadReportSectionText(proxyModelCatalog, "summary"), "WPF proxy", HistoryTones.Accent);
            AddNetworkMetricTile("Catalog detail", ReadReportSectionText(proxyModelCatalog, "detail"), "WPF report", HistoryTones.Accent);
        }

        if (TryGetProxySingleSection(root, out var proxySingle))
        {
            AddNetworkMetricTile("Single verdict", ReadReportSectionText(proxySingle, "verdict"), "WPF single", HistoryTones.Healthy);
            AddNetworkMetricTile("Primary issue", ReadReportSectionText(proxySingle, "primaryIssue"), "WPF single", HistoryTones.Danger);
            AddNetworkMetricTile("CDN", ReadReportSectionText(proxySingle, "cdnSummary", "cdnProvider"), "WPF single", HistoryTones.Accent);
            AddNetworkMetricTile("Trace", ReadReportSectionText(proxySingle, "traceability", "traceId", "requestId"), "WPF single", HistoryTones.Accent);
            AddNetworkMetricTile("Capability note", ReadReportSectionText(proxySingle, "capabilityMatrixSummary"), "WPF report", HistoryTones.Warning);
            if (TryGetProxySingleScenarioResults(root, out var scenarioResults))
            {
                AddNumberMetricTileIfPresent("Scenarios", (double)scenarioResults.GetArrayLength(), "WPF single", HistoryTones.Accent);
            }

            if (TryGetProperty(proxySingle, "longStreaming", out var longStreaming) && longStreaming.ValueKind == JsonValueKind.Object)
            {
                AddNetworkMetricTile("Long stream", ReadReportSectionText(longStreaming, "Summary", "Error"), "WPF single", HistoryTones.Healthy);
                AddMetricTileIfPresent("Stream first token", TryGetDouble(longStreaming, "firstTokenLatencyMs"), "WPF single", HistoryTones.Warning);
            }
            else
            {
                AddNetworkMetricTile("Long stream", ReadReportSectionText(proxySingle, "longStreamingSummary"), "WPF single", HistoryTones.Healthy);
            }
        }

        if (TryGetClientApiSection(root, out var clientApi))
        {
            AddNetworkMetricTile("Client API", ReadReportSectionText(clientApi, "summary"), "WPF diagnostics", HistoryTones.Accent);
            AddNetworkMetricTile("Client API detail", ReadReportSectionText(clientApi, "detail"), "WPF report", HistoryTones.Accent);
            AddNetworkMetricTile("Client checked", ReadReportSectionText(clientApi, "checkedAt"), "timestamp", HistoryTones.Warning);
            AddNumberMetricTileIfPresent("Installed clients", TryGetDouble(clientApi, "installedCount"), "client API", HistoryTones.Healthy);
            AddNumberMetricTileIfPresent("Configured clients", TryGetDouble(clientApi, "configuredCount"), "client API", HistoryTones.Accent);
            AddNumberMetricTileIfPresent("Reachable clients", TryGetDouble(clientApi, "reachableCount"), "client API", HistoryTones.Healthy);
        }

        if (TryGetStunSection(root, out var stun))
        {
            AddNetworkMetricTile("STUN", ReadReportSectionText(stun, "summary"), "WPF network", HistoryTones.Accent);
            AddNetworkMetricTile("NAT", ReadReportSectionText(stun, "natType"), "STUN", HistoryTones.Healthy);
            if (TryGetDouble(stun, "confidence") is { } confidence)
            {
                MetricTiles.Add(new HistoryMetricTile("NAT confidence", FormatPercentValue(confidence), "STUN", HistoryTones.Accent));
            }

            AddNetworkMetricTile("STUN coverage", ReadReportSectionText(stun, "coverageSummary"), "WPF network", HistoryTones.Warning);
            AddNetworkMetricTile("STUN review", ReadReportSectionText(stun, "reviewRecommendation"), "WPF network", HistoryTones.Warning);
        }

        if (TryGetUnlockCatalogSection(root, out var unlock))
        {
            AddNetworkMetricTile("Web API", ReadReportSectionText(unlock, "summary"), "WPF unlock", HistoryTones.Accent);
            AddNetworkMetricTile("Unlock catalog", ReadReportSectionText(unlock, "unlockCatalogSummary"), "WPF unlock", HistoryTones.Healthy);
            AddNetworkMetricTile("Unlock detail", ReadReportSectionText(unlock, "unlockCatalogDetail"), "WPF report", HistoryTones.Accent);
            if (TryGetProperty(unlock, "semanticCounts", out var semanticCounts) && semanticCounts.ValueKind == JsonValueKind.Object)
            {
                AddNumberMetricTileIfPresent("Unlock ready", TryGetDouble(semanticCounts, "ReadyCount"), "catalog", HistoryTones.Healthy);
                AddNumberMetricTileIfPresent("Auth required", TryGetDouble(semanticCounts, "AuthRequiredCount"), "catalog", HistoryTones.Warning);
                AddNumberMetricTileIfPresent("Region blocked", TryGetDouble(semanticCounts, "RegionRestrictedCount"), "catalog", HistoryTones.Danger);
                AddNumberMetricTileIfPresent("Review unlock", TryGetDouble(semanticCounts, "ReviewRequiredCount"), "catalog", HistoryTones.Warning);
                AddNumberMetricTileIfPresent("Unlock total", TryGetDouble(semanticCounts, "TotalCount"), "catalog", HistoryTones.Accent);
            }
        }

        if (TryGetLegacyRouteSection(root, out var legacyRoute))
        {
            AddNetworkMetricTile("Route", ReadReportSectionText(legacyRoute, "summary"), "WPF route", HistoryTones.Accent);
            AddNetworkMetricTile("Route geo", ReadReportSectionText(legacyRoute, "geoSummary"), "WPF route", HistoryTones.Healthy);
            AddNetworkMetricTile("Route hops", ReadReportSectionText(legacyRoute, "hopSummary"), "WPF route", HistoryTones.Warning);
            AddNetworkMetricTile("Route map", ReadReportSectionText(legacyRoute, "mapSummary"), "WPF route", HistoryTones.Accent);
            if (TryGetBool(legacyRoute, "hasMapImage") is { } hasMapImage)
            {
                MetricTiles.Add(new HistoryMetricTile(
                    "Route map image",
                    hasMapImage ? "Available" : "Missing",
                    "WPF artifact",
                    hasMapImage ? HistoryTones.Healthy : HistoryTones.Warning));
            }
        }

        if (TryGetLegacyPortScanSection(root, out var legacyPortScan))
        {
            AddNetworkMetricTile("Port scan", ReadReportSectionText(legacyPortScan, "summary"), "WPF port scan", HistoryTones.Accent);
            AddNetworkMetricTile("Batch scan", ReadReportSectionText(legacyPortScan, "batchSummary"), "WPF port scan", HistoryTones.Accent);
            AddNetworkMetricTile("Port export", ReadReportSectionText(legacyPortScan, "exportSummary"), "WPF report", HistoryTones.Warning);
            AddNetworkMetricTile("Scan target", ReadReportSectionText(legacyPortScan, "target"), "WPF port scan", HistoryTones.Accent);
            AddNetworkMetricTile("Scan profile", ReadReportSectionText(legacyPortScan, "profileName", "profileKey"), "profile", HistoryTones.Healthy);
            AddNetworkMetricTile("Scan ports", ReadReportSectionText(legacyPortScan, "effectivePortsText", "customPortsText") is { } ports ? CompactTileDelta(ports) : null, "port set", HistoryTones.Accent);
            AddNetworkMetricTile("Resolved", ReadStringArraySummary(legacyPortScan, "resolvedAddresses") ?? ReadStringArraySummary(legacyPortScan, "systemResolvedAddresses"), "DNS", HistoryTones.Healthy);
            AddNetworkMetricTile("Scan progress", ReadReportSectionText(legacyPortScan, "progressSummary"), "WPF port scan", HistoryTones.Warning);
            AddNumberMetricTileIfPresent("Open ports", TryGetDouble(legacyPortScan, "openPortCount"), "WPF port scan", HistoryTones.Healthy);
            AddNumberMetricTileIfPresent("Open endpoints", TryGetDouble(legacyPortScan, "openEndpointCount"), "WPF port scan", HistoryTones.Healthy);
            AddNumberMetricTileIfPresent("Attempted endpoints", TryGetDouble(legacyPortScan, "attemptedEndpointCount"), "WPF port scan", HistoryTones.Accent);
            AddNumberMetricTileIfPresent("Findings", ReadArrayLength(legacyPortScan, "findings"), "WPF port scan", HistoryTones.Warning);
            AddNumberMetricTileIfPresent("Batch targets", ReadArrayLength(legacyPortScan, "batchRows"), "WPF port scan", HistoryTones.Accent);
            AddNumberMetricTileIfPresent("Batch findings", ReadArrayLength(legacyPortScan, "batchFindings"), "WPF port scan", HistoryTones.Accent);
        }

        if (TryGetLegacySpeedSection(root, out var legacySpeed))
        {
            AddNetworkMetricTile("Speed test", ReadReportSectionText(legacySpeed, "summary") is { } summary ? CompactTileDelta(summary) : null, "WPF speed", HistoryTones.Accent);
            AddNetworkMetricTile("Speed latency", ReadReportSectionText(legacySpeed, "latencyDetail") is { } latency ? CompactTileDelta(latency) : null, "latency samples", HistoryTones.Healthy);
            AddNetworkMetricTile("Speed transfer", ReadReportSectionText(legacySpeed, "transferDetail") is { } transfer ? CompactTileDelta(transfer) : null, "transfer samples", HistoryTones.Accent);
            AddNetworkMetricTile("Packet loss", ReadReportSectionText(legacySpeed, "packetLossDetail") is { } packetLoss ? CompactTileDelta(packetLoss) : null, "WPF speed", HistoryTones.Warning);
        }

        if (TryGetLegacySplitRoutingSection(root, out var legacySplitRouting))
        {
            AddNetworkMetricTile("Split routing", ReadReportSectionText(legacySplitRouting, "summary") is { } summary ? CompactTileDelta(summary) : null, "WPF routing", HistoryTones.Accent);
            AddNetworkMetricTile("IP insight", ReadReportSectionText(legacySplitRouting, "ipInsightSummary") is { } insight ? CompactTileDelta(insight) : null, "public IP", HistoryTones.Healthy);
            AddNetworkMetricTile("Adapters", ReadReportSectionText(legacySplitRouting, "adapterSummary") is { } adapters ? CompactTileDelta(adapters) : null, "network", HistoryTones.Accent);
            AddNetworkMetricTile("Exit routing", ReadReportSectionText(legacySplitRouting, "exitSummary") is { } exits ? CompactTileDelta(exits) : null, "egress", HistoryTones.Warning);
            AddNetworkMetricTile("DNS routing", ReadReportSectionText(legacySplitRouting, "dnsSummary") is { } dns ? CompactTileDelta(dns) : null, "resolver", HistoryTones.Accent);
            AddNetworkMetricTile("Reachability", ReadReportSectionText(legacySplitRouting, "reachabilitySummary") is { } reachability ? CompactTileDelta(reachability) : null, "HTTPS", HistoryTones.Healthy);
        }

        if (TryGetProperty(root, "Scores", out var scores) || TryGetProperty(root, "scores", out scores))
        {
            AddScoreMetric(scores, "Overall", "Safety");
            AddScoreMetric(scores, "CodexFit", "Codex");
            AddScoreMetric(scores, "AgentFit", "Agent");
            AddScoreMetric(scores, "RagFit", "RAG");
        }
    }

    private void ResetSelectedReportSummary()
    {
        HistoryTotalRequestsSummary = "\u603B\u8BF7\u6C42 0";
        HistorySuccessRequestsSummary = "\u6210\u529F\u8BF7\u6C42 0";
        HistoryErrorRateSummary = "\u9519\u8BEF\u7387 0.00%";
        HistoryP50Summary = "P50 0 ms";
        HistoryP95Summary = "P95 0 ms";
        HistoryP99Summary = "P99 0 ms";
        HistoryInputThroughputSummary = "Input 0 tokens/s";
        HistoryOutputThroughputSummary = "Output 0 tokens/s";
        HistoryTotalInputSummary = "\u603B\u8F93\u5165 0";
        HistoryTotalOutputSummary = "\u603B\u8F93\u51FA 0";
        HistoryTimeoutSummary = "Timeouts 0";
        HistoryRateLimitSummary = "429 count 0";
        HistoryServerErrorSummary = "5xx count 0";
    }

    private void ResetSelectedReportMetadata()
    {
        SelectedReportEndpointSummary = "--";
        SelectedReportProtocolSummary = "--";
        SelectedReportExitSummary = "0";
        SelectedReportProxyModeSummary = "--";
        SelectedReportModelSummary = "--";
        SelectedReportInputTokensSummary = "0";
        SelectedReportOutputTokensSummary = "0";
        SelectedReportPromptCacheTokensSummary = "0";
        SelectedReportCacheHitRateSummary = "0.0%";
        SelectedReportOutputTokenSourceSummary = "--";
        SelectedReportProtocolSupportSummary = "--";
        SelectedReportAttachmentTitle = "Attachments";
        HasSelectedReportRouteMapEvidence = false;
        HasSelectedReportRouteMapImage = false;
        SelectedReportRouteMapImagePath = "";
        SelectedReportRouteMapSummary = "No route map recorded";
        SelectedReportRouteMapGeoSummary = "";
    }

    private async Task PopulateHistoryAggregateSummaryAsync(IReadOnlyList<HistoryReportSummary> summaries)
    {
        ResetSelectedReportSummary();
        if (summaries.Count == 0)
        {
            return;
        }

        HistoryAggregateAccumulator aggregate = new();
        foreach (var summary in summaries)
        {
            var report = await _repository.GetAsync(summary.RunId);
            if (report is null)
            {
                continue;
            }

            using var payload = TryParsePayload(report.PayloadJson);
            AccumulateHistoryReport(aggregate, report, payload?.RootElement);
        }

        ApplyHistoryAggregateSummary(aggregate);
    }

    private static void AccumulateHistoryReport(
        HistoryAggregateAccumulator aggregate,
        HistoryReport report,
        JsonElement? root)
    {
        var counters = ReadRequestCounters(report, root);
        aggregate.TotalRequests += counters.Total;
        aggregate.SuccessRequests += counters.Success;
        aggregate.FailedRequests += counters.Failed;

        var p50 = ReadLatencySummary(root, "P50LatencyMs", "p50LatencyMs", "LatencyP50", "latencyP50") ?? report.DurationMs;
        if (p50.HasValue)
        {
            aggregate.P50Samples.Add(p50.Value);
        }

        if (ReadLatencySummary(root, "P95LatencyMs", "p95LatencyMs", "LatencyP95", "latencyP95") is { } p95)
        {
            aggregate.P95Samples.Add(p95);
        }

        if (ReadLatencySummary(root, "P99LatencyMs", "p99LatencyMs", "LatencyP99", "latencyP99") is { } p99)
        {
            aggregate.P99Samples.Add(p99);
        }

        if (ReadThroughputSummary(root, "InputTokensPerSecond", "inputTokensPerSecond", "EndToEndTokensPerSecond") is { } inputThroughput)
        {
            aggregate.InputThroughputSamples.Add(inputThroughput);
        }

        if (ReadThroughputSummary(root, "OutputTokensPerSecond", "outputTokensPerSecond", "MedianOutputTokensPerSecond", "medianOutputTokensPerSecond") is { } outputThroughput)
        {
            aggregate.OutputThroughputSamples.Add(outputThroughput);
        }

        if (root.HasValue)
        {
            aggregate.TotalInputTokens += ReadTotalInputTokens(root.Value) ?? 0;
            aggregate.TotalOutputTokens += ReadTotalOutputTokens(root.Value) ?? 0;
            aggregate.TimeoutCount += TryGetDouble(root.Value, "TimeoutCount") ?? TryGetDouble(root.Value, "timeoutCount") ?? 0;
            aggregate.RateLimitedRequests += TryGetDouble(root.Value, "RateLimitedRequests") ?? TryGetDouble(root.Value, "rateLimitedRequests") ?? 0;
            aggregate.ServerErrorCount += TryGetDouble(root.Value, "ServerErrorCount") ?? TryGetDouble(root.Value, "serverErrorCount") ?? 0;

            if (TryGetProperty(root.Value, "throughput", out var throughput))
            {
                aggregate.TotalInputTokens += TryGetDouble(throughput, "TotalInputTokens") ?? TryGetDouble(throughput, "totalInputTokens") ?? 0;
                aggregate.TotalOutputTokens += TryGetDouble(throughput, "TotalOutputTokens") ?? TryGetDouble(throughput, "totalOutputTokens") ?? 0;
            }
        }
    }

    private static (double Total, double Success, double Failed) ReadRequestCounters(
        HistoryReport report,
        JsonElement? root)
    {
        if (!root.HasValue)
        {
            return (0, 0, 0);
        }

        var value = root.Value;
        var total = TryGetDouble(value, "TotalRequests") ?? TryGetDouble(value, "totalRequests");
        var success = TryGetDouble(value, "SuccessRequests") ?? TryGetDouble(value, "successRequests");
        var failed = TryGetDouble(value, "FailedRequests") ?? TryGetDouble(value, "failedRequests");

        var successRate = TryGetDouble(value, "SuccessRate") ?? TryGetDouble(value, "successRate");
        if (total is > 0 && !success.HasValue && successRate.HasValue)
        {
            success = Math.Round(total.Value * successRate.Value / 100.0);
        }

        var schema = TryGetString(value, "Schema") ?? TryGetString(value, "schema") ?? string.Empty;
        if (!total.HasValue && schema.StartsWith("application-access-batch", StringComparison.OrdinalIgnoreCase))
        {
            total = TryGetDouble(value, "TargetCount") ?? TryGetDouble(value, "targetCount") ?? 0;
            success = TryGetDouble(value, "SucceededTargetCount") ?? TryGetDouble(value, "succeededTargetCount") ?? 0;
            failed = TryGetDouble(value, "FailedTargetCount") ?? TryGetDouble(value, "failedTargetCount") ?? Math.Max(0, total.Value - success.Value);
        }

        if (!total.HasValue && schema.StartsWith("application-access", StringComparison.OrdinalIgnoreCase))
        {
            var succeeded = TryGetBool(value, "Succeeded") ?? TryGetBool(value, "succeeded") ?? false;
            total = 1;
            success = succeeded ? 1 : 0;
            failed = succeeded ? 0 : 1;
        }

        if (!total.HasValue && schema.StartsWith("model-chat-multi", StringComparison.OrdinalIgnoreCase))
        {
            total = TryGetDouble(value, "ResultCount") ?? TryGetDouble(value, "resultCount");
            success = TryGetDouble(value, "SuccessCount") ?? TryGetDouble(value, "successCount");
            failed = TryGetDouble(value, "FailedCount") ?? TryGetDouble(value, "failedCount");
            if ((!total.HasValue || !success.HasValue) &&
                TryGetProperty(value, "Results", out var modelResults) &&
                modelResults.ValueKind == JsonValueKind.Array)
            {
                var items = modelResults.EnumerateArray().ToArray();
                total ??= items.Length;
                success ??= items.Count(static item => TryGetBool(item, "Succeeded") ?? TryGetBool(item, "succeeded") ?? false);
                failed ??= Math.Max(0, total.Value - success.Value);
            }
        }

        if (!total.HasValue && schema.StartsWith("network-review", StringComparison.OrdinalIgnoreCase))
        {
            var succeeded = IsNetworkReviewPayloadSuccessful(report, value);
            total = 1;
            success = succeeded ? 1 : 0;
            failed = succeeded ? 0 : 1;
        }

        if (!total.HasValue && schema.StartsWith("model-chat", StringComparison.OrdinalIgnoreCase))
        {
            var succeeded = report.Score is null or >= 60;
            total = 1;
            success = succeeded ? 1 : 0;
            failed = succeeded ? 0 : 1;
        }

        if ((!total.HasValue || total <= 0 || !success.HasValue) &&
            (TryGetProperty(value, "RouteHits", out var routeHits) || TryGetProperty(value, "routeHits", out routeHits)) &&
            routeHits.ValueKind == JsonValueKind.Array)
        {
            var routeCounters = ReadRouteHitCounters(routeHits);
            if (!total.HasValue || total <= 0)
            {
                total = routeCounters.Total;
            }

            if (!success.HasValue || (success <= 0 && routeCounters.Success > 0))
            {
                success = routeCounters.Success;
            }

            if (!failed.HasValue || (failed <= 0 && routeCounters.Failed > 0))
            {
                failed = routeCounters.Failed;
            }
        }

        if ((!total.HasValue || !success.HasValue) &&
            TryGetProperty(value, "scenarios", out var scenarios) &&
            scenarios.ValueKind == JsonValueKind.Array)
        {
            var items = scenarios.EnumerateArray().ToArray();
            total ??= items.Length;
            success ??= items.Count(static scenario =>
                TryGetBool(scenario, "Success") ?? TryGetBool(scenario, "success") ?? false);
        }

        if ((!total.HasValue || !success.HasValue) &&
            TryGetProperty(value, "sites", out var sites) &&
            sites.ValueKind == JsonValueKind.Array)
        {
            var items = sites.EnumerateArray().ToArray();
            total ??= items.Length;
            var successRates = items
                .Select(static site => TryGetDouble(site, "SuccessRate") ?? TryGetDouble(site, "successRate"))
                .Where(static item => item.HasValue)
                .Select(static item => item!.Value)
                .ToArray();
            if (!success.HasValue && successRates.Length > 0)
            {
                success = successRates.Count(static rate => rate >= 99);
            }
        }

        total ??= 0;
        success ??= 0;
        failed ??= Math.Max(0, total.Value - success.Value);
        return (Math.Max(0, total.Value), Math.Max(0, success.Value), Math.Max(0, failed.Value));
    }

    private static bool IsNetworkReviewPayloadSuccessful(HistoryReport report, JsonElement payload)
    {
        if (report.Score is < 60)
        {
            return false;
        }

        var summary = TryGetString(payload, "Summary") ?? TryGetString(payload, "summary") ?? string.Empty;
        if (summary.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryGetProperty(payload, "Result", out var result) &&
            result.ValueKind == JsonValueKind.Object &&
            (TryGetString(result, "Error") ?? TryGetString(result, "error")) is { Length: > 0 })
        {
            return false;
        }

        return true;
    }

    private void ApplyHistoryAggregateSummary(HistoryAggregateAccumulator aggregate)
    {
        var errorRate = aggregate.TotalRequests > 0
            ? aggregate.FailedRequests / aggregate.TotalRequests * 100.0
            : 0;

        HistoryTotalRequestsSummary = $"\u603B\u8BF7\u6C42 {FormatCompactNumber(aggregate.TotalRequests)}";
        HistorySuccessRequestsSummary = $"\u6210\u529F\u8BF7\u6C42 {FormatCompactNumber(aggregate.SuccessRequests)}";
        HistoryErrorRateSummary = $"\u9519\u8BEF\u7387 {errorRate:F2}%";
        HistoryP50Summary = $"P50 {FormatMillisecondsZero(ReadMedian(aggregate.P50Samples))}";
        HistoryP95Summary = $"P95 {FormatMillisecondsZero(ReadMedian(aggregate.P95Samples))}";
        HistoryP99Summary = $"P99 {FormatMillisecondsZero(ReadMedian(aggregate.P99Samples))}";
        HistoryInputThroughputSummary = $"Input {ReadAverage(aggregate.InputThroughputSamples):F1} tokens/s";
        HistoryOutputThroughputSummary = $"Output {ReadAverage(aggregate.OutputThroughputSamples):F1} tokens/s";
        HistoryTotalInputSummary = $"\u603B\u8F93\u5165 {FormatCompactNumber(aggregate.TotalInputTokens)}";
        HistoryTotalOutputSummary = $"\u603B\u8F93\u51FA {FormatCompactNumber(aggregate.TotalOutputTokens)}";
        HistoryTimeoutSummary = $"Timeouts {FormatCompactNumber(aggregate.TimeoutCount)}";
        HistoryRateLimitSummary = $"429 count {FormatCompactNumber(aggregate.RateLimitedRequests)}";
        HistoryServerErrorSummary = $"5xx count {FormatCompactNumber(aggregate.ServerErrorCount)}";
    }

    private void PopulateSelectedReportMetadata(HistoryReport report, JsonElement? root)
    {
        SelectedReportEndpointSummary = string.IsNullOrWhiteSpace(report.Endpoint) ? "--" : report.Endpoint;
        SelectedReportProxyModeSummary = TranslateType(report.TestType);

        if (!root.HasValue)
        {
            return;
        }

        var payload = root.Value;
        var detailPayload = TryGetProperty(payload, "Result", out var nestedResult) && nestedResult.ValueKind == JsonValueKind.Object
            ? nestedResult
            : payload;
        SelectedReportProtocolSummary = FormatWireApiSummary(
            TryGetString(payload, "PreferredWireApi") ??
            TryGetString(payload, "preferredWireApi") ??
            TryGetString(payload, "WireApi") ??
            TryGetString(payload, "wireApi") ??
            TryGetString(payload, "Protocol") ??
            TryGetString(payload, "protocol"));

        if (SelectedReportProtocolSummary == "--" &&
            ((TryGetProperty(payload, "RouteHits", out var routeHits) && routeHits.ValueKind == JsonValueKind.Array && routeHits.GetArrayLength() > 0) ||
             (TryGetProperty(payload, "routeHits", out var routeHitsLower) && routeHitsLower.ValueKind == JsonValueKind.Array && routeHitsLower.GetArrayLength() > 0)))
        {
            SelectedReportProtocolSummary = "\u900F\u660E\u4EE3\u7406";
        }

        if ((TryGetString(payload, "Schema") ?? TryGetString(payload, "schema"))?.StartsWith("network-review", StringComparison.OrdinalIgnoreCase) == true &&
            TryGetProperty(payload, "Proxy", out var proxyContext))
        {
            SelectedReportProxyModeSummary = TryGetString(proxyContext, "Mode") ?? SelectedReportProxyModeSummary;
            SelectedReportProtocolSummary = TryGetString(proxyContext, "ProtocolSummary") ?? "\u7F51\u7EDC\u590D\u6838";
            SelectedReportModelSummary = TryGetString(proxyContext, "ModelPool") ?? "--";
            SelectedReportEndpointSummary = TryGetString(payload, "Endpoint") ?? SelectedReportEndpointSummary;
        }

        if ((TryGetString(payload, "Schema") ?? TryGetString(payload, "schema"))?.StartsWith("application-access", StringComparison.OrdinalIgnoreCase) == true)
        {
            SelectedReportProtocolSummary = "\u5E94\u7528\u63A5\u5165";
            SelectedReportProxyModeSummary = TryGetString(payload, "TargetName") ?? TryGetString(payload, "targetName") ?? SelectedReportProxyModeSummary;
            SelectedReportEndpointSummary = TryGetString(payload, "Endpoint") ?? TryGetString(payload, "BaseUrl") ?? SelectedReportEndpointSummary;
            SelectedReportModelSummary = TryGetString(payload, "Model") ?? TryGetString(payload, "model") ?? "--";
        }

        if (IsModelChatMultiPayload(payload))
        {
            SelectedReportProtocolSummary = "\u591A\u6A21\u578B\u5BF9\u6BD4";
            SelectedReportModelSummary = ReadStringArraySummary(payload, "Models") ??
                                         ReadStringArraySummary(payload, "models") ??
                                         SelectedReportModelSummary;
        }

        SelectedReportExitSummary =
            TryGetString(payload, "LocalExitIp") ??
            TryGetString(payload, "localExitIp") ??
            TryGetString(payload, "ExitIp") ??
            TryGetString(payload, "exitIp") ??
            TryGetString(detailPayload, "PublicIp") ??
            TryGetString(detailPayload, "publicIp") ??
            TryGetString(detailPayload, "MappedAddress") ??
            TryGetString(detailPayload, "mappedAddress") ??
            "0";

        SelectedReportModelSummary =
            TryGetString(payload, "Model") ??
            TryGetString(payload, "model") ??
            TryGetString(payload, "ProbeModel") ??
            TryGetString(payload, "probeModel") ??
            ReadStringArraySummary(payload, "Models") ??
            ReadStringArraySummary(payload, "models") ??
            ReadFirstStringFromArray(payload, "ModelPoolSummary", "Name", "name", "ModelName", "modelName") ??
            ReadFirstStringFromArray(payload, "modelPoolSummary", "Name", "name", "ModelName", "modelName") ??
            ReadFirstStringFromArray(payload, "ConfiguredRoutes", "ModelFilter", "modelFilter") ??
            ReadFirstStringFromArray(payload, "configuredRoutes", "ModelFilter", "modelFilter") ??
            SelectedReportModelSummary;

        var inputTokens = ReadTotalInputTokens(payload);
        var outputTokens = ReadTotalOutputTokens(payload);
        var promptCacheTokens = ReadPromptCacheTokens(payload);
        SelectedReportInputTokensSummary = FormatCompactNumber(inputTokens ?? 0);
        SelectedReportOutputTokensSummary = FormatCompactNumber(outputTokens ?? 0);
        SelectedReportPromptCacheTokensSummary = FormatCompactNumber(promptCacheTokens ?? 0);
        SelectedReportCacheHitRateSummary = $"{ReadCacheHitRate(payload, inputTokens ?? 0, promptCacheTokens ?? 0):F1}%";
        SelectedReportOutputTokenSourceSummary = (TryGetBool(payload, "OutputTokenCountEstimated") ?? TryGetBool(payload, "outputTokenCountEstimated")) switch
        {
            true => "\u4F30\u7B97",
            false => "\u771F\u5B9E",
            null => "--"
        };
        SelectedReportProtocolSupportSummary = BuildProtocolSupportSummary(payload);
    }

    private void PopulateSelectedRouteMapEvidence(JsonElement root)
    {
        if (!TryGetProperty(root, "RouteMap", out var routeMap) ||
            routeMap.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var summary = TryGetString(routeMap, "Summary") ??
                      TryGetString(routeMap, "summary") ??
                      "Route map recorded";
        var geoSummary = TryGetString(routeMap, "GeoSummary") ??
                         TryGetString(routeMap, "geoSummary") ??
                         string.Empty;
        var imagePath = TryGetString(routeMap, "ImagePath") ??
                        TryGetString(routeMap, "imagePath") ??
                        TryGetString(routeMap, "MapImagePath") ??
                        TryGetString(routeMap, "mapImagePath") ??
                        string.Empty;
        var hasMapFlag = TryGetBool(routeMap, "HasMap") ??
                         TryGetBool(routeMap, "hasMap") ??
                         false;
        var hasImage = hasMapFlag &&
                       !string.IsNullOrWhiteSpace(imagePath) &&
                       File.Exists(imagePath);

        HasSelectedReportRouteMapEvidence = true;
        HasSelectedReportRouteMapImage = hasImage;
        SelectedReportRouteMapImagePath = imagePath;
        SelectedReportRouteMapSummary = string.IsNullOrWhiteSpace(summary)
            ? "No route map recorded"
            : summary;
        SelectedReportRouteMapGeoSummary = geoSummary;

        MetricTiles.Add(new HistoryMetricTile(
            "Route map",
            hasImage ? "Available" : hasMapFlag ? "Missing" : "No image",
            CompactTileDelta(SelectedReportRouteMapSummary),
            hasImage ? HistoryTones.Healthy : HistoryTones.Warning));
    }

    private void PopulateSelectedReportSummary(HistoryReport report, JsonElement? root)
    {
        var counters = ReadRequestCounters(report, root);
        double? totalRequests = counters.Total > 0 ? counters.Total : null;
        double? successRequests = counters.Total > 0 || counters.Success > 0 ? counters.Success : null;
        double? failedRequests = counters.Total > 0 || counters.Failed > 0 ? counters.Failed : null;

        if ((!totalRequests.HasValue || !successRequests.HasValue) &&
            root.HasValue &&
            (TryGetString(root.Value, "Schema") ?? TryGetString(root.Value, "schema"))?.StartsWith("application-access", StringComparison.OrdinalIgnoreCase) == true)
        {
            var succeeded = TryGetBool(root.Value, "Succeeded") ?? TryGetBool(root.Value, "succeeded") ?? false;
            totalRequests = 1;
            successRequests = succeeded ? 1 : 0;
            failedRequests = succeeded ? 0 : 1;
        }

        if ((!totalRequests.HasValue || !successRequests.HasValue) &&
            root.HasValue &&
            (TryGetString(root.Value, "Schema") ?? TryGetString(root.Value, "schema"))?.StartsWith("model-chat-multi", StringComparison.OrdinalIgnoreCase) == true)
        {
            totalRequests = TryGetDouble(root.Value, "ResultCount") ?? TryGetDouble(root.Value, "resultCount");
            successRequests = TryGetDouble(root.Value, "SuccessCount") ?? TryGetDouble(root.Value, "successCount");
            failedRequests = TryGetDouble(root.Value, "FailedCount") ?? TryGetDouble(root.Value, "failedCount");
            if ((!totalRequests.HasValue || !successRequests.HasValue) &&
                TryGetProperty(root.Value, "Results", out var modelResults) &&
                modelResults.ValueKind == JsonValueKind.Array)
            {
                var items = modelResults.EnumerateArray().ToArray();
                totalRequests ??= items.Length;
                successRequests ??= items.Count(static item => TryGetBool(item, "Succeeded") ?? TryGetBool(item, "succeeded") ?? false);
                failedRequests ??= Math.Max(0, totalRequests.Value - successRequests.Value);
            }
        }

        if ((!totalRequests.HasValue || !successRequests.HasValue) &&
            root.HasValue &&
            (TryGetString(root.Value, "Schema") ?? TryGetString(root.Value, "schema"))?.StartsWith("network-review", StringComparison.OrdinalIgnoreCase) == true)
        {
            var succeeded = IsNetworkReviewPayloadSuccessful(report, root.Value);
            totalRequests = 1;
            successRequests = succeeded ? 1 : 0;
            failedRequests = succeeded ? 0 : 1;
        }

        if ((!totalRequests.HasValue || !successRequests.HasValue) &&
            root.HasValue &&
            TryGetProperty(root.Value, "scenarios", out var scenarios) &&
            scenarios.ValueKind == JsonValueKind.Array)
        {
            var items = scenarios.EnumerateArray().ToArray();
            totalRequests ??= items.Length;
            successRequests ??= items.Count(static scenario =>
                TryGetBool(scenario, "Success") ?? TryGetBool(scenario, "success") ?? false);
            failedRequests ??= Math.Max(0, totalRequests.Value - successRequests.Value);
        }

        if ((!totalRequests.HasValue || !successRequests.HasValue) &&
            root.HasValue &&
            TryGetProperty(root.Value, "sites", out var sites) &&
            sites.ValueKind == JsonValueKind.Array)
        {
            var items = sites.EnumerateArray().ToArray();
            totalRequests ??= items.Length;
            var successRates = items
                .Select(static site => TryGetDouble(site, "SuccessRate") ?? TryGetDouble(site, "successRate"))
                .Where(static value => value.HasValue)
                .Select(static value => value!.Value)
                .ToArray();
            if (!successRequests.HasValue && successRates.Length > 0)
            {
                successRequests = successRates.Count(static rate => rate >= 99);
            }
        }

        var errorRate = totalRequests is > 0
            ? (failedRequests ?? Math.Max(0, totalRequests.Value - (successRequests ?? 0))) / totalRequests.Value * 100.0
            : 0;

        HistoryTotalRequestsSummary = $"\u603B\u8BF7\u6C42 {FormatCompactNumber(totalRequests ?? 0)}";
        HistorySuccessRequestsSummary = $"\u6210\u529F\u8BF7\u6C42 {FormatCompactNumber(successRequests ?? 0)}";
        HistoryErrorRateSummary = $"\u9519\u8BEF\u7387 {errorRate:F2}%";

        var p50 = ReadLatencySummary(root, "P50LatencyMs", "p50LatencyMs", "LatencyP50", "latencyP50") ??
                  report.DurationMs;
        var p95 = ReadLatencySummary(root, "P95LatencyMs", "p95LatencyMs", "LatencyP95", "latencyP95");
        var p99 = ReadLatencySummary(root, "P99LatencyMs", "p99LatencyMs", "LatencyP99", "latencyP99");
        HistoryP50Summary = $"P50 {FormatMillisecondsZero(p50)}";
        HistoryP95Summary = $"P95 {FormatMillisecondsZero(p95)}";
        HistoryP99Summary = $"P99 {FormatMillisecondsZero(p99)}";

        var inputThroughput = ReadThroughputSummary(root, "InputTokensPerSecond", "inputTokensPerSecond", "EndToEndTokensPerSecond");
        var outputThroughput = ReadThroughputSummary(root, "OutputTokensPerSecond", "outputTokensPerSecond", "MedianOutputTokensPerSecond", "medianOutputTokensPerSecond");
        var totalInput = root.HasValue
            ? TryGetDouble(root.Value, "TotalInputTokens") ?? TryGetDouble(root.Value, "totalInputTokens")
            : null;
        var totalOutput = root.HasValue
            ? ReadTotalOutputTokens(root.Value)
            : null;
        HistoryInputThroughputSummary = $"Input {(inputThroughput ?? 0):F1} tokens/s";
        HistoryOutputThroughputSummary = $"Output {(outputThroughput ?? 0):F1} tokens/s";
        HistoryTotalInputSummary = $"\u603B\u8F93\u5165 {FormatCompactNumber(totalInput ?? 0)}";
        HistoryTotalOutputSummary = $"\u603B\u8F93\u51FA {FormatCompactNumber(totalOutput ?? 0)}";

        HistoryTimeoutSummary = $"Timeouts {FormatCompactNumber(root.HasValue ? TryGetDouble(root.Value, "TimeoutCount") ?? TryGetDouble(root.Value, "timeoutCount") ?? 0 : 0)}";
        HistoryRateLimitSummary = $"429 count {FormatCompactNumber(root.HasValue ? TryGetDouble(root.Value, "RateLimitedRequests") ?? TryGetDouble(root.Value, "rateLimitedRequests") ?? 0 : 0)}";
        HistoryServerErrorSummary = $"5xx count {FormatCompactNumber(root.HasValue ? TryGetDouble(root.Value, "ServerErrorCount") ?? TryGetDouble(root.Value, "serverErrorCount") ?? 0 : 0)}";
    }

    private static bool IsModelChatPayload(JsonElement root)
    {
        var schema = TryGetString(root, "Schema") ?? TryGetString(root, "schema") ?? string.Empty;
        return schema.StartsWith("model-chat", StringComparison.OrdinalIgnoreCase) ||
               TryGetProperty(root, "PromptCacheTokens", out _) ||
               TryGetProperty(root, "promptCacheTokens", out _) ||
               TryGetProperty(root, "OutputTokenCountEstimated", out _) ||
               TryGetProperty(root, "outputTokenCountEstimated", out _);
    }

    private static bool IsModelChatMultiPayload(JsonElement root)
    {
        var schema = TryGetString(root, "Schema") ?? TryGetString(root, "schema") ?? string.Empty;
        return schema.StartsWith("model-chat-multi", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetBatchSection(JsonElement root, out JsonElement batch)
        => TryGetProperty(root, "batch", out batch) ||
           TryGetProperty(root, "Batch", out batch);

    private static bool TryGetProxyTrendsSection(JsonElement root, out JsonElement trends)
    {
        if ((TryGetProperty(root, "trends", out trends) || TryGetProperty(root, "proxyTrends", out trends)) &&
            trends.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (TryGetProperty(root, "proxy", out var proxy) &&
            proxy.ValueKind == JsonValueKind.Object &&
            (TryGetProperty(proxy, "trends", out trends) || TryGetProperty(proxy, "proxyTrends", out trends)) &&
            trends.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        trends = default;
        return false;
    }

    private static bool TryGetProxyConcurrencySection(JsonElement root, out JsonElement concurrency)
    {
        if ((TryGetProperty(root, "concurrency", out concurrency) || TryGetProperty(root, "proxyConcurrency", out concurrency)) &&
            concurrency.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (TryGetProperty(root, "proxy", out var proxy) &&
            proxy.ValueKind == JsonValueKind.Object &&
            (TryGetProperty(proxy, "concurrency", out concurrency) || TryGetProperty(proxy, "proxyConcurrency", out concurrency)) &&
            concurrency.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        concurrency = default;
        return false;
    }

    private static bool TryGetProxyStabilitySection(JsonElement root, out JsonElement stability)
    {
        if ((TryGetProperty(root, "stability", out stability) || TryGetProperty(root, "proxyStability", out stability)) &&
            stability.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (TryGetProperty(root, "proxy", out var proxy) &&
            proxy.ValueKind == JsonValueKind.Object &&
            (TryGetProperty(proxy, "stability", out stability) || TryGetProperty(proxy, "proxyStability", out stability)) &&
            stability.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        stability = default;
        return false;
    }

    private static bool TryGetProxySingleSection(JsonElement root, out JsonElement single)
    {
        if ((TryGetProperty(root, "single", out single) || TryGetProperty(root, "proxySingle", out single)) &&
            single.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (TryGetProperty(root, "proxy", out var proxy) &&
            proxy.ValueKind == JsonValueKind.Object &&
            (TryGetProperty(proxy, "single", out single) || TryGetProperty(proxy, "proxySingle", out single)) &&
            single.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        single = default;
        return false;
    }

    private static bool TryGetProxySingleScenarioResults(JsonElement root, out JsonElement scenarios)
    {
        if (TryGetProxySingleSection(root, out var single) &&
            (TryGetProperty(single, "scenarioResults", out scenarios) || TryGetProperty(single, "scenarios", out scenarios)) &&
            scenarios.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        scenarios = default;
        return false;
    }

    private static bool TryGetProxyModelCatalogSection(JsonElement root, out JsonElement modelCatalog)
    {
        if ((TryGetProperty(root, "modelCatalog", out modelCatalog) || TryGetProperty(root, "proxyModelCatalog", out modelCatalog)) &&
            modelCatalog.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (TryGetProperty(root, "proxy", out var proxy) &&
            proxy.ValueKind == JsonValueKind.Object &&
            (TryGetProperty(proxy, "modelCatalog", out modelCatalog) || TryGetProperty(proxy, "proxyModelCatalog", out modelCatalog)) &&
            modelCatalog.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        modelCatalog = default;
        return false;
    }

    private static bool TryGetClientApiSection(JsonElement root, out JsonElement clientApi)
    {
        if ((TryGetProperty(root, "clientApi", out clientApi) || TryGetProperty(root, "clientApiDiagnostics", out clientApi)) &&
            clientApi.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        clientApi = default;
        return false;
    }

    private static bool TryGetStunSection(JsonElement root, out JsonElement stun)
    {
        if ((TryGetProperty(root, "stun", out stun) || TryGetProperty(root, "stunNat", out stun)) &&
            stun.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        stun = default;
        return false;
    }

    private static bool TryGetUnlockCatalogSection(JsonElement root, out JsonElement unlock)
    {
        if ((TryGetProperty(root, "chatgptUnlock", out unlock) || TryGetProperty(root, "unlockCatalog", out unlock)) &&
            unlock.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        unlock = default;
        return false;
    }

    private static bool TryGetLegacyRouteSection(JsonElement root, out JsonElement route)
    {
        if (TryGetProperty(root, "route", out route) && route.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        route = default;
        return false;
    }

    private static bool TryGetLegacyPortScanSection(JsonElement root, out JsonElement portScan)
    {
        if (TryGetProperty(root, "portScan", out portScan) && portScan.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        portScan = default;
        return false;
    }

    private static bool TryGetLegacySpeedSection(JsonElement root, out JsonElement speed)
    {
        if (TryGetProperty(root, "speed", out speed) && speed.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        speed = default;
        return false;
    }

    private static bool TryGetLegacySplitRoutingSection(JsonElement root, out JsonElement splitRouting)
    {
        if (TryGetProperty(root, "splitRouting", out splitRouting) && splitRouting.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        splitRouting = default;
        return false;
    }

    private static bool TryGetBatchRankingArray(JsonElement root, out JsonElement ranking)
    {
        if ((TryGetProperty(root, "sites", out ranking) || TryGetProperty(root, "Sites", out ranking)) &&
            ranking.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (TryGetBatchSection(root, out var batch) &&
            (TryGetProperty(batch, "ranking", out ranking) || TryGetProperty(batch, "Ranking", out ranking)) &&
            ranking.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        ranking = default;
        return false;
    }

    private static double? ReadBatchRankingScore(JsonElement site)
        => TryGetDouble(site, "Score") ??
           TryGetDouble(site, "score") ??
           TryGetDouble(site, "CompositeScore") ??
           TryGetDouble(site, "compositeScore");

    private static double? ReadBatchRankingThroughput(JsonElement site)
    {
        var direct = TryGetDouble(site, "ThroughputTokensPerSecond") ??
                     TryGetDouble(site, "throughputTokensPerSecond") ??
                     TryGetDouble(site, "TokensPerSecond") ??
                     TryGetDouble(site, "tokensPerSecond") ??
                     TryGetDouble(site, "AverageBenchmarkTokensPerSecond") ??
                     TryGetDouble(site, "averageBenchmarkTokensPerSecond");
        if (direct.HasValue)
        {
            return direct;
        }

        if (TryGetProperty(site, "throughputBenchmark", out var benchmark) ||
            TryGetProperty(site, "ThroughputBenchmark", out benchmark))
        {
            return TryGetDouble(benchmark, "MedianOutputTokensPerSecond") ??
                   TryGetDouble(benchmark, "medianOutputTokensPerSecond") ??
                   TryGetDouble(benchmark, "MaximumOutputTokensPerSecond") ??
                   TryGetDouble(benchmark, "maximumOutputTokensPerSecond");
        }

        if (TryGetProperty(site, "longStreaming", out var longStreaming) ||
            TryGetProperty(site, "LongStreaming", out longStreaming))
        {
            return TryGetDouble(longStreaming, "OutputTokensPerSecond") ??
                   TryGetDouble(longStreaming, "outputTokensPerSecond");
        }

        return null;
    }

    private static double? ReadBatchRankingLatency(JsonElement site)
        => TryGetDouble(site, "LatencyMs") ??
           TryGetDouble(site, "latencyMs") ??
           TryGetDouble(site, "ChatLatencyMs") ??
           TryGetDouble(site, "chatLatencyMs");

    private static double? ReadBatchRankingTtft(JsonElement site)
        => TryGetDouble(site, "TtftMs") ??
           TryGetDouble(site, "ttftMs") ??
           TryGetDouble(site, "FirstTokenLatencyMs") ??
           TryGetDouble(site, "firstTokenLatencyMs");

    private static bool IsBatchRankingFailure(JsonElement site)
    {
        var verdict = TryGetString(site, "verdict") ?? TryGetString(site, "Verdict") ?? string.Empty;
        var issue = TryGetString(site, "primaryIssue") ?? TryGetString(site, "PrimaryIssue") ?? string.Empty;
        return verdict.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
               verdict.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               issue.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
               issue.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBatchRankingSuccessful(JsonElement site, double? score)
    {
        var verdict = TryGetString(site, "verdict") ?? TryGetString(site, "Verdict") ?? string.Empty;
        if (verdict.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
            verdict.Contains("ok", StringComparison.OrdinalIgnoreCase) ||
            verdict.Contains("success", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsBatchRankingFailure(site))
        {
            return false;
        }

        return score is null or >= 60;
    }

    private static string ResolveBatchRankingState(JsonElement site, bool success)
    {
        var verdict = TryGetString(site, "verdict") ?? TryGetString(site, "Verdict");
        if (!string.IsNullOrWhiteSpace(verdict))
        {
            if (IsBatchRankingFailure(site))
            {
                return HistoryStates.Failed;
            }

            if (success)
            {
                return HistoryStates.Passed;
            }
        }

        return success ? HistoryStates.Passed : HistoryStates.Review;
    }

    private static string BuildBatchRankingEvidence(JsonElement site, double? successRate, double? score)
    {
        List<string> parts = [];
        if (score.HasValue)
        {
            parts.Add($"score {score.Value:F1}");
        }

        if (successRate.HasValue)
        {
            parts.Add($"error {Math.Max(0, 100 - successRate.Value):F1}%");
        }

        if (ReadBatchRankingLatency(site) is { } chatLatency)
        {
            parts.Add($"latency {FormatMilliseconds(chatLatency)}");
        }

        if (ReadBatchRankingTtft(site) is { } ttft)
        {
            parts.Add($"ttft {FormatMilliseconds(ttft)}");
        }

        if (ReadBatchRankingThroughput(site) is { } throughput)
        {
            parts.Add($"{throughput:F1} tok/s");
        }

        AddEvidencePart(parts, TryGetString(site, "verdict") ?? TryGetString(site, "Verdict"));
        AddEvidencePart(parts, TryGetString(site, "primaryIssue") ?? TryGetString(site, "PrimaryIssue"));
        AddEvidencePart(parts, TryGetString(site, "traceability") ?? TryGetString(site, "Traceability"));
        AddEvidencePart(parts, TryGetString(site, "traceId") ?? TryGetString(site, "TraceId"));
        AddEvidencePart(parts, TryGetString(site, "requestId") ?? TryGetString(site, "RequestId"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string? ReadReportSectionText(JsonElement section, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(section, propertyName, out var value))
            {
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                return NormalizeInlineWhitespace(text);
            }
        }

        return null;
    }

}
