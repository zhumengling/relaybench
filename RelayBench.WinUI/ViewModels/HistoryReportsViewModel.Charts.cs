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
    private static string BuildProxyTrendEvidence(
        string? summary,
        string? detail,
        string? trend24h,
        bool? hasTrendChart)
    {
        List<string> parts = [];
        AddEvidencePart(parts, trend24h is null ? null : $"24h {CompactTileDelta(trend24h)}");
        AddEvidencePart(parts, summary is null ? null : CompactTileDelta(summary));
        AddEvidencePart(parts, detail is null ? null : CompactTileDelta(detail));
        if (hasTrendChart.HasValue)
        {
            parts.Add(hasTrendChart.Value ? "chart recorded" : "chart missing");
        }

        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private void PopulateChartRowsFromPayload(JsonElement root)
    {
        PopulateBatchRankingChartRows(root);

        if (TryGetProxyTrendsSection(root, out var proxyTrends))
        {
            PopulateProxyTrendChartRows(proxyTrends);
        }

        if (TryGetProxyConcurrencySection(root, out var proxyConcurrency))
        {
            PopulateProxyConcurrencyChartRows(proxyConcurrency);
        }

        if (TryGetProxyStabilitySection(root, out var proxyStability))
        {
            PopulateProxyStabilityChartRows(proxyStability);
        }
    }

    private void PopulateBatchRankingChartRows(JsonElement root)
    {
        if (!TryGetBatchRankingArray(root, out var sites))
        {
            return;
        }

        var siteItems = sites
            .EnumerateArray()
            .Where(static site => site.ValueKind == JsonValueKind.Object)
            .Take(32)
            .Select((site, index) => new BatchRankingChartItem(
                site,
                (int)(TryGetDouble(site, "Rank") ?? TryGetDouble(site, "rank") ?? index + 1),
                TryGetString(site, "Name") ??
                TryGetString(site, "name") ??
                TryGetString(site, "BaseUrl") ??
                TryGetString(site, "baseUrl") ??
                "Site",
                TryGetString(site, "BaseUrl") ?? TryGetString(site, "baseUrl") ?? "--",
                ReadBatchRankingScore(site),
                ReadBatchRankingLatency(site),
                ReadBatchRankingTtft(site),
                ReadBatchRankingThroughput(site),
                TryGetDouble(site, "SuccessRate") ?? TryGetDouble(site, "successRate")))
            .ToArray();
        if (siteItems.Length == 0)
        {
            return;
        }

        var maxLatency = siteItems
            .Select(static site => site.ChatLatencyMs ?? site.TtftMs ?? 0)
            .Where(static value => value > 0)
            .DefaultIfEmpty(0)
            .Max();
        var maxThroughput = siteItems
            .Select(static site => site.TokensPerSecond ?? 0)
            .Where(static value => value > 0)
            .DefaultIfEmpty(0)
            .Max();

        foreach (var site in siteItems.OrderBy(static item => item.Rank))
        {
            var success = IsBatchRankingSuccessful(site.Source, site.Score);
            var state = ResolveBatchRankingState(site.Source, success);
            ChartRows.Add(new HistoryChartRow(
                $"#{site.Rank} {site.Name}",
                site.BaseUrl,
                state,
                "Score",
                site.Score.HasValue ? $"{site.Score.Value:F1}" : "--",
                ClampPercent(site.Score),
                "Latency",
                FormatMilliseconds(site.ChatLatencyMs ?? site.TtftMs),
                NormalizeInverseRatio(site.ChatLatencyMs ?? site.TtftMs, maxLatency),
                "tok/s",
                site.TokensPerSecond.HasValue ? $"{site.TokensPerSecond.Value:F1}" : "--",
                NormalizeDirectRatio(site.TokensPerSecond, maxThroughput),
                BuildBatchRankingEvidence(site.Source, site.SuccessRate, site.Score),
                success ? HistoryTones.Healthy : IsBatchRankingFailure(site.Source) ? HistoryTones.Danger : HistoryTones.Warning));
        }
    }

    private void PopulateProxyTrendChartRows(JsonElement trends)
    {
        var summary = ReadReportSectionText(trends, "summary");
        var detail = ReadReportSectionText(trends, "detail");
        var trend24h = ReadReportSectionText(trends, "trend24h", "summary24h");
        var target = ReadReportSectionText(trends, "trendTarget", "target", "baseUrl");
        var hasTrendChart = TryGetBool(trends, "hasTrendChart");
        if (string.IsNullOrWhiteSpace(summary) &&
            string.IsNullOrWhiteSpace(detail) &&
            string.IsNullOrWhiteSpace(trend24h) &&
            !hasTrendChart.HasValue)
        {
            return;
        }

        var successPercent = TryReadFirstPercent(trend24h) ?? TryReadFirstPercent(summary);
        var state = successPercent is < 80 || hasTrendChart == false ? HistoryStates.Review : HistoryStates.Passed;
        ChartRows.Add(new HistoryChartRow(
            "Proxy trend",
            target is null ? "WPF trend evidence" : CompactTileDelta(target),
            state,
            "Success",
            successPercent.HasValue ? $"{successPercent.Value:F1}%" : "--",
            ClampPercent(successPercent),
            "Chart",
            hasTrendChart.HasValue ? hasTrendChart.Value ? "Available" : "Missing" : "--",
            hasTrendChart == true ? 100 : 0,
            "24h",
            trend24h is null ? "--" : CompactTileDelta(trend24h),
            ClampPercent(successPercent),
            BuildProxyTrendEvidence(summary, detail, trend24h, hasTrendChart),
            state == HistoryStates.Passed ? HistoryTones.Healthy : HistoryTones.Warning));
    }

    private void PopulateProxyConcurrencyChartRows(JsonElement concurrency)
    {
        if (!TryGetProperty(concurrency, "stages", out var stages) ||
            stages.ValueKind != JsonValueKind.Array)
        {
            var summary = ReadReportSectionText(concurrency, "summary");
            var detail = ReadReportSectionText(concurrency, "detail", "error");
            var hasChart = TryGetBool(concurrency, "hasChart");
            var hasEvidence = !string.IsNullOrWhiteSpace(summary) ||
                              !string.IsNullOrWhiteSpace(detail) ||
                              hasChart.HasValue;
            if (!hasEvidence)
            {
                return;
            }

            ChartRows.Add(new HistoryChartRow(
                "Proxy concurrency",
                ReadReportSectionText(concurrency, "model") ?? ReadReportSectionText(concurrency, "baseUrl", "target") ?? "WPF concurrency evidence",
                hasChart == false ? HistoryStates.Review : HistoryStates.Passed,
                "Stable",
                TryGetDouble(concurrency, "stableConcurrencyLimit") is { } stable ? $"x{stable:F0}" : "--",
                TryGetDouble(concurrency, "stableConcurrencyLimit") is { } stableRatio ? ClampPercent(stableRatio * 10) : 0,
                "429",
                TryGetDouble(concurrency, "rateLimitStartConcurrency") is { } rateLimit ? $"x{rateLimit:F0}" : "--",
                TryGetDouble(concurrency, "rateLimitStartConcurrency") is { } rateLimitRatio ? ClampPercent(rateLimitRatio * 10) : 0,
                "Risk",
                TryGetDouble(concurrency, "highRiskConcurrency") is { } risk ? $"x{risk:F0}" : "--",
                TryGetDouble(concurrency, "highRiskConcurrency") is { } riskRatio ? ClampPercent(riskRatio * 10) : 0,
                BuildProxyConcurrencySummaryEvidence(concurrency, summary, detail, hasChart),
                hasChart == false ? HistoryTones.Warning : HistoryTones.Healthy));
            return;
        }

        var stageItems = stages
            .EnumerateArray()
            .Where(static stage => stage.ValueKind == JsonValueKind.Object)
            .Take(32)
            .Select(stage => new ProxyConcurrencyChartStage(
                stage,
                TryGetDouble(stage, "Concurrency") ?? TryGetDouble(stage, "concurrency"),
                TryGetDouble(stage, "TotalRequests") ?? TryGetDouble(stage, "totalRequests") ?? 0,
                TryGetDouble(stage, "SuccessCount") ?? TryGetDouble(stage, "successCount") ?? 0,
                TryGetDouble(stage, "RateLimitedCount") ?? TryGetDouble(stage, "rateLimitedCount") ?? 0,
                TryGetDouble(stage, "ServerErrorCount") ?? TryGetDouble(stage, "serverErrorCount") ?? 0,
                TryGetDouble(stage, "TimeoutCount") ?? TryGetDouble(stage, "timeoutCount") ?? 0,
                TryGetDouble(stage, "P50ChatLatencyMs") ?? TryGetDouble(stage, "p50ChatLatencyMs"),
                TryGetDouble(stage, "P50TtftMs") ?? TryGetDouble(stage, "p50TtftMs"),
                TryGetDouble(stage, "AverageTokensPerSecond") ?? TryGetDouble(stage, "averageTokensPerSecond")))
            .ToArray();
        if (stageItems.Length == 0)
        {
            return;
        }

        var maxLatency = stageItems
            .Select(static stage => stage.P50ChatLatencyMs ?? 0)
            .Where(static value => value > 0)
            .DefaultIfEmpty(0)
            .Max();
        var maxTokens = stageItems
            .Select(static stage => stage.AverageTokensPerSecond ?? 0)
            .Where(static value => value > 0)
            .DefaultIfEmpty(0)
            .Max();

        foreach (var stage in stageItems.OrderBy(static item => item.Concurrency ?? double.MaxValue))
        {
            var state = ResolveProxyConcurrencyStageState(
                stage.TotalRequests,
                stage.SuccessCount,
                stage.RateLimitedCount,
                stage.ServerErrorCount,
                stage.TimeoutCount);
            var successRate = stage.TotalRequests <= 0
                ? 0
                : Math.Clamp(stage.SuccessCount * 100 / stage.TotalRequests, 0, 100);
            ChartRows.Add(new HistoryChartRow(
                stage.Concurrency.HasValue ? $"Concurrency x{stage.Concurrency.Value:F0}" : "Concurrency stage",
                ReadReportSectionText(concurrency, "model") ?? ReadReportSectionText(concurrency, "baseUrl", "target") ?? "WPF stage",
                state,
                "Success",
                stage.TotalRequests <= 0 ? "--" : $"{successRate:F1}%",
                successRate,
                "Latency",
                FormatMilliseconds(stage.P50ChatLatencyMs),
                NormalizeInverseRatio(stage.P50ChatLatencyMs, maxLatency),
                "tok/s",
                stage.AverageTokensPerSecond.HasValue ? $"{stage.AverageTokensPerSecond.Value:F1}" : "--",
                NormalizeDirectRatio(stage.AverageTokensPerSecond, maxTokens),
                BuildProxyConcurrencyStageEvidence(
                    stage.Source,
                    stage.TotalRequests,
                    stage.SuccessCount,
                    stage.RateLimitedCount,
                    stage.ServerErrorCount,
                    stage.TimeoutCount),
                state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
        }
    }

    private void PopulateProxyStabilityChartRows(JsonElement stability)
    {
        var healthScore = TryGetDouble(stability, "healthScore");
        var healthLabel = ReadReportSectionText(stability, "healthLabel");
        var fullSuccess = TryGetDouble(stability, "fullSuccessRate");
        var semantic = TryGetDouble(stability, "semanticStabilityRate");
        var avgChat = TryGetDouble(stability, "averageChatLatencyMs");
        var state = ResolveProxyStabilityState(healthScore, healthLabel);
        if (healthScore.HasValue ||
            fullSuccess.HasValue ||
            semantic.HasValue ||
            avgChat.HasValue ||
            !string.IsNullOrWhiteSpace(healthLabel))
        {
            ChartRows.Add(new HistoryChartRow(
                "Stability health",
                healthLabel ?? "WPF stability evidence",
                state,
                "Health",
                healthScore.HasValue ? $"{healthScore.Value:F0}" : "--",
                ClampPercent(healthScore),
                "Full",
                fullSuccess.HasValue ? $"{fullSuccess.Value:F1}%" : "--",
                ClampPercent(fullSuccess),
                "Semantic",
                semantic.HasValue ? $"{semantic.Value:F1}%" : "--",
                ClampPercent(semantic),
                BuildProxyStabilitySummaryEvidence(stability),
                state == HistoryStates.Passed ? HistoryTones.Healthy : state == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
        }

        if (!TryGetProperty(stability, "failureDistribution", out var failures) ||
            failures.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var failureItems = failures
            .EnumerateArray()
            .Where(static failure => failure.ValueKind == JsonValueKind.Object)
            .Take(32)
            .Select(failure => new ProxyStabilityFailureChartItem(
                failure,
                TryGetString(failure, "failureKind") ?? TryGetString(failure, "FailureKind") ?? "Failure",
                TryGetDouble(failure, "Count") ?? TryGetDouble(failure, "count") ?? 0,
                TryGetDouble(failure, "Rate") ?? TryGetDouble(failure, "rate") ?? 0))
            .ToArray();
        var maxCount = failureItems
            .Select(static failure => failure.Count)
            .Where(static value => value > 0)
            .DefaultIfEmpty(0)
            .Max();
        foreach (var failure in failureItems)
        {
            var rowState = failure.Rate >= 50 || failure.Count >= 5 ? HistoryStates.Failed : HistoryStates.Review;
            ChartRows.Add(new HistoryChartRow(
                failure.Kind,
                "Failure distribution",
                rowState,
                "Rate",
                $"{failure.Rate:F1}%",
                ClampPercent(failure.Rate),
                "Count",
                FormatCompactNumber(failure.Count),
                NormalizeDirectRatio(failure.Count, maxCount),
                "Impact",
                rowState,
                rowState == HistoryStates.Failed ? 100 : 45,
                BuildProxyStabilityFailureEvidence(failure.Source),
                rowState == HistoryStates.Failed ? HistoryTones.Danger : HistoryTones.Warning));
        }
    }

    private static string BuildProxySingleScenarioEvidence(JsonElement scenario)
    {
        List<string> parts = [];
        if ((TryGetDouble(scenario, "StatusCode") ?? TryGetDouble(scenario, "statusCode")) is { } statusCode)
        {
            parts.Add(statusCode.ToString("F0", CultureInfo.InvariantCulture));
        }

        if ((TryGetDouble(scenario, "ChunkCount") ?? TryGetDouble(scenario, "chunkCount")) is { } chunks)
        {
            parts.Add($"{chunks:F0} chunks");
        }

        if ((TryGetDouble(scenario, "OutputTokensPerSecond") ?? TryGetDouble(scenario, "outputTokensPerSecond")) is { } outputTokensPerSecond)
        {
            parts.Add($"{outputTokensPerSecond:F1} tok/s");
        }

        if ((TryGetDouble(scenario, "MaxChunkGapMilliseconds") ?? TryGetDouble(scenario, "maxChunkGapMilliseconds")) is { } maxChunkGap)
        {
            parts.Add($"gap {FormatMilliseconds(maxChunkGap)}");
        }

        if ((TryGetBool(scenario, "SemanticMatch") ?? TryGetBool(scenario, "semanticMatch")) == false)
        {
            parts.Add("semantic mismatch");
        }

        AddEvidencePart(parts, TryGetString(scenario, "failureKind") ?? TryGetString(scenario, "FailureKind"));
        AddEvidencePart(parts, TryGetString(scenario, "Error") ?? TryGetString(scenario, "error"));
        AddEvidencePart(parts, TryGetString(scenario, "Summary") ?? TryGetString(scenario, "summary"));
        AddEvidencePart(parts, TryGetString(scenario, "TraceId") ?? TryGetString(scenario, "traceId"));
        AddEvidencePart(parts, TryGetString(scenario, "RequestId") ?? TryGetString(scenario, "requestId"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string BuildProxyModelCatalogEvidence(string? summary, string? detail)
    {
        List<string> parts = [];
        AddEvidencePart(parts, summary);
        AddEvidencePart(parts, detail);
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string ResolveClientApiCheckState(JsonElement check, bool reachable)
    {
        var verdict = TryGetString(check, "Verdict") ?? TryGetString(check, "verdict") ?? string.Empty;
        var error = TryGetString(check, "Error") ?? TryGetString(check, "error") ?? string.Empty;
        var installed = TryGetBool(check, "Installed") ?? TryGetBool(check, "installed");
        if (reachable || IsNetworkStatusPassing(verdict))
        {
            return HistoryStates.Passed;
        }

        if (installed == false ||
            IsNetworkStatusFailed(verdict) ||
            IsNetworkStatusFailed(error) ||
            verdict.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            return HistoryStates.Failed;
        }

        return HistoryStates.Review;
    }

    private static string BuildApplicationAccessEvidence(JsonElement item)
    {
        List<string> parts = [];
        AddEvidencePart(parts, TryGetString(item, "Action") ?? TryGetString(item, "action"));
        AddEvidencePart(parts, TryGetString(item, "Summary") ?? TryGetString(item, "summary"));
        AddEvidencePart(parts, ReadStringArrayEvidence(item, "Changed", "ChangedFiles", "changedFiles"));
        AddEvidencePart(parts, ReadStringArrayEvidence(item, "Backups", "BackupFiles", "backupFiles"));
        AddEvidencePart(parts, TryGetString(item, "Error") ?? TryGetString(item, "error"));
        if (TryGetProperty(item, "Probe", out var probe) && probe.ValueKind == JsonValueKind.Object)
        {
            AddEvidencePart(parts, TryGetString(probe, "Summary") ?? TryGetString(probe, "summary"));
            AddEvidencePart(parts, TryGetString(probe, "Error") ?? TryGetString(probe, "error"));
        }

        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string BuildClientApiCheckEvidence(JsonElement check)
    {
        List<string> parts = [];
        var probeMethod = TryGetString(check, "ProbeMethod") ?? TryGetString(check, "probeMethod");
        var probeUrl = TryGetString(check, "ProbeUrl") ?? TryGetString(check, "probeUrl");
        if (!string.IsNullOrWhiteSpace(probeUrl))
        {
            parts.Add(string.IsNullOrWhiteSpace(probeMethod) ? probeUrl : $"{probeMethod} {probeUrl}");
        }

        AddEvidencePart(parts, TryGetString(check, "EndpointLabel") ?? TryGetString(check, "endpointLabel"));
        AddEvidencePart(parts, TryGetString(check, "ConfigSource") ?? TryGetString(check, "configSource"));
        AddEvidencePart(parts, TryGetString(check, "ProxySource") ?? TryGetString(check, "proxySource"));
        AddEvidencePart(parts, TryGetString(check, "RoutingNote") ?? TryGetString(check, "routingNote"));
        AddEvidencePart(parts, TryGetString(check, "Evidence") ?? TryGetString(check, "evidence"));
        AddEvidencePart(parts, TryGetString(check, "Summary") ?? TryGetString(check, "summary"));
        AddEvidencePart(parts, TryGetString(check, "Error") ?? TryGetString(check, "error"));
        AddEvidencePart(parts, TryGetString(check, "RestoreHint") ?? TryGetString(check, "restoreHint"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string? ReadStringArrayEvidence(JsonElement root, string label, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(root, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = array.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()?.Trim() ?? string.Empty)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (values.Length == 0)
            {
                continue;
            }

            var visible = string.Join(", ", values.Take(4));
            var suffix = values.Length > 4 ? $" +{values.Length - 4}" : string.Empty;
            return $"{label}: {visible}{suffix}";
        }

        return null;
    }

    private static string ResolveStunState(string? summary, string? recommendation)
    {
        var combined = $"{summary} {recommendation}";
        if (IsNetworkStatusFailed(combined))
        {
            return HistoryStates.Failed;
        }

        return combined.Contains("review", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains(HistoryStates.Review, StringComparison.OrdinalIgnoreCase)
            ? HistoryStates.Review
            : HistoryStates.Passed;
    }

    private static string BuildStunEvidence(
        string? summary,
        string? coverage,
        string? test,
        string? attributes,
        string? recommendation)
    {
        List<string> parts = [];
        AddEvidencePart(parts, summary);
        AddEvidencePart(parts, coverage);
        AddEvidencePart(parts, test);
        AddEvidencePart(parts, attributes);
        AddEvidencePart(parts, recommendation);
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string ResolveUnlockCatalogState(string? summary, string? catalogSummary, JsonElement? semanticCounts)
    {
        var combined = $"{summary} {catalogSummary}";
        if (IsNetworkStatusFailed(combined))
        {
            return HistoryStates.Failed;
        }

        if (semanticCounts.HasValue)
        {
            var ready = TryGetDouble(semanticCounts.Value, "ReadyCount") ?? 0;
            var blocked = (TryGetDouble(semanticCounts.Value, "AuthRequiredCount") ?? 0) +
                          (TryGetDouble(semanticCounts.Value, "RegionRestrictedCount") ?? 0) +
                          (TryGetDouble(semanticCounts.Value, "ReviewRequiredCount") ?? 0);
            if (ready > 0 && blocked <= 0)
            {
                return HistoryStates.Passed;
            }

            if (ready > 0)
            {
                return HistoryStates.Review;
            }
        }

        return string.IsNullOrWhiteSpace(combined) ? HistoryStates.Review : HistoryStates.Passed;
    }

    private static string BuildUnlockCatalogEvidence(
        string? summary,
        string? catalogSummary,
        string? catalogDetail,
        string? rawTrace,
        JsonElement? semanticCounts)
    {
        List<string> parts = [];
        if (semanticCounts.HasValue)
        {
            var ready = TryGetDouble(semanticCounts.Value, "ReadyCount") ?? 0;
            var auth = TryGetDouble(semanticCounts.Value, "AuthRequiredCount") ?? 0;
            var region = TryGetDouble(semanticCounts.Value, "RegionRestrictedCount") ?? 0;
            var review = TryGetDouble(semanticCounts.Value, "ReviewRequiredCount") ?? 0;
            var total = TryGetDouble(semanticCounts.Value, "TotalCount") ?? 0;
            parts.Add($"ready {ready:F0}/{total:F0}");
            if (auth > 0)
            {
                parts.Add($"auth {auth:F0}");
            }

            if (region > 0)
            {
                parts.Add($"region {region:F0}");
            }

            if (review > 0)
            {
                parts.Add($"review {review:F0}");
            }
        }

        AddEvidencePart(parts, summary);
        AddEvidencePart(parts, catalogSummary);
        AddEvidencePart(parts, catalogDetail);
        AddEvidencePart(parts, rawTrace);
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string ResolveLegacyRouteState(string? summary, string? rawOutput)
    {
        var combined = $"{summary} {rawOutput}";
        if (IsNetworkStatusFailed(combined))
        {
            return HistoryStates.Failed;
        }

        return string.IsNullOrWhiteSpace(combined) ? HistoryStates.Review : HistoryStates.Passed;
    }

    private static string BuildLegacyRouteEvidence(
        string? summary,
        string? mapSummary,
        string? geoSummary,
        string? hopSummary,
        string? rawOutput)
    {
        List<string> parts = [];
        AddEvidencePart(parts, summary);
        AddEvidencePart(parts, mapSummary);
        AddEvidencePart(parts, geoSummary);
        AddEvidencePart(parts, hopSummary);
        AddEvidencePart(parts, rawOutput);
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string BuildLegacySpeedEvidence(JsonElement speed)
    {
        List<string> parts = [];
        AddEvidencePart(parts, ReadReportSectionText(speed, "summary"));
        AddEvidencePart(parts, ReadReportSectionText(speed, "latencyDetail"));
        AddEvidencePart(parts, ReadReportSectionText(speed, "transferDetail"));
        AddEvidencePart(parts, ReadReportSectionText(speed, "packetLossDetail"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string BuildLegacySplitRoutingEvidence(JsonElement splitRouting, params string[] propertyNames)
    {
        List<string> parts = [];
        foreach (var propertyName in propertyNames)
        {
            AddEvidencePart(parts, ReadReportSectionText(splitRouting, propertyName));
        }

        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string ResolveLegacyNetworkTextState(params string?[] values)
    {
        var combined = string.Join(" ", values.Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (IsLegacyNetworkFailure(combined))
        {
            return HistoryStates.Failed;
        }

        if (IsLegacyNetworkReview(combined))
        {
            return HistoryStates.Review;
        }

        return string.IsNullOrWhiteSpace(combined) ? HistoryStates.Review : HistoryStates.Passed;
    }

    private static bool IsLegacyNetworkFailure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("unreachable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyNetworkReview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("review", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("partial", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("suspect", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("issue", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("risk", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLegacyPortScanSummaryEvidence(JsonElement portScan)
    {
        List<string> parts = [];
        AddEvidencePart(parts, ReadReportSectionText(portScan, "summary"));
        AddEvidencePart(parts, ReadReportSectionText(portScan, "detail"));
        AddEvidencePart(parts, ReadReportSectionText(portScan, "batchSummary"));
        AddEvidencePart(parts, ReadReportSectionText(portScan, "exportSummary"));
        AddEvidencePart(parts, ReadReportSectionText(portScan, "progressSummary"));
        AddEvidencePart(parts, ReadReportSectionText(portScan, "rawOutput"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string BuildLegacyPortScanFindingName(JsonElement finding, string? target = null)
    {
        var endpoint = TryGetString(finding, "Endpoint") ?? TryGetString(finding, "endpoint");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        var address = TryGetString(finding, "Address") ?? TryGetString(finding, "address") ?? target;
        var port = TryGetDouble(finding, "Port") ?? TryGetDouble(finding, "port");
        if (!string.IsNullOrWhiteSpace(address) && port.HasValue)
        {
            return $"{address}:{port.Value:F0}";
        }

        return string.IsNullOrWhiteSpace(address) ? "Port finding" : address;
    }

    private static string BuildLegacyPortScanFindingProtocol(JsonElement finding)
    {
        var protocol = TryGetString(finding, "Protocol") ?? TryGetString(finding, "protocol") ?? "--";
        var service = TryGetString(finding, "ServiceHint") ?? TryGetString(finding, "serviceHint");
        return string.IsNullOrWhiteSpace(service) ||
               string.Equals(protocol, service, StringComparison.OrdinalIgnoreCase)
            ? protocol
            : $"{protocol}/{service}";
    }

    private static string BuildLegacyPortScanFindingEvidence(JsonElement finding)
    {
        List<string> parts = [];
        AddEvidencePart(parts, TryGetString(finding, "Address") ?? TryGetString(finding, "address"));
        AddEvidencePart(parts, TryGetString(finding, "ServiceHint") ?? TryGetString(finding, "serviceHint"));
        AddEvidencePart(parts, TryGetString(finding, "Banner") ?? TryGetString(finding, "banner"));
        AddEvidencePart(parts, TryGetString(finding, "TlsSummary") ?? TryGetString(finding, "tlsSummary"));
        AddEvidencePart(parts, TryGetString(finding, "HttpSummary") ?? TryGetString(finding, "httpSummary"));
        AddEvidencePart(parts, ReadReportSectionText(finding, "ProbeNotes", "probeNotes"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string BuildLegacyPortScanBatchEvidence(JsonElement row)
    {
        List<string> parts = [];
        if ((TryGetDouble(row, "OpenEndpointCount") ?? TryGetDouble(row, "openEndpointCount")) is { } endpoints)
        {
            parts.Add($"{endpoints:F0} open endpoints");
        }

        if ((TryGetDouble(row, "OpenPortCount") ?? TryGetDouble(row, "openPortCount")) is { } ports)
        {
            parts.Add($"{ports:F0} open ports");
        }

        AddEvidencePart(parts, ReadReportSectionText(row, "ResolvedAddresses", "resolvedAddresses"));
        AddEvidencePart(parts, ReadReportSectionText(row, "Summary", "summary"));
        AddEvidencePart(parts, ReadReportSectionText(row, "Error", "error"));
        AddEvidencePart(parts, ReadReportSectionText(row, "CheckedAt", "checkedAt"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string ResolveLegacyPortScanState(string? status, string? error, double? openCount, bool finding)
    {
        var combined = $"{status} {error}";
        if (IsNetworkStatusFailed(combined) ||
            combined.Contains(HistoryStates.Failed, StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return HistoryStates.Failed;
        }

        if (combined.Contains("review", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains(HistoryStates.Review, StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("partial", StringComparison.OrdinalIgnoreCase))
        {
            return HistoryStates.Review;
        }

        if (finding ||
            (openCount.HasValue && openCount.Value > 0) ||
            IsNetworkStatusPassing(combined))
        {
            return HistoryStates.Passed;
        }

        return string.IsNullOrWhiteSpace(combined) ? HistoryStates.Review : TranslateNetworkState(combined, success: true);
    }

    private static string ResolveProxyConcurrencyStageState(
        double total,
        double success,
        double rateLimited,
        double serverErrors,
        double timeouts)
    {
        if (total > 0 && success <= 0)
        {
            return HistoryStates.Failed;
        }

        return rateLimited > 0 ||
               serverErrors > 0 ||
               timeouts > 0 ||
               (total > 0 && success < total)
            ? HistoryStates.Review
            : HistoryStates.Passed;
    }

    private static string BuildProxyConcurrencyStageEvidence(
        JsonElement stage,
        double total,
        double success,
        double rateLimited,
        double serverErrors,
        double timeouts)
    {
        List<string> parts = [];
        if (total > 0 || success > 0)
        {
            parts.Add($"{success:F0}/{total:F0} ok");
        }

        if (rateLimited > 0)
        {
            parts.Add($"429 {rateLimited:F0}");
        }

        if (serverErrors > 0)
        {
            parts.Add($"5xx {serverErrors:F0}");
        }

        if (timeouts > 0)
        {
            parts.Add($"timeout {timeouts:F0}");
        }

        if ((TryGetDouble(stage, "AverageTokensPerSecond") ?? TryGetDouble(stage, "averageTokensPerSecond")) is { } tokensPerSecond)
        {
            parts.Add($"{tokensPerSecond:F1} tok/s");
        }

        if ((TryGetDouble(stage, "P95ChatLatencyMs") ?? TryGetDouble(stage, "p95ChatLatencyMs")) is { } p95ChatLatency)
        {
            parts.Add($"p95 {FormatMilliseconds(p95ChatLatency)}");
        }

        AddEvidencePart(parts, TryGetString(stage, "Summary") ?? TryGetString(stage, "summary"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string BuildProxyConcurrencySummaryEvidence(
        JsonElement concurrency,
        string? summary,
        string? detail,
        bool? hasChart)
    {
        List<string> parts = [];
        if (TryGetDouble(concurrency, "stableConcurrencyLimit") is { } stable)
        {
            parts.Add($"stable x{stable:F0}");
        }

        if (TryGetDouble(concurrency, "rateLimitStartConcurrency") is { } rateLimit)
        {
            parts.Add($"429 at x{rateLimit:F0}");
        }

        if (TryGetDouble(concurrency, "highRiskConcurrency") is { } highRisk)
        {
            parts.Add($"risk at x{highRisk:F0}");
        }

        AddEvidencePart(parts, summary);
        AddEvidencePart(parts, detail);
        if (hasChart.HasValue)
        {
            parts.Add(hasChart.Value ? "chart recorded" : "chart missing");
        }

        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string ResolveProxyStabilityState(double? healthScore, string? healthLabel)
    {
        if (!string.IsNullOrWhiteSpace(healthLabel))
        {
            if (healthLabel.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                healthLabel.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                healthLabel.Contains("unhealthy", StringComparison.OrdinalIgnoreCase))
            {
                return HistoryStates.Failed;
            }

            if (healthLabel.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
                healthLabel.Contains("review", StringComparison.OrdinalIgnoreCase) ||
                healthLabel.Contains("degrad", StringComparison.OrdinalIgnoreCase))
            {
                return HistoryStates.Review;
            }
        }

        if (healthScore is < 60)
        {
            return HistoryStates.Failed;
        }

        return healthScore is < 85 ? HistoryStates.Review : HistoryStates.Passed;
    }

    private static string BuildProxyStabilityFailureEvidence(JsonElement failure)
    {
        List<string> parts = [];
        AddEvidencePart(parts, TryGetString(failure, "Summary") ?? TryGetString(failure, "summary"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static string BuildProxyStabilitySummaryEvidence(JsonElement stability)
    {
        List<string> parts = [];
        if (TryGetDouble(stability, "fullSuccessRate") is { } fullSuccessRate)
        {
            parts.Add($"full {fullSuccessRate:F1}%");
        }

        if (TryGetDouble(stability, "semanticStabilityRate") is { } semanticRate)
        {
            parts.Add($"semantic {semanticRate:F1}%");
        }

        if (TryGetDouble(stability, "maxConsecutiveFailures") is { } maxFailures)
        {
            parts.Add($"max failures {maxFailures:F0}");
        }

        AddEvidencePart(parts, ReadReportSectionText(stability, "failureDistributionSummary"));
        AddEvidencePart(parts, ReadReportSectionText(stability, "cdnStabilitySummary"));
        AddEvidencePart(parts, ReadReportSectionText(stability, "insightSummary", "summary", "detail"));
        return parts.Count == 0 ? "--" : string.Join(" | ", parts);
    }

    private static void AddEvidencePart(ICollection<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(CompactTileDelta(value));
        }
    }

    private static double? ReadTotalInputTokens(JsonElement root)
        => TryGetDouble(root, "TotalInputTokens") ??
           TryGetDouble(root, "totalInputTokens") ??
           TryGetDouble(root, "InputTokens") ??
           TryGetDouble(root, "inputTokens");

    private static double? ReadTotalOutputTokens(JsonElement root)
        => TryGetDouble(root, "TotalOutputTokens") ??
           TryGetDouble(root, "totalOutputTokens") ??
           TryGetDouble(root, "OutputTokens") ??
           TryGetDouble(root, "outputTokens");

    private static (double Total, double Success, double Failed) ReadRouteHitCounters(JsonElement routeHits)
    {
        if (routeHits.ValueKind != JsonValueKind.Array)
        {
            return (0, 0, 0);
        }

        double total = 0;
        double success = 0;
        double failed = 0;
        foreach (var route in routeHits.EnumerateArray())
        {
            total += TryGetDouble(route, "Sent") ?? TryGetDouble(route, "sent") ?? 0;
            success += TryGetDouble(route, "Success") ?? TryGetDouble(route, "success") ?? 0;
            failed += TryGetDouble(route, "Failed") ?? TryGetDouble(route, "failed") ?? 0;
        }

        if (total <= 0 && (success > 0 || failed > 0))
        {
            total = success + failed;
        }

        if (success <= 0 && total > 0 && failed > 0)
        {
            success = Math.Max(0, total - failed);
        }

        if (failed <= 0 && total > 0 && success > 0)
        {
            failed = Math.Max(0, total - success);
        }

        return (Math.Max(0, total), Math.Max(0, success), Math.Max(0, failed));
    }

    private static double? ReadPromptCacheTokens(JsonElement root)
        => TryGetDouble(root, "PromptCacheTokens") ??
           TryGetDouble(root, "promptCacheTokens") ??
           TryGetDouble(root, "CachedTokens") ??
           TryGetDouble(root, "cachedTokens");

    private static double ReadCacheHitRate(JsonElement root, double inputTokens, double promptCacheTokens)
    {
        var recorded = TryGetDouble(root, "CacheHitRate") ?? TryGetDouble(root, "cacheHitRate");
        if (recorded.HasValue)
        {
            return recorded.Value;
        }

        return inputTokens > 0 && promptCacheTokens > 0
            ? promptCacheTokens / Math.Max(1, inputTokens) * 100
            : 0;
    }

    private static double? ReadLatencySummary(JsonElement? root, params string[] names)
    {
        if (!root.HasValue)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetDouble(root.Value, name) is { } value)
            {
                return value;
            }
        }

        if (TryGetProperty(root.Value, "latencies", out var latencies))
        {
            return TryGetDouble(latencies, "chatMs") ??
                   TryGetDouble(latencies, "modelsMs") ??
                   TryGetDouble(latencies, "ttftMs");
        }

        return null;
    }

    private static double? ReadThroughputSummary(JsonElement? root, params string[] names)
    {
        if (!root.HasValue)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetDouble(root.Value, name) is { } value)
            {
                return value;
            }
        }

        if (TryGetProperty(root.Value, "throughput", out var throughput))
        {
            foreach (var name in names)
            {
                if (TryGetDouble(throughput, name) is { } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

}
