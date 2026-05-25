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
    private bool PopulateProtocolRowsFromPayload(JsonElement root)
    {
        var added = false;
        if (PopulateNetworkReviewRowsFromPayload(root))
        {
            return true;
        }

        if (IsModelChatMultiPayload(root) &&
            TryGetProperty(root, "Results", out var modelResults) &&
            modelResults.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in modelResults.EnumerateArray().Take(32))
            {
                var success = TryGetBool(result, "Succeeded") ?? TryGetBool(result, "succeeded") ?? false;
                var outputCharacters = TryGetDouble(result, "OutputCharacters") ?? TryGetDouble(result, "outputCharacters");
                ProtocolResults.Add(new HistoryProtocolResult(
                    TryGetString(result, "Model") ?? TryGetString(result, "model") ?? "Model",
                    FormatMilliseconds(TryGetDouble(result, "ResponseTimeMs") ?? TryGetDouble(result, "responseTimeMs")),
                    outputCharacters.HasValue ? FormatCompactNumber(outputCharacters.Value) : "--",
                    TryGetString(result, "Error") ?? TryGetString(result, "error") ?? "--",
                    success ? HistoryStates.Passed : HistoryStates.Failed,
                    success ? HistoryTones.Healthy : HistoryTones.Danger));
                added = true;
            }

            return added;
        }

        if ((TryGetString(root, "Schema") ?? TryGetString(root, "schema"))?.StartsWith("application-access", StringComparison.OrdinalIgnoreCase) == true)
        {
            return PopulateApplicationAccessRowsFromPayload(root);
        }

        if (TryGetProperty(root, "scenarios", out var scenarios) && scenarios.ValueKind == JsonValueKind.Array)
        {
            foreach (var scenario in scenarios.EnumerateArray().Take(32))
            {
                var name = TryGetString(scenario, "DisplayName") ??
                           TryGetString(scenario, "displayName") ??
                           TryGetString(scenario, "scenario") ??
                           "Scenario";
                var success = TryGetBool(scenario, "Success") ?? TryGetBool(scenario, "success") ?? false;
                var state = TryGetString(scenario, "CapabilityStatus") ??
                            TryGetString(scenario, "capabilityStatus") ??
                            (success ? HistoryStates.Passed : HistoryStates.Review);
                ProtocolResults.Add(new HistoryProtocolResult(
                    name,
                    FormatMilliseconds(TryGetDouble(scenario, "latencyMs")),
                    FormatMilliseconds(TryGetDouble(scenario, "firstTokenMs")),
                    success ? "0%" : "100%",
                    TranslateState(state, success),
                    success ? HistoryTones.Healthy : HistoryTones.Warning));
                added = true;
            }
        }

        if (TryGetProxySingleScenarioResults(root, out var proxySingleScenarios))
        {
            foreach (var scenario in proxySingleScenarios.EnumerateArray().Take(32))
            {
                var name = TryGetString(scenario, "DisplayName") ??
                           TryGetString(scenario, "displayName") ??
                           TryGetString(scenario, "scenario") ??
                           "Scenario";
                var success = TryGetBool(scenario, "Success") ?? TryGetBool(scenario, "success") ?? false;
                var state = TryGetString(scenario, "CapabilityStatus") ??
                            TryGetString(scenario, "capabilityStatus") ??
                            (success ? "Supported" : "Failed");
                ProtocolResults.Add(new HistoryProtocolResult(
                    name,
                    FormatMilliseconds(TryGetDouble(scenario, "latencyMs") ?? TryGetDouble(scenario, "LatencyMs")),
                    FormatMilliseconds(TryGetDouble(scenario, "firstTokenLatencyMs") ?? TryGetDouble(scenario, "FirstTokenLatencyMs")),
                    BuildProxySingleScenarioEvidence(scenario),
                    TranslateState(state, success),
                    success ? HistoryTones.Healthy : HistoryTones.Danger));
                added = true;
            }
        }

        if (TryGetProxyModelCatalogSection(root, out var proxyModelCatalog))
        {
            var summary = ReadReportSectionText(proxyModelCatalog, "summary");
            var detail = ReadReportSectionText(proxyModelCatalog, "detail");
            var state = IsNetworkStatusFailed($"{summary} {detail}") ? HistoryStates.Failed : HistoryStates.Passed;
            ProtocolResults.Add(new HistoryProtocolResult(
                "Model catalog",
                "--",
                "--",
                BuildProxyModelCatalogEvidence(summary, detail),
                state,
                state == HistoryStates.Passed ? HistoryTones.Healthy : HistoryTones.Danger));
            added = true;
        }

        if (TryGetClientApiSection(root, out var clientApi) &&
            TryGetProperty(clientApi, "checks", out var clientChecks) &&
            clientChecks.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in clientChecks.EnumerateArray().Take(32))
            {
                var reachable = TryGetBool(check, "Reachable") ?? TryGetBool(check, "reachable") ?? false;
                var state = ResolveClientApiCheckState(check, reachable);
                ProtocolResults.Add(new HistoryProtocolResult(
                    TryGetString(check, "Name") ?? TryGetString(check, "name") ?? TryGetString(check, "Provider") ?? "Client API",
                    FormatMilliseconds(TryGetDouble(check, "latencyMs") ?? TryGetDouble(check, "LatencyMs")),
                    (TryGetDouble(check, "StatusCode") ?? TryGetDouble(check, "statusCode")) is { } statusCode
                        ? statusCode.ToString("F0", CultureInfo.InvariantCulture)
                        : "--",
                    BuildClientApiCheckEvidence(check),
                    state,
                    state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
                added = true;
            }
        }

        if (TryGetStunSection(root, out var stun))
        {
            var summary = ReadReportSectionText(stun, "summary");
            var coverage = ReadReportSectionText(stun, "coverageSummary");
            var test = ReadReportSectionText(stun, "testSummary");
            var attributes = ReadReportSectionText(stun, "attributeSummary");
            var recommendation = ReadReportSectionText(stun, "reviewRecommendation");
            var natType = ReadReportSectionText(stun, "natType");
            var state = ResolveStunState(summary, recommendation);
            ProtocolResults.Add(new HistoryProtocolResult(
                "STUN NAT",
                TryGetDouble(stun, "confidence") is { } confidence ? FormatPercentValue(confidence) : "--",
                natType ?? "--",
                BuildStunEvidence(summary, coverage, test, attributes, recommendation),
                state,
                state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
            added = true;
        }

        if (TryGetUnlockCatalogSection(root, out var unlock))
        {
            var summary = ReadReportSectionText(unlock, "summary");
            var catalogSummary = ReadReportSectionText(unlock, "unlockCatalogSummary");
            var catalogDetail = ReadReportSectionText(unlock, "unlockCatalogDetail");
            var rawTrace = ReadReportSectionText(unlock, "rawTrace");
            var semanticCounts = TryGetProperty(unlock, "semanticCounts", out var counts) && counts.ValueKind == JsonValueKind.Object
                ? counts
                : (JsonElement?)null;
            var state = ResolveUnlockCatalogState(summary, catalogSummary, semanticCounts);
            ProtocolResults.Add(new HistoryProtocolResult(
                "Unlock catalog",
                semanticCounts.HasValue ? FormatCompactNumber(TryGetDouble(semanticCounts.Value, "ReadyCount") ?? 0) : "--",
                semanticCounts.HasValue ? FormatCompactNumber(TryGetDouble(semanticCounts.Value, "TotalCount") ?? 0) : "--",
                BuildUnlockCatalogEvidence(summary, catalogSummary, catalogDetail, rawTrace, semanticCounts),
                state,
                state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
            added = true;
        }

        if (TryGetLegacyRouteSection(root, out var legacyRoute))
        {
            var summary = ReadReportSectionText(legacyRoute, "summary");
            var mapSummary = ReadReportSectionText(legacyRoute, "mapSummary");
            var geoSummary = ReadReportSectionText(legacyRoute, "geoSummary");
            var hopSummary = ReadReportSectionText(legacyRoute, "hopSummary");
            var rawOutput = ReadReportSectionText(legacyRoute, "rawOutput");
            var state = ResolveLegacyRouteState(summary, rawOutput);
            ProtocolResults.Add(new HistoryProtocolResult(
                "Route review",
                TryGetBool(legacyRoute, "hasMapImage") == true ? "Map" : "--",
                string.IsNullOrWhiteSpace(geoSummary) ? "--" : CompactTileDelta(geoSummary),
                BuildLegacyRouteEvidence(summary, mapSummary, geoSummary, hopSummary, rawOutput),
                state,
                state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
            added = true;
        }

        if (TryGetLegacyPortScanSection(root, out var legacyPortScan))
        {
            var portRowsAdded = false;
            var target = ReadReportSectionText(legacyPortScan, "target");
            if (TryGetProperty(legacyPortScan, "findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
            {
                foreach (var finding in findings.EnumerateArray().Take(32))
                {
                    if (finding.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var state = ResolveLegacyPortScanState("open", null, 1, finding: true);
                    ProtocolResults.Add(new HistoryProtocolResult(
                        BuildLegacyPortScanFindingName(finding, target),
                        FormatMilliseconds(TryGetDouble(finding, "ConnectLatencyMilliseconds") ??
                                           TryGetDouble(finding, "connectLatencyMilliseconds") ??
                                           TryGetDouble(finding, "LatencyMs") ??
                                           TryGetDouble(finding, "latencyMs")),
                        BuildLegacyPortScanFindingProtocol(finding),
                        BuildLegacyPortScanFindingEvidence(finding),
                        state,
                        HistoryTones.Healthy));
                    portRowsAdded = true;
                    added = true;
                }
            }

            if (TryGetProperty(legacyPortScan, "batchFindings", out var batchFindings) && batchFindings.ValueKind == JsonValueKind.Array)
            {
                foreach (var finding in batchFindings.EnumerateArray().Take(32))
                {
                    if (finding.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var findingTarget = TryGetString(finding, "target") ?? TryGetString(finding, "Target");
                    var state = ResolveLegacyPortScanState("open", null, 1, finding: true);
                    ProtocolResults.Add(new HistoryProtocolResult(
                        BuildLegacyPortScanFindingName(finding, findingTarget),
                        FormatMilliseconds(TryGetDouble(finding, "ConnectLatencyMilliseconds") ??
                                           TryGetDouble(finding, "connectLatencyMilliseconds") ??
                                           TryGetDouble(finding, "LatencyMs") ??
                                           TryGetDouble(finding, "latencyMs")),
                        BuildLegacyPortScanFindingProtocol(finding),
                        BuildLegacyPortScanFindingEvidence(finding),
                        state,
                        HistoryTones.Healthy));
                    portRowsAdded = true;
                    added = true;
                }
            }

            if (TryGetProperty(legacyPortScan, "batchRows", out var batchRows) && batchRows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in batchRows.EnumerateArray().Take(32))
                {
                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var openEndpoints = TryGetDouble(row, "OpenEndpointCount") ?? TryGetDouble(row, "openEndpointCount");
                    var openPorts = TryGetDouble(row, "OpenPortCount") ?? TryGetDouble(row, "openPortCount");
                    var status = ReadReportSectionText(row, "Status", "status");
                    var error = ReadReportSectionText(row, "Error", "error");
                    var state = ResolveLegacyPortScanState(status, error, openEndpoints ?? openPorts, finding: false);
                    ProtocolResults.Add(new HistoryProtocolResult(
                        ReadReportSectionText(row, "Target", "target") ?? "Port scan target",
                        "--",
                        $"{FormatCompactNumber(openEndpoints ?? 0)} endpoints / {FormatCompactNumber(openPorts ?? 0)} ports",
                        BuildLegacyPortScanBatchEvidence(row),
                        state,
                        state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
                    portRowsAdded = true;
                    added = true;
                }
            }

            if (!portRowsAdded)
            {
                var state = ResolveLegacyPortScanState(
                    ReadReportSectionText(legacyPortScan, "summary"),
                    ReadReportSectionText(legacyPortScan, "detail", "rawOutput"),
                    TryGetDouble(legacyPortScan, "openEndpointCount") ?? TryGetDouble(legacyPortScan, "openPortCount"),
                    finding: false);
                ProtocolResults.Add(new HistoryProtocolResult(
                    "Port scan",
                    target ?? "--",
                    ReadReportSectionText(legacyPortScan, "profileName", "profileKey", "effectivePortsText") ?? "--",
                    BuildLegacyPortScanSummaryEvidence(legacyPortScan),
                    state,
                    state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
                added = true;
            }
        }

        if (TryGetLegacySpeedSection(root, out var legacySpeed))
        {
            var summary = ReadReportSectionText(legacySpeed, "summary");
            var latency = ReadReportSectionText(legacySpeed, "latencyDetail");
            var transfer = ReadReportSectionText(legacySpeed, "transferDetail");
            var packetLoss = ReadReportSectionText(legacySpeed, "packetLossDetail");
            var state = ResolveLegacyNetworkTextState(summary, latency, transfer, packetLoss);
            ProtocolResults.Add(new HistoryProtocolResult(
                "Speed test",
                latency is null ? "--" : CompactTileDelta(latency),
                packetLoss is null ? "--" : CompactTileDelta(packetLoss),
                BuildLegacySpeedEvidence(legacySpeed),
                state,
                state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
            added = true;
        }

        if (TryGetLegacySplitRoutingSection(root, out var legacySplitRouting))
        {
            var summary = ReadReportSectionText(legacySplitRouting, "summary");
            var insight = ReadReportSectionText(legacySplitRouting, "ipInsightSummary");
            var adapters = ReadReportSectionText(legacySplitRouting, "adapterSummary");
            var exit = ReadReportSectionText(legacySplitRouting, "exitSummary");
            var dns = ReadReportSectionText(legacySplitRouting, "dnsSummary");
            var reachability = ReadReportSectionText(legacySplitRouting, "reachabilitySummary");
            var state = ResolveLegacyNetworkTextState(summary, insight, adapters, exit, dns, reachability);
            ProtocolResults.Add(new HistoryProtocolResult(
                "Split routing",
                insight is null ? "--" : CompactTileDelta(insight),
                adapters is null ? "--" : CompactTileDelta(adapters),
                BuildLegacySplitRoutingEvidence(legacySplitRouting, "summary", "ipInsightSummary", "adapterSummary"),
                state,
                state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
            added = true;

            if (!string.IsNullOrWhiteSpace(exit))
            {
                var exitState = ResolveLegacyNetworkTextState(exit);
                ProtocolResults.Add(new HistoryProtocolResult(
                    "Exit routing",
                    "--",
                    "--",
                    exit,
                    exitState,
                    exitState == HistoryStates.Passed ? HistoryTones.Healthy : exitState == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
            }

            if (!string.IsNullOrWhiteSpace(dns))
            {
                var dnsState = ResolveLegacyNetworkTextState(dns);
                ProtocolResults.Add(new HistoryProtocolResult(
                    "DNS routing",
                    "--",
                    "--",
                    dns,
                    dnsState,
                    dnsState == HistoryStates.Passed ? HistoryTones.Healthy : dnsState == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
            }

            if (!string.IsNullOrWhiteSpace(reachability))
            {
                var reachabilityState = ResolveLegacyNetworkTextState(reachability);
                ProtocolResults.Add(new HistoryProtocolResult(
                    "Reachability",
                    "--",
                    "--",
                    reachability,
                    reachabilityState,
                    reachabilityState == HistoryStates.Passed ? HistoryTones.Healthy : reachabilityState == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
            }
        }

        if (TryGetBatchRankingArray(root, out var sites))
        {
            foreach (var site in sites.EnumerateArray().Take(32))
            {
                var score = ReadBatchRankingScore(site);
                var successRate = TryGetDouble(site, "SuccessRate") ?? TryGetDouble(site, "successRate");
                var success = IsBatchRankingSuccessful(site, score);
                ProtocolResults.Add(new HistoryProtocolResult(
                    TryGetString(site, "Name") ?? TryGetString(site, "name") ?? TryGetString(site, "BaseUrl") ?? TryGetString(site, "baseUrl") ?? "Site",
                    FormatMilliseconds(TryGetDouble(site, "LatencyMs") ?? TryGetDouble(site, "latencyMs") ?? TryGetDouble(site, "chatLatencyMs") ?? TryGetDouble(site, "ChatLatencyMs")),
                    FormatMilliseconds(TryGetDouble(site, "TtftMs") ?? TryGetDouble(site, "ttftMs") ?? TryGetDouble(site, "ttftMs") ?? TryGetDouble(site, "TtftMs")),
                    BuildBatchRankingEvidence(site, successRate, score),
                    ResolveBatchRankingState(site, success),
                    success ? HistoryTones.Healthy : IsBatchRankingFailure(site) ? HistoryTones.Danger : HistoryTones.Warning));
                added = true;
            }
        }

        if (TryGetProxyTrendsSection(root, out var proxyTrends))
        {
            var target = ReadReportSectionText(proxyTrends, "trendTarget", "target", "baseUrl");
            var summary = ReadReportSectionText(proxyTrends, "summary");
            var detail = ReadReportSectionText(proxyTrends, "detail");
            var trend24h = ReadReportSectionText(proxyTrends, "trend24h", "summary24h");
            var hasTrendChart = TryGetBool(proxyTrends, "hasTrendChart");
            var hasEvidence = !string.IsNullOrWhiteSpace(summary) ||
                              !string.IsNullOrWhiteSpace(detail) ||
                              !string.IsNullOrWhiteSpace(trend24h);
            ProtocolResults.Add(new HistoryProtocolResult(
                "Proxy trend",
                target is null ? "--" : CompactTileDelta(target),
                trend24h is null ? hasTrendChart == true ? "Chart" : "--" : CompactTileDelta(trend24h),
                BuildProxyTrendEvidence(summary, detail, trend24h, hasTrendChart),
                hasEvidence ? HistoryStates.Passed : HistoryStates.Review,
                hasEvidence ? HistoryTones.Healthy : HistoryTones.Warning));
            added = true;
        }

        if (TryGetProxyConcurrencySection(root, out var proxyConcurrency))
        {
            var stageRowsAdded = false;
            if (TryGetProperty(proxyConcurrency, "stages", out var stages) && stages.ValueKind == JsonValueKind.Array)
            {
                foreach (var stage in stages.EnumerateArray().Take(32))
                {
                    var concurrency = TryGetDouble(stage, "Concurrency") ?? TryGetDouble(stage, "concurrency");
                    var total = TryGetDouble(stage, "TotalRequests") ?? TryGetDouble(stage, "totalRequests") ?? 0;
                    var success = TryGetDouble(stage, "SuccessCount") ?? TryGetDouble(stage, "successCount") ?? 0;
                    var rateLimited = TryGetDouble(stage, "RateLimitedCount") ?? TryGetDouble(stage, "rateLimitedCount") ?? 0;
                    var serverErrors = TryGetDouble(stage, "ServerErrorCount") ?? TryGetDouble(stage, "serverErrorCount") ?? 0;
                    var timeouts = TryGetDouble(stage, "TimeoutCount") ?? TryGetDouble(stage, "timeoutCount") ?? 0;
                    var state = ResolveProxyConcurrencyStageState(total, success, rateLimited, serverErrors, timeouts);
                    ProtocolResults.Add(new HistoryProtocolResult(
                        concurrency.HasValue ? $"Concurrency x{concurrency.Value:F0}" : "Concurrency stage",
                        FormatMilliseconds(TryGetDouble(stage, "P50ChatLatencyMs") ?? TryGetDouble(stage, "p50ChatLatencyMs")),
                        FormatMilliseconds(TryGetDouble(stage, "P50TtftMs") ?? TryGetDouble(stage, "p50TtftMs")),
                        BuildProxyConcurrencyStageEvidence(stage, total, success, rateLimited, serverErrors, timeouts),
                        state,
                        state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
                    stageRowsAdded = true;
                    added = true;
                }
            }

            if (!stageRowsAdded)
            {
                var summary = ReadReportSectionText(proxyConcurrency, "summary");
                var detail = ReadReportSectionText(proxyConcurrency, "detail", "error");
                var hasChart = TryGetBool(proxyConcurrency, "hasChart");
                ProtocolResults.Add(new HistoryProtocolResult(
                    "Proxy concurrency",
                    ReadReportSectionText(proxyConcurrency, "baseUrl", "target") is { } target ? CompactTileDelta(target) : "--",
                    ReadReportSectionText(proxyConcurrency, "model") is { } model ? CompactTileDelta(model) : "--",
                    BuildProxyConcurrencySummaryEvidence(proxyConcurrency, summary, detail, hasChart),
                    string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(detail) ? HistoryStates.Review : HistoryStates.Passed,
                    string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(detail) ? HistoryTones.Warning : HistoryTones.Healthy));
                added = true;
            }
        }

        if (TryGetProxyStabilitySection(root, out var proxyStability))
        {
            var failureRowsAdded = false;
            if (TryGetProperty(proxyStability, "failureDistribution", out var failures) &&
                failures.ValueKind == JsonValueKind.Array)
            {
                foreach (var failure in failures.EnumerateArray().Take(32))
                {
                    var count = TryGetDouble(failure, "Count") ?? TryGetDouble(failure, "count") ?? 0;
                    var rate = TryGetDouble(failure, "Rate") ?? TryGetDouble(failure, "rate") ?? 0;
                    var state = rate >= 50 || count >= 5 ? HistoryStates.Failed : HistoryStates.Review;
                    ProtocolResults.Add(new HistoryProtocolResult(
                        TryGetString(failure, "failureKind") ?? TryGetString(failure, "FailureKind") ?? "Stability failure",
                        FormatCompactNumber(count),
                        $"{rate:F1}%",
                        BuildProxyStabilityFailureEvidence(failure),
                        state,
                        state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
                    failureRowsAdded = true;
                    added = true;
                }
            }

            if (!failureRowsAdded)
            {
                var healthScore = TryGetDouble(proxyStability, "healthScore");
                var healthLabel = ReadReportSectionText(proxyStability, "healthLabel");
                var state = ResolveProxyStabilityState(healthScore, healthLabel);
                ProtocolResults.Add(new HistoryProtocolResult(
                    "Proxy stability",
                    healthScore.HasValue ? $"{healthScore.Value:F1}" : "--",
                    healthLabel ?? "--",
                    BuildProxyStabilitySummaryEvidence(proxyStability),
                    state,
                    state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
                added = true;
            }
        }

        if (TryGetProperty(root, "Results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray().Take(32))
            {
                var status = TryGetString(result, "Status") ?? "--";
                var success = status.Contains("Passed", StringComparison.OrdinalIgnoreCase);
                ProtocolResults.Add(new HistoryProtocolResult(
                    TryGetString(result, "DisplayName") ?? "Safety scenario",
                    TryGetDouble(result, "Score") is { } score ? $"{score:F1}" : "--",
                    TryGetString(result, "RiskLevel") ?? "--",
                    TryGetString(result, "ErrorKind") ?? "--",
                    TranslateState(status, success),
                    success ? HistoryTones.Healthy : HistoryTones.Warning));
                added = true;
            }
        }

        if ((TryGetProperty(root, "RouteHits", out var routeHits) || TryGetProperty(root, "routeHits", out routeHits)) &&
            routeHits.ValueKind == JsonValueKind.Array)
        {
            foreach (var route in routeHits.EnumerateArray().Take(32))
            {
                var sent = TryGetDouble(route, "Sent") ?? TryGetDouble(route, "sent") ?? 0;
                var success = TryGetDouble(route, "Success") ?? TryGetDouble(route, "success") ?? 0;
                var failed = TryGetDouble(route, "Failed") ?? TryGetDouble(route, "failed") ?? 0;
                var statusCode = TryGetDouble(route, "LastStatusCode") ?? TryGetDouble(route, "lastStatusCode");
                var circuitState = TryGetString(route, "CircuitState") ?? TryGetString(route, "circuitState") ?? "Closed";
                var cooldownSeconds = TryGetDouble(route, "CooldownSeconds") ?? TryGetDouble(route, "cooldownSeconds") ?? 0;
                var modelCooldowns = CountRouteModelCooldowns(route);
                var ok = IsTransparentProxyRouteHealthy(failed, circuitState, cooldownSeconds, modelCooldowns);
                ProtocolResults.Add(new HistoryProtocolResult(
                    TryGetString(route, "Name") ?? TryGetString(route, "name") ?? "Proxy route",
                    FormatMilliseconds(TryGetDouble(route, "LastLatencyMs") ?? TryGetDouble(route, "lastLatencyMs")),
                    $"{success:F0}/{sent:F0}",
                    BuildTransparentProxyRouteEvidence(statusCode, circuitState, cooldownSeconds, modelCooldowns),
                    ResolveTransparentProxyRouteState(failed, circuitState, cooldownSeconds, modelCooldowns),
                    ok ? HistoryTones.Healthy : circuitState.Contains("Open", StringComparison.OrdinalIgnoreCase) ? HistoryTones.Danger : HistoryTones.Warning));
                added = true;
            }
        }

        if ((TryGetProperty(root, "ModelPoolSummary", out var modelPools) || TryGetProperty(root, "modelPoolSummary", out modelPools)) &&
            modelPools.ValueKind == JsonValueKind.Array)
        {
            foreach (var pool in modelPools.EnumerateArray().Take(32))
            {
                var memberCount = TryGetDouble(pool, "MemberCount") ??
                                  TryGetDouble(pool, "memberCount") ??
                                  TryGetDouble(pool, "TotalCount") ??
                                  TryGetDouble(pool, "totalCount") ??
                                  0;
                var healthyMembers = TryGetDouble(pool, "HealthyMembers") ??
                                     TryGetDouble(pool, "healthyMembers") ??
                                     TryGetDouble(pool, "HealthyCount") ??
                                     TryGetDouble(pool, "healthyCount") ??
                                     Math.Max(0, memberCount);
                var openCircuitMembers = TryGetDouble(pool, "OpenCircuitMembers") ??
                                         TryGetDouble(pool, "openCircuitMembers") ??
                                         0;
                var state = ResolveTransparentProxyPoolState(memberCount, healthyMembers, openCircuitMembers);
                ProtocolResults.Add(new HistoryProtocolResult(
                    TryGetString(pool, "ModelName") ?? TryGetString(pool, "modelName") ?? TryGetString(pool, "Name") ?? "Model pool",
                    FormatMilliseconds(TryGetDouble(pool, "BestLatencyMs") ?? TryGetDouble(pool, "bestLatencyMs")),
                    $"{healthyMembers:F0}/{memberCount:F0}",
                    BuildTransparentProxyPoolEvidence(pool, openCircuitMembers),
                    state,
                    state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
                added = true;
            }
        }

        if ((TryGetProperty(root, "ConfiguredRoutes", out var configuredRoutes) ||
             TryGetProperty(root, "configuredRoutes", out configuredRoutes)) &&
            configuredRoutes.ValueKind == JsonValueKind.Array)
        {
            foreach (var route in configuredRoutes.EnumerateArray().Take(32))
            {
                if (route.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var enabled = TryGetBool(route, "Enabled") ?? TryGetBool(route, "enabled") ?? true;
                var retry = TryGetDouble(route, "RequestRetry") ?? TryGetDouble(route, "requestRetry");
                var retryInterval = TryGetDouble(route, "MaxRetryIntervalSeconds") ?? TryGetDouble(route, "maxRetryIntervalSeconds");
                var cooldown = TryGetDouble(route, "ModelCooldownSeconds") ?? TryGetDouble(route, "modelCooldownSeconds");
                var state = enabled ? HistoryStates.Passed : HistoryStates.Review;
                ProtocolResults.Add(new HistoryProtocolResult(
                    TryGetString(route, "Name") ?? TryGetString(route, "name") ?? "Configured route",
                    TryGetDouble(route, "Priority") is { } priority
                        ? priority.ToString("F0", CultureInfo.InvariantCulture)
                        : "--",
                    FormatConfiguredRouteWireApi(route),
                    BuildConfiguredRouteEvidence(route, retry, retryInterval, cooldown),
                    state,
                    enabled ? HistoryTones.Healthy : HistoryTones.Warning));
                added = true;
            }
        }

        return added;
    }

}
