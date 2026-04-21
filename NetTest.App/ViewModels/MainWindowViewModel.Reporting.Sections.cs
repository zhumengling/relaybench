using NetTest.App.Infrastructure;
using NetTest.App.Services;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<DiagnosticReportSection> BuildReportSections(string overviewStatus, string conclusionSummary)
    {
        return
        [
            new("结论摘要", conclusionSummary),
            new("总览", $"状态：{overviewStatus}\n上次运行：{LastRunAt}\n\n{PlannedModulesSummary}"),
            new("网络检测", $"{NetworkSummary}\n\n{AdapterSummary}\n\n{PingSummary}"),
            new("官方 API 可用性", $"{ChatGptSummary}\n\n扩展可用性目录：\n{UnlockCatalogSummary}\n\n原始 Trace：\n{ChatGptRawTrace}"),
            new("扩展可用性目录", $"{UnlockCatalogSummary}\n\n{UnlockCatalogDetail}"),
            new("客户端 API 联通鉴定", $"{ClientApiSummary}\n\n{ClientApiDetail}"),
            new("STUN NAT 分类测试", $"{StunSummary}\n\n覆盖与复核：\n{StunCoverageSummary}\n\n分类测试过程：\n{StunTestSummary}\n\n属性详情：\n{StunAttributeSummary}"),
            new("中转站模型列表", $"{ProxyModelCatalogSummary}\n\n{ProxyModelCatalogDetail}"),
            new("单站测试", $"{ProxyVerdictSummary}\n\n{ProxyCapabilityMatrixSummary}\n\n{ProxyKeyMetricsSummary}\n\n已管理入口参照：\n{ProxyManagedEntryAssessmentSummary}\n\n{ProxyIssueSummary}\n\n关键响应头：\n{ProxyHeadersSummary}\n\n原始摘要：\n{ProxySummary}\n\n原始明细：\n{ProxyDetail}"),
            new("单站测试（稳定性）", $"{ProxyStabilityInsightSummary}\n\n{ProxyStabilitySummary}\n\n{ProxyStabilityDetail}"),
            new("批量对比", $"{ProxyBatchRecommendationSummary}\n\n{ProxyBatchSummary}\n\n{ProxyBatchDetail}"),
            new("中转站趋势", $"{ProxyTrendSummary}\n\n{ProxyTrendDetail}"),
            new("测速", $"{SpeedTestSummary}\n\n{SpeedTestLatencyDetail}\n\n{SpeedTestTransferDetail}\n\n{SpeedTestPacketLossDetail}"),
            new("路由与 MTR", $"{RouteSummary}\n\n{RouteMapSummary}\n\n{RouteGeoSummary}\n\n{RouteHopSummary}\n\n原始输出：\n{RouteRawOutput}"),
            new("IP 与分流检测", $"{SplitRoutingSummary}\n\n{SplitRoutingIpInsightSummary}\n\n{SplitRoutingAdapterSummary}\n\n{SplitRoutingExitSummary}\n\n{SplitRoutingDnsSummary}\n\n{SplitRoutingReachabilitySummary}"),
            new("端口扫描", $"{PortScanSummary}\n\n批量摘要：\n{PortScanBatchSummary}\n\n导出摘要：\n{PortScanExportSummary}\n\n{PortScanDetail}\n\n原始输出：\n{PortScanRawOutput}"),
            new("历史报告", HistorySummary)
        ];
    }

    private IReadOnlyList<DiagnosticReportTextArtifact> BuildReportTextArtifacts(string conclusionSummary)
    {
        return
        [
            new("raw/conclusions.txt", NormalizeArtifactContent(conclusionSummary), "结论摘要"),
            new("raw/chatgpt-trace.txt", NormalizeArtifactContent(ChatGptRawTrace), "网络复核 / 官方 API Trace 原始输出"),
            new("raw/client-api.txt", NormalizeArtifactContent($"{ClientApiSummary}\n\n{ClientApiDetail}"), "网络复核 / 客户端 API 联通鉴定原始结果"),
            new("raw/unlock-catalog.txt", NormalizeArtifactContent($"{UnlockCatalogSummary}\n\n{UnlockCatalogDetail}"), "扩展可用性目录原始摘要"),
            new("raw/stun-tests.txt", NormalizeArtifactContent($"{StunSummary}\n\n{StunCoverageSummary}\n\n{StunTestSummary}\n\n{StunAttributeSummary}"), "网络复核 / STUN 与 NAT 分类原始结果"),
            new("raw/proxy-model-catalog.txt", NormalizeArtifactContent($"{ProxyModelCatalogSummary}\n\n{ProxyModelCatalogDetail}"), "中转站模型列表原始结果"),
            new("raw/proxy-single.txt", NormalizeArtifactContent($"{ProxyVerdictSummary}\n\n{ProxyCapabilityMatrixSummary}\n\n{ProxyKeyMetricsSummary}\n\n{ProxyManagedEntryAssessmentSummary}\n\n{ProxyIssueSummary}\n\n{ProxyHeadersSummary}\n\n{ProxySummary}\n\n{ProxyDetail}"), "单站测试原始结果"),
            new("raw/proxy-stability.txt", NormalizeArtifactContent($"{ProxyStabilityInsightSummary}\n\n{ProxyStabilitySummary}\n\n{ProxyStabilityDetail}"), "单站测试稳定性原始结果"),
            new("raw/proxy-batch.txt", NormalizeArtifactContent($"{ProxyBatchRecommendationSummary}\n\n{ProxyBatchSummary}\n\n{ProxyBatchDetail}"), "批量对比原始结果"),
            new("raw/proxy-trends.txt", NormalizeArtifactContent($"{ProxyTrendSummary}\n\n{ProxyTrendDetail}"), "中转站趋势原始结果"),
            new("raw/route-output.txt", NormalizeArtifactContent($"{RouteSummary}\n\n{RouteMapSummary}\n\n{RouteGeoSummary}\n\n{RouteHopSummary}\n\n{RouteRawOutput}"), "网络复核 / 路由与 MTR 原始输出"),
            new("raw/port-scan-output.txt", NormalizeArtifactContent($"{PortScanSummary}\n\n{PortScanBatchSummary}\n\n{PortScanExportSummary}\n\n{PortScanDetail}\n\n{PortScanRawOutput}"), "端口扫描原始输出")
        ];
    }

    private IReadOnlyList<DiagnosticReportImageArtifact> BuildReportImageArtifacts()
    {
        List<DiagnosticReportImageArtifact> artifacts = [];
        if (RouteMapImage is not null)
        {
            artifacts.Add(new DiagnosticReportImageArtifact("media/route-map.png", RouteMapImage, "\u771F\u5B9E\u5730\u56FE\u8DEF\u7531\u622A\u56FE"));
        }

        if (ProxyTrendChartImage is not null)
        {
            artifacts.Add(new DiagnosticReportImageArtifact("media/proxy-trend-chart.png", ProxyTrendChartImage, "\u4E2D\u8F6C\u7AD9\u8D8B\u52BF\u56FE"));
        }

        return artifacts;
    }

    private object BuildStructuredReportPayload(
        string overviewStatus,
        string conclusionSummary,
        RelayRecommendationSnapshot relayRecommendation,
        TrendSummarySnapshot trendSnapshot,
        UnlockSemanticSnapshot unlockSnapshot,
        NatReviewSnapshot natSnapshot,
        IReadOnlyList<DiagnosticReportSection> sections,
        IReadOnlyList<DiagnosticReportTextArtifact> textArtifacts,
        IReadOnlyList<DiagnosticReportImageArtifact> imageArtifacts)
    {
        return new
        {
            generatedAt = DateTimeOffset.Now,
            overview = new
            {
                status = overviewStatus,
                lastRunAt = LastRunAt,
                plannedModulesSummary = PlannedModulesSummary
            },
            conclusions = new
            {
                summary = conclusionSummary,
                recommendedRelay = new
                {
                    relayRecommendation.Source,
                    relayRecommendation.Name,
                    relayRecommendation.BaseUrl,
                    relayRecommendation.Score,
                    relayRecommendation.Summary
                },
                past24Hours = new
                {
                    trendSnapshot.Target,
                    trendSnapshot.SampleCount,
                    trendSnapshot.Summary,
                    trendSnapshot.EarlierStability,
                    trendSnapshot.LaterStability,
                    trendSnapshot.EarlierChatLatency,
                    trendSnapshot.LaterChatLatency,
                    trendSnapshot.EarlierTtft,
                    trendSnapshot.LaterTtft
                },
                unlock = new
                {
                    unlockSnapshot.Summary,
                    unlockSnapshot.ReadyCount,
                    unlockSnapshot.AuthRequiredCount,
                    unlockSnapshot.RegionRestrictedCount,
                    unlockSnapshot.ReviewRequiredCount,
                    unlockSnapshot.TotalCount
                },
                nat = new
                {
                    natSnapshot.Summary,
                    natSnapshot.NatType,
                    natSnapshot.Confidence,
                    natSnapshot.CoverageSummary,
                    natSnapshot.ReviewRecommendation
                }
            },
            chatgptUnlock = new
            {
                summary = ChatGptSummary,
                rawTrace = ChatGptRawTrace,
                unlockCatalogSummary = UnlockCatalogSummary,
                unlockCatalogDetail = UnlockCatalogDetail,
                semanticCounts = new
                {
                    unlockSnapshot.ReadyCount,
                    unlockSnapshot.AuthRequiredCount,
                    unlockSnapshot.RegionRestrictedCount,
                    unlockSnapshot.ReviewRequiredCount,
                    unlockSnapshot.TotalCount
                }
            },
            clientApi = new
            {
                summary = ClientApiSummary,
                detail = ClientApiDetail,
                checkedAt = _lastClientApiDiagnosticsResult?.CheckedAt,
                installedCount = _lastClientApiDiagnosticsResult?.InstalledCount,
                configuredCount = _lastClientApiDiagnosticsResult?.ConfiguredCount,
                reachableCount = _lastClientApiDiagnosticsResult?.ReachableCount,
                checks = _lastClientApiDiagnosticsResult?.Checks.Select(check => new
                {
                    check.Name,
                    check.Provider,
                    check.Kind,
                    check.ProbeUrl,
                    check.ProbeMethod,
                    check.Installed,
                    check.ConfigDetected,
                    check.InstallEvidence,
                    check.ConfigSource,
                    check.ProxySource,
                    check.AccessPathLabel,
                    check.ConfigOriginLabel,
                    check.EndpointLabel,
                    check.RoutingNote,
                    check.RestoreSupported,
                    check.RestoreHint,
                    check.Reachable,
                    check.StatusCode,
                    latencyMs = check.Latency?.TotalMilliseconds,
                    check.Verdict,
                    check.Summary,
                    check.Evidence,
                    check.Error
                }).ToArray()
            },
            stun = new
            {
                summary = StunSummary,
                coverageSummary = StunCoverageSummary,
                attributeSummary = StunAttributeSummary,
                testSummary = StunTestSummary,
                natType = _lastStunResult?.NatType,
                confidence = _lastStunResult?.ClassificationConfidence,
                reviewRecommendation = _lastStunResult?.ReviewRecommendation
            },
            proxy = new
            {
                modelCatalog = new
                {
                    summary = ProxyModelCatalogSummary,
                    detail = ProxyModelCatalogDetail
                },
                single = new
                {
                    summary = ProxySummary,
                    detail = ProxyDetail,
                    verdictSummary = ProxyVerdictSummary,
                    capabilityMatrixSummary = ProxyCapabilityMatrixSummary,
                    keyMetricsSummary = ProxyKeyMetricsSummary,
                    longStreamingSummary = ProxyLongStreamingSummary,
                    traceabilitySummary = ProxyTraceabilitySummary,
                    managedEntryAssessmentSummary = ProxyManagedEntryAssessmentSummary,
                    issueSummary = ProxyIssueSummary,
                    headersSummary = ProxyHeadersSummary,
                    verdict = _lastProxySingleResult?.Verdict,
                    recommendation = _lastProxySingleResult?.Recommendation,
                    primaryIssue = _lastProxySingleResult?.PrimaryIssue,
                    primaryFailureKind = _lastProxySingleResult?.PrimaryFailureKind?.ToString(),
                    resolvedAddresses = _lastProxySingleResult?.ResolvedAddresses,
                    cdnProvider = _lastProxySingleResult?.CdnProvider,
                    edgeSignature = _lastProxySingleResult?.EdgeSignature,
                    cdnSummary = _lastProxySingleResult?.CdnSummary,
                    requestId = _lastProxySingleResult?.RequestId,
                    traceId = _lastProxySingleResult?.TraceId,
                    traceability = _lastProxySingleResult?.TraceabilitySummary,
                    longStreaming = _lastProxySingleResult?.LongStreamingResult is null
                        ? null
                        : new
                        {
                            _lastProxySingleResult.LongStreamingResult.Success,
                            _lastProxySingleResult.LongStreamingResult.ReceivedDone,
                            _lastProxySingleResult.LongStreamingResult.ExpectedSegmentCount,
                            _lastProxySingleResult.LongStreamingResult.ActualSegmentCount,
                            _lastProxySingleResult.LongStreamingResult.SequenceIntegrityPassed,
                            _lastProxySingleResult.LongStreamingResult.ChunkCount,
                            firstTokenLatencyMs = _lastProxySingleResult.LongStreamingResult.FirstTokenLatency?.TotalMilliseconds,
                            totalDurationMs = _lastProxySingleResult.LongStreamingResult.TotalDuration?.TotalMilliseconds,
                            _lastProxySingleResult.LongStreamingResult.OutputTokensPerSecond,
                            _lastProxySingleResult.LongStreamingResult.EndToEndTokensPerSecond,
                            _lastProxySingleResult.LongStreamingResult.OutputTokenCount,
                            _lastProxySingleResult.LongStreamingResult.OutputTokenCountEstimated,
                            _lastProxySingleResult.LongStreamingResult.OutputCharacterCount,
                            _lastProxySingleResult.LongStreamingResult.MaxChunkGapMilliseconds,
                            _lastProxySingleResult.LongStreamingResult.AverageChunkGapMilliseconds,
                            _lastProxySingleResult.LongStreamingResult.Summary,
                            _lastProxySingleResult.LongStreamingResult.Error,
                            _lastProxySingleResult.LongStreamingResult.Preview,
                            _lastProxySingleResult.LongStreamingResult.RequestId,
                            _lastProxySingleResult.LongStreamingResult.TraceId
                        },
                    scenarioResults = _lastProxySingleResult?.ScenarioResults?.Select(result => new
                    {
                        scenario = result.Scenario.ToString(),
                        result.DisplayName,
                        result.CapabilityStatus,
                        result.Success,
                        result.StatusCode,
                        latencyMs = result.Latency?.TotalMilliseconds,
                        firstTokenLatencyMs = result.FirstTokenLatency?.TotalMilliseconds,
                        durationMs = result.Duration?.TotalMilliseconds,
                        result.ReceivedDone,
                        result.ChunkCount,
                        result.SemanticMatch,
                        result.Summary,
                        result.Preview,
                        result.OutputTokenCount,
                        result.OutputTokenCountEstimated,
                        result.OutputCharacterCount,
                        generationDurationMs = result.GenerationDuration?.TotalMilliseconds,
                        result.OutputTokensPerSecond,
                        result.EndToEndTokensPerSecond,
                        result.MaxChunkGapMilliseconds,
                        result.AverageChunkGapMilliseconds,
                        result.RequestId,
                        result.TraceId,
                        failureKind = result.FailureKind?.ToString(),
                        result.FailureStage,
                        result.Error,
                        responseHeaders = result.ResponseHeaders
                    }).ToArray()
                },
                stability = new
                {
                    summary = ProxyStabilitySummary,
                    detail = ProxyStabilityDetail,
                    insightSummary = ProxyStabilityInsightSummary,
                    healthScore = _lastProxyStabilityResult?.HealthScore,
                    healthLabel = _lastProxyStabilityResult?.HealthLabel,
                    fullSuccessRate = _lastProxyStabilityResult?.FullSuccessRate,
                    chatSuccessRate = _lastProxyStabilityResult?.ChatSuccessRate,
                    streamSuccessRate = _lastProxyStabilityResult?.StreamSuccessRate,
                    responsesSuccessCount = _lastProxyStabilityResult?.ResponsesSuccessCount,
                    structuredOutputSuccessCount = _lastProxyStabilityResult?.StructuredOutputSuccessCount,
                    averageChatLatencyMs = _lastProxyStabilityResult?.AverageChatLatency?.TotalMilliseconds,
                    averageTtftMs = _lastProxyStabilityResult?.AverageStreamFirstTokenLatency?.TotalMilliseconds,
                    averageResponsesLatencyMs = _lastProxyStabilityResult?.AverageResponsesLatency?.TotalMilliseconds,
                    averageStructuredOutputLatencyMs = _lastProxyStabilityResult?.AverageStructuredOutputLatency?.TotalMilliseconds,
                    maxConsecutiveFailures = _lastProxyStabilityResult?.MaxConsecutiveFailures,
                    failureDistributionSummary = _lastProxyStabilityResult?.FailureDistributionSummary,
                    distinctResolvedAddressCount = _lastProxyStabilityResult?.DistinctResolvedAddressCount,
                    distinctEdgeSignatureCount = _lastProxyStabilityResult?.DistinctEdgeSignatureCount,
                    edgeSwitchCount = _lastProxyStabilityResult?.EdgeSwitchCount,
                    cdnStabilitySummary = _lastProxyStabilityResult?.CdnStabilitySummary,
                    failureDistribution = _lastProxyStabilityResult?.FailureDistributions?.Select(item => new
                    {
                        failureKind = item.FailureKind.ToString(),
                        item.Count,
                        item.Rate,
                        item.Summary
                    }).ToArray()
                },
                batch = new
                {
                    summary = ProxyBatchSummary,
                    detail = ProxyBatchDetail,
                    recommendationSummary = ProxyBatchRecommendationSummary,
                    ranking = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns)).Select(row => new
                    {
                        row.Entry.Name,
                        row.Entry.BaseUrl,
                        score = row.CompositeScore,
                        verdict = row.LatestResult.Verdict,
                        primaryIssue = row.LatestResult.PrimaryIssue,
                        primaryFailureKind = row.LatestResult.PrimaryFailureKind?.ToString(),
                        cdnSummary = row.LatestResult.CdnSummary,
                        edgeSignature = row.LatestResult.EdgeSignature,
                        chatLatencyMs = row.AverageChatLatencyMs,
                        ttftMs = row.AverageTtftMs,
                        throughputTokensPerSecond = row.AverageBenchmarkTokensPerSecond,
                        traceability = row.LatestResult.TraceabilitySummary,
                        requestId = row.LatestResult.RequestId,
                        traceId = row.LatestResult.TraceId,
                        longStreaming = row.LatestResult.LongStreamingResult is null
                            ? null
                            : new
                            {
                                row.LatestResult.LongStreamingResult.Success,
                                row.LatestResult.LongStreamingResult.ActualSegmentCount,
                                row.LatestResult.LongStreamingResult.ExpectedSegmentCount,
                                row.LatestResult.LongStreamingResult.OutputTokensPerSecond,
                                row.LatestResult.LongStreamingResult.RequestId,
                                row.LatestResult.LongStreamingResult.TraceId
                            },
                        throughputBenchmark = row.LatestResult.ThroughputBenchmarkResult is null
                            ? null
                            : new
                            {
                                row.LatestResult.ThroughputBenchmarkResult.SuccessfulSampleCount,
                                row.LatestResult.ThroughputBenchmarkResult.CompletedSampleCount,
                                row.LatestResult.ThroughputBenchmarkResult.MedianOutputTokensPerSecond,
                                row.LatestResult.ThroughputBenchmarkResult.MinimumOutputTokensPerSecond,
                                row.LatestResult.ThroughputBenchmarkResult.MaximumOutputTokensPerSecond,
                                row.LatestResult.ThroughputBenchmarkResult.RequestId,
                                row.LatestResult.ThroughputBenchmarkResult.TraceId
                            }
                    }).ToArray()
                },
                trends = new
                {
                    summary = ProxyTrendSummary,
                    detail = ProxyTrendDetail,
                    hasTrendChart = ProxyTrendChartImage is not null,
                    trendTarget = trendSnapshot.Target,
                    trend24h = trendSnapshot.Summary
                }
            },
            route = new
            {
                summary = RouteSummary,
                mapSummary = RouteMapSummary,
                geoSummary = RouteGeoSummary,
                hopSummary = RouteHopSummary,
                rawOutput = RouteRawOutput,
                hasMapImage = RouteMapImage is not null
            },
            portScan = new
            {
                summary = PortScanSummary,
                batchSummary = PortScanBatchSummary,
                exportSummary = PortScanExportSummary,
                detail = PortScanDetail,
                rawOutput = PortScanRawOutput,
                progressSummary = PortScanProgressSummary,
                batchConcurrencyText = PortScanBatchConcurrencyText,
                searchText = PortScanSearchText,
                protocolFilter = SelectedPortScanProtocolFilterKey,
                batchTargetsText = PortScanBatchTargetsText,
                target = _lastPortScanResult?.Target,
                profileKey = _lastPortScanResult?.ProfileKey,
                profileName = _lastPortScanResult?.ProfileName,
                customPortsText = _lastPortScanResult?.CustomPortsText,
                effectivePortsText = _lastPortScanResult?.EffectivePortsText,
                openPortCount = _lastPortScanResult?.OpenPortCount,
                openEndpointCount = _lastPortScanResult?.OpenEndpointCount,
                attemptedEndpointCount = _lastPortScanResult?.AttemptedEndpointCount,
                resolvedAddresses = _lastPortScanResult?.ResolvedAddresses,
                systemResolvedAddresses = _lastPortScanResult?.SystemResolvedAddresses,
                findings = _lastPortScanResult?.Findings.Select(finding => new
                {
                    finding.Address,
                    finding.Port,
                    finding.Protocol,
                    finding.Endpoint,
                    finding.ConnectLatencyMilliseconds,
                    finding.ServiceHint,
                    finding.Banner,
                    finding.TlsSummary,
                    finding.HttpSummary,
                    finding.ProbeNotes
                }).ToArray(),
                batchRows = PortScanBatchRows.Select(row => new
                {
                    row.Target,
                    row.Status,
                    row.OpenEndpointCount,
                    row.OpenPortCount,
                    row.ResolvedAddresses,
                    row.Summary,
                    row.Error,
                    row.CheckedAt
                }).ToArray(),
                batchFindings = _lastPortScanBatchResults.SelectMany(result => result.Findings.Select(finding => new
                {
                    target = result.Target,
                    finding.Address,
                    finding.Port,
                    finding.Protocol,
                    finding.Endpoint,
                    finding.ConnectLatencyMilliseconds,
                    finding.ServiceHint,
                    finding.Banner,
                    finding.TlsSummary,
                    finding.HttpSummary,
                    finding.ProbeNotes
                })).ToArray()
            },
            speed = new
            {
                summary = SpeedTestSummary,
                latencyDetail = SpeedTestLatencyDetail,
                transferDetail = SpeedTestTransferDetail,
                packetLossDetail = SpeedTestPacketLossDetail
            },
            splitRouting = new
            {
                summary = SplitRoutingSummary,
                ipInsightSummary = SplitRoutingIpInsightSummary,
                adapterSummary = SplitRoutingAdapterSummary,
                exitSummary = SplitRoutingExitSummary,
                dnsSummary = SplitRoutingDnsSummary,
                reachabilitySummary = SplitRoutingReachabilitySummary
            },
            history = new
            {
                summary = HistorySummary
            },
            artifacts = new
            {
                text = textArtifacts.Select(artifact => new
                {
                    artifact.RelativePath,
                    artifact.Description
                }).ToArray(),
                images = imageArtifacts.Select(artifact => new
                {
                    artifact.RelativePath,
                    artifact.Description
                }).ToArray()
            },
            sections = sections.Select(section => new
            {
                section.Title,
                section.Content
            }).ToArray()
        };
    }
}

