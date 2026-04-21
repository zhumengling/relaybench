using System.Collections.ObjectModel;
using System.Text;
using NetTest.App.Infrastructure;
using NetTest.App.Services;
using NetTest.Core.Models;
using NetTest.Core.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private void UpdateHistorySummary()
    {
        if (_historyEntries.Count == 0)
        {
            HistorySummary = "还没有保存的诊断历史。";
            return;
        }

        StringBuilder builder = new();
        foreach (var entry in _historyEntries.Take(12))
        {
            builder.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] {entry.Category} - {entry.Title}");
            builder.AppendLine(entry.Summary);
            builder.AppendLine();
        }

        HistorySummary = builder.ToString().TrimEnd();
    }

    private void ApplyNetworkSnapshot(NetworkSnapshot snapshot)
    {
        NetworkSummary =
            $"检测时间：{snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"主机名：{snapshot.HostName}\n" +
            $"公网 IP：{snapshot.PublicIp ?? "未获取"}\n" +
            $"Cloudflare 节点：{snapshot.CloudflareColo ?? "未获取"}\n" +
            $"活动网卡数：{snapshot.Adapters.Count}";

        AdapterSummary = snapshot.Adapters.Count == 0
            ? "未检测到活动网卡。"
            : string.Join(
                "\n\n",
                snapshot.Adapters.Select(adapter =>
                    $"{adapter.Name} [{adapter.NetworkType}]\n" +
                    $"描述：{adapter.Description}\n" +
                    $"地址：{string.Join(", ", adapter.UnicastAddresses)}\n" +
                    $"DNS：{string.Join(", ", adapter.DnsServers)}"));

        PingSummary = string.Join(
            "\n",
            snapshot.PingChecks.Select(ping =>
                $"{ping.Target,-14} {TranslatePingStatus(ping.Status),-8} RTT={ping.RoundTripTime?.ToString() ?? "--"} ms  地址={ping.Address ?? "--"}  {ping.Error ?? string.Empty}".Trim()));

        AppendModuleOutput("网络返回", NetworkSummary, PingSummary);
    }

    private void ApplyChatGptTrace(ChatGptTraceResult result)
    {
        ChatGptSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"出口 IP：{result.PublicIp ?? "未获取"}\n" +
            $"loc：{result.LocationCode ?? "缺失"}\n" +
            $"地区：{result.LocationName ?? "未知"}\n" +
            $"Cloudflare 节点：{result.CloudflareColo ?? "缺失"}\n" +
            $"判断：{result.SupportSummary}\n" +
            $"错误：{result.Error ?? "无"}";

        ChatGptRawTrace = string.IsNullOrWhiteSpace(result.RawTrace)
            ? "本次未捕获到原始 Trace 文本。"
            : result.RawTrace;

        AppendModuleOutput("官方 API Trace 返回", ChatGptSummary);
    }

    private void ApplyStunResult(StunProbeResult result)
    {
        _lastStunResult = result;
        var transportLabel = result.TransportProtocol == StunTransportProtocol.Tcp ? "TCP" : "UDP";
        var serverCapabilitySummary = BuildStunServerCapabilitySummary(result);

        StunSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"协议：{transportLabel}\n" +
            $"服务器：{result.ServerHost}:{result.ServerPort}\n" +
            $"解析地址：{string.Join(", ", result.ResolvedAddresses)}\n" +
            $"本地端点：{result.LocalEndpoint ?? "---"}\n" +
            $"响应服务器：{result.RespondingServer ?? "---"}\n" +
            $"RESPONSE-ORIGIN：{result.ResponseOrigin ?? "---"}\n" +
            $"映射地址：{result.MappedAddress ?? "---"}\n" +
            $"OTHER-ADDRESS：{result.OtherAddress ?? "---"}\n" +
            $"CHANGED-ADDRESS：{result.ChangedAddress ?? "---"}\n" +
            $"RTT：{result.RoundTrip?.TotalMilliseconds.ToString("F0") ?? "--"} ms\n" +
            $"服务器能力：{serverCapabilitySummary}\n" +
            $"映射行为：{result.MappingBehaviorHint ?? "—"}\n" +
            $"NAT 类型：{result.NatType ?? "---"}\n" +
            $"置信度：{result.ClassificationConfidence}\n" +
            $"结论说明：{result.NatTypeSummary ?? "—"}\n" +
            $"错误：{result.Error ?? "无"}";

        StunCoverageSummary =
            $"覆盖说明：{result.CoverageSummary}\n" +
            $"复核建议：{result.ReviewRecommendation}";

        StunAttributeSummary = result.Attributes.Count == 0
            ? "未返回可解析的 STUN 属性。"
            : string.Join("\n", result.Attributes.Select(pair => $"{pair.Key}: {pair.Value}"));

        StunTestSummary = result.Tests.Count == 0
            ? "没有可展示的 NAT 分类测试过程。"
            : string.Join(
                "\n\n",
                result.Tests.Select(test =>
                    $"{test.TestName}\n" +
                    $"请求目标：{test.RequestTarget}\n" +
                    $"请求模式：{test.RequestMode}\n" +
                    $"是否成功：{(test.Success ? "成功" : "失败")}\n" +
                    $"本地端点：{test.LocalEndpoint ?? "--"}\n" +
                    $"映射地址：{test.MappedAddress ?? "--"}\n" +
                    $"响应来源：{test.ResponseOrigin ?? "--"}\n" +
                    $"备用地址：{test.AlternateAddress ?? "--"}\n" +
                    $"RTT：{FormatMilliseconds(test.RoundTrip)}\n" +
                    $"摘要：{test.Summary}\n" +
                    $"错误：{test.Error ?? "无"}"));

        AppendModuleOutput("STUN 结果", StunSummary, StunCoverageSummary, StunTestSummary);
    }

    internal static string BuildStunServerCapabilitySummary(StunProbeResult result)
    {
        if (result.TransportProtocol == StunTransportProtocol.Tcp)
        {
            return "当前是 TCP 基础映射模式，只适合看反射地址，不做 UDP NAT 行为细分。";
        }

        var hasOtherAddress = !string.IsNullOrWhiteSpace(result.OtherAddress);
        var hasChangedAddress = !string.IsNullOrWhiteSpace(result.ChangedAddress);
        var hasResponseOrigin = !string.IsNullOrWhiteSpace(result.ResponseOrigin);

        if (hasOtherAddress && hasResponseOrigin)
        {
            return "服务器已返回 RFC 5780 关键属性，可继续做 NAT 行为复核。";
        }

        if (hasOtherAddress || hasChangedAddress || hasResponseOrigin)
        {
            return "服务器只部分支持 NAT 行为测试，结果可参考，但细分结论要保守看待。";
        }

        return "服务器更像普通 STUN，只能确认公网映射，不能可靠细分 NAT 类型。";
    }

    private void ApplyProxyResult(ProxyDiagnosticsResult result)
    {
        var scenarios = GetScenarioResults(result);
        var models = FindScenario(scenarios, ProxyProbeScenarioKind.Models);
        var chat = FindScenario(scenarios, ProxyProbeScenarioKind.ChatCompletions);
        var stream = FindScenario(scenarios, ProxyProbeScenarioKind.ChatCompletionsStream);
        var responses = FindScenario(scenarios, ProxyProbeScenarioKind.Responses);
        var structuredOutput = FindScenario(scenarios, ProxyProbeScenarioKind.StructuredOutput);
        var systemPrompt = FindScenario(scenarios, ProxyProbeScenarioKind.SystemPromptMapping);
        var functionCalling = FindScenario(scenarios, ProxyProbeScenarioKind.FunctionCalling);
        var errorTransparency = FindScenario(scenarios, ProxyProbeScenarioKind.ErrorTransparency);
        var streamingIntegrity = FindScenario(scenarios, ProxyProbeScenarioKind.StreamingIntegrity);
        var officialReferenceIntegrity = FindScenario(scenarios, ProxyProbeScenarioKind.OfficialReferenceIntegrity);
        var multiModal = FindScenario(scenarios, ProxyProbeScenarioKind.MultiModal);
        var cacheMechanism = FindScenario(scenarios, ProxyProbeScenarioKind.CacheMechanism);
        var cacheIsolation = FindScenario(scenarios, ProxyProbeScenarioKind.CacheIsolation);

        StringBuilder summaryBuilder = new();
        summaryBuilder.AppendLine($"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}");
        summaryBuilder.AppendLine($"中转站地址：{result.BaseUrl}");
        summaryBuilder.AppendLine($"请求模型：{result.RequestedModel}");
        summaryBuilder.AppendLine($"实际模型：{result.EffectiveModel ?? "未解析"}");
        summaryBuilder.AppendLine($"总判定：{result.Verdict ?? "待复核"}");
        summaryBuilder.AppendLine($"建议用途：{result.Recommendation ?? "请结合稳定性和入口组对比结果继续判断。"}");
        summaryBuilder.AppendLine($"主要问题：{result.PrimaryIssue ?? "无"}");
        summaryBuilder.AppendLine($"模型列表：{FormatScenarioStatus(models)}");
        summaryBuilder.AppendLine($"普通对话：{FormatScenarioStatus(chat)}");
        summaryBuilder.AppendLine($"流式对话：{FormatScenarioStatus(stream)}");
        summaryBuilder.AppendLine($"Responses：{FormatScenarioStatus(responses)}");
        summaryBuilder.AppendLine($"结构化输出：{FormatScenarioStatus(structuredOutput)}");
        if (systemPrompt is not null || functionCalling is not null)
        {
            summaryBuilder.AppendLine($"System Prompt：{FormatScenarioStatus(systemPrompt)}");
            summaryBuilder.AppendLine($"Function Calling：{FormatScenarioStatus(functionCalling)}");
        }

        if (errorTransparency is not null)
        {
            summaryBuilder.AppendLine($"错误透传：{FormatScenarioStatus(errorTransparency)}");
        }

        if (streamingIntegrity is not null)
        {
            summaryBuilder.AppendLine($"流式完整性：{FormatScenarioStatus(streamingIntegrity)}");
        }

        if (officialReferenceIntegrity is not null)
        {
            summaryBuilder.AppendLine($"官方对照完整性：{FormatScenarioStatus(officialReferenceIntegrity)}");
        }

        if (multiModal is not null)
        {
            summaryBuilder.AppendLine($"多模态：{FormatScenarioStatus(multiModal)}");
        }

        if (cacheMechanism is not null)
        {
            summaryBuilder.AppendLine($"缓存机制：{FormatScenarioStatus(cacheMechanism)}");
        }

        if (cacheIsolation is not null)
        {
            summaryBuilder.AppendLine($"缓存隔离：{FormatScenarioStatus(cacheIsolation)}");
        }

        summaryBuilder.AppendLine($"独立吞吐：{BuildThroughputBenchmarkDigest(result.ThroughputBenchmarkResult)}");
        summaryBuilder.AppendLine($"流式探针速率：{FormatTokensPerSecond(stream?.OutputTokensPerSecond, stream?.OutputTokenCountEstimated == true, stream?.OutputTokensPerSecondSampleCount ?? 1)}");
        summaryBuilder.AppendLine($"流式输出量：{FormatOutputCount(stream)}");
        summaryBuilder.AppendLine($"长流稳定简测：{BuildLongStreamingDigest(result.LongStreamingResult)}");
        summaryBuilder.AppendLine($"可追溯性：{result.TraceabilitySummary ?? "未识别"}");
        summaryBuilder.AppendLine($"解析地址：{(result.ResolvedAddresses is { Count: > 0 } ? string.Join(", ", result.ResolvedAddresses) : "未获取")}");
        summaryBuilder.AppendLine($"CDN / 边缘：{result.CdnSummary ?? "无明显特征"}");
        summaryBuilder.Append($"摘要：{result.Summary}");
        ProxySummary = summaryBuilder.ToString();

        StringBuilder detailBuilder = new();
        detailBuilder.AppendLine(result.SampleModels.Count == 0
            ? "示例模型：无"
            : $"示例模型：{string.Join(", ", result.SampleModels)}");
        detailBuilder.AppendLine();

        foreach (var scenario in scenarios)
        {
            detailBuilder.AppendLine($"[{scenario.DisplayName}]");
            detailBuilder.AppendLine($"状态：{scenario.CapabilityStatus}");
            detailBuilder.AppendLine($"状态码：{scenario.StatusCode?.ToString() ?? "--"}");
            detailBuilder.AppendLine($"耗时：{FormatMilliseconds(scenario.Latency)}");
            detailBuilder.AppendLine($"首 Token：{FormatMilliseconds(scenario.FirstTokenLatency)}");
            detailBuilder.AppendLine($"总输出耗时：{FormatMilliseconds(scenario.GenerationDuration)}");
            detailBuilder.AppendLine($"输出量：{FormatOutputCount(scenario)}");
            detailBuilder.AppendLine($"输出速率：{FormatTokensPerSecond(scenario.OutputTokensPerSecond, scenario.OutputTokenCountEstimated, scenario.OutputTokensPerSecondSampleCount)}");
            detailBuilder.AppendLine($"端到端速率：{FormatTokensPerSecond(scenario.EndToEndTokensPerSecond, scenario.OutputTokenCountEstimated, scenario.OutputTokensPerSecondSampleCount)}");
            detailBuilder.AppendLine($"最大 chunk 间隔：{FormatMillisecondsDoubleValue(scenario.MaxChunkGapMilliseconds)}");
            detailBuilder.AppendLine($"平均 chunk 间隔：{FormatMillisecondsDoubleValue(scenario.AverageChunkGapMilliseconds)}");
            detailBuilder.AppendLine($"摘要：{scenario.Summary}");
            detailBuilder.AppendLine($"预览：{scenario.Preview ?? "（无）"}");
            detailBuilder.AppendLine($"Request-ID：{scenario.RequestId ?? "--"}");
            detailBuilder.AppendLine($"Trace-ID：{scenario.TraceId ?? "--"}");
            detailBuilder.AppendLine($"错误：{scenario.Error ?? "无"}");
            detailBuilder.AppendLine();
        }

        detailBuilder.AppendLine($"CDN 提供商：{result.CdnProvider ?? "未识别"}");
        detailBuilder.AppendLine($"边缘签名：{result.EdgeSignature ?? "未识别"}");
        detailBuilder.AppendLine($"解析地址：{(result.ResolvedAddresses is { Count: > 0 } ? string.Join(", ", result.ResolvedAddresses) : "未获取")}");
        detailBuilder.AppendLine($"可追溯性：{result.TraceabilitySummary ?? "未识别"}");
        detailBuilder.AppendLine($"Request-ID：{result.RequestId ?? "--"}");
        detailBuilder.AppendLine($"Trace-ID：{result.TraceId ?? "--"}");

        if (!string.IsNullOrWhiteSpace(result.ResponseHeadersSummary))
        {
            detailBuilder.AppendLine();
            detailBuilder.AppendLine("关键响应头：");
            detailBuilder.AppendLine(result.ResponseHeadersSummary);
        }

        if (result.LongStreamingResult is { } longStreamingResult)
        {
            detailBuilder.AppendLine();
            detailBuilder.AppendLine("[长流稳定简测]");
            detailBuilder.AppendLine($"结果：{(longStreamingResult.Success ? "通过" : "异常")}");
            detailBuilder.AppendLine($"段数：{longStreamingResult.ActualSegmentCount}/{longStreamingResult.ExpectedSegmentCount}");
            detailBuilder.AppendLine($"顺序校验：{(longStreamingResult.SequenceIntegrityPassed ? "通过" : "失败")}");
            detailBuilder.AppendLine($"DONE：{(longStreamingResult.ReceivedDone ? "已收到" : "缺失")}");
            detailBuilder.AppendLine($"Chunk 数：{longStreamingResult.ChunkCount}");
            detailBuilder.AppendLine($"首 Token：{FormatMilliseconds(longStreamingResult.FirstTokenLatency)}");
            detailBuilder.AppendLine($"总耗时：{FormatMilliseconds(longStreamingResult.TotalDuration)}");
            detailBuilder.AppendLine($"输出速率：{FormatTokensPerSecond(longStreamingResult.OutputTokensPerSecond, longStreamingResult.OutputTokenCountEstimated)}");
            detailBuilder.AppendLine($"端到端速率：{FormatTokensPerSecond(longStreamingResult.EndToEndTokensPerSecond, longStreamingResult.OutputTokenCountEstimated)}");
            detailBuilder.AppendLine($"输出量：{(longStreamingResult.OutputTokenCount?.ToString() ?? "--")} token");
            detailBuilder.AppendLine($"最大 chunk 间隔：{FormatMillisecondsDoubleValue(longStreamingResult.MaxChunkGapMilliseconds)}");
            detailBuilder.AppendLine($"平均 chunk 间隔：{FormatMillisecondsDoubleValue(longStreamingResult.AverageChunkGapMilliseconds)}");
            detailBuilder.AppendLine($"Request-ID：{longStreamingResult.RequestId ?? "--"}");
            detailBuilder.AppendLine($"Trace-ID：{longStreamingResult.TraceId ?? "--"}");
            detailBuilder.AppendLine($"摘要：{longStreamingResult.Summary}");
            detailBuilder.AppendLine($"预览：{longStreamingResult.Preview ?? "（无）"}");
            detailBuilder.AppendLine($"错误：{longStreamingResult.Error ?? "无"}");
        }

        if (result.ThroughputBenchmarkResult is { } throughputBenchmarkResult)
        {
            detailBuilder.AppendLine();
            detailBuilder.AppendLine("[独立吞吐测试]");
            detailBuilder.AppendLine($"结果：{(throughputBenchmarkResult.SuccessfulSampleCount > 0 ? "通过" : "异常")}");
            detailBuilder.AppendLine($"样本：{throughputBenchmarkResult.SuccessfulSampleCount}/{throughputBenchmarkResult.CompletedSampleCount}");
            detailBuilder.AppendLine($"中位数：{FormatTokensPerSecond(throughputBenchmarkResult.MedianOutputTokensPerSecond, throughputBenchmarkResult.OutputTokenCountEstimated, throughputBenchmarkResult.CompletedSampleCount)}");
            detailBuilder.AppendLine($"均值：{FormatTokensPerSecond(throughputBenchmarkResult.AverageOutputTokensPerSecond, throughputBenchmarkResult.OutputTokenCountEstimated)}");
            detailBuilder.AppendLine($"区间：{FormatThroughputBenchmarkRange(throughputBenchmarkResult)}");
            detailBuilder.AppendLine($"端到端中位数：{FormatTokensPerSecond(throughputBenchmarkResult.MedianEndToEndTokensPerSecond, throughputBenchmarkResult.OutputTokenCountEstimated)}");
            detailBuilder.AppendLine($"平均输出量：{(throughputBenchmarkResult.AverageOutputTokenCount?.ToString() ?? "--")} token");
            detailBuilder.AppendLine($"Request-ID：{throughputBenchmarkResult.RequestId ?? "--"}");
            detailBuilder.AppendLine($"Trace-ID：{throughputBenchmarkResult.TraceId ?? "--"}");
            detailBuilder.AppendLine($"摘要：{throughputBenchmarkResult.Summary}");
            detailBuilder.AppendLine($"错误：{throughputBenchmarkResult.Error ?? "无"}");
        }

        ProxyDetail = detailBuilder.ToString().TrimEnd();
        RefreshProxyRelayInsights(result);
        RefreshProxyAdvancedSummaries(result);
        RefreshProxyManagedEntryAssessment(result);

        AppendModuleOutput(
            GetSingleProxyOutputTitle(),
            ProxyVerdictSummary,
            ProxyCapabilityMatrixSummary,
            ProxySingleCapabilityDetailSummary,
            ProxyKeyMetricsSummary,
            ProxyLongStreamingSummary,
            ProxyTraceabilitySummary,
            ProxyManagedEntryAssessmentSummary,
            ProxySummary,
            ProxyDetail);
        RecordSingleProxyTrend(result);
        RefreshProxyUnifiedOutput();
        SaveState();
    }

    private void ApplyProxyStabilityResult(ProxyStabilityResult result)
    {
        var responsesSuccessCount = result.RoundResults
            .Select(round => FindScenario(GetScenarioResults(round), ProxyProbeScenarioKind.Responses))
            .Count(round => round?.Success == true);
        var structuredOutputSuccessCount = result.RoundResults
            .Select(round => FindScenario(GetScenarioResults(round), ProxyProbeScenarioKind.StructuredOutput))
            .Count(round => round?.Success == true);

        ProxyStabilitySummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"中转站地址：{result.BaseUrl}\n" +
            $"轮次：{result.CompletedRounds}/{result.RequestedRounds}\n" +
            $"间隔：{result.DelayMilliseconds} ms\n" +
            $"健康度：{result.HealthScore}/100（{result.HealthLabel}）\n" +
            $"完整成功率：{result.FullSuccessRate:F1}%\n" +
            $"普通对话成功率：{result.ChatSuccessRate:F1}%\n" +
            $"流式成功率：{result.StreamSuccessRate:F1}%\n" +
            $"Responses 成功轮次：{responsesSuccessCount}/{result.CompletedRounds}\n" +
            $"结构化输出成功轮次：{structuredOutputSuccessCount}/{result.CompletedRounds}\n" +
            $"平均普通对话延迟：{FormatMilliseconds(result.AverageChatLatency)}\n" +
            $"平均首 Token 时间：{FormatMilliseconds(result.AverageStreamFirstTokenLatency)}\n" +
            $"平均 Responses 延迟：{FormatMilliseconds(result.AverageResponsesLatency)}\n" +
            $"平均结构化输出延迟：{FormatMilliseconds(result.AverageStructuredOutputLatency)}\n" +
            $"最大连续失败次数：{result.MaxConsecutiveFailures}\n" +
            $"失败分布：{result.FailureDistributionSummary ?? "无"}\n" +
            $"CDN / 边缘：{result.CdnStabilitySummary ?? "无明显 CDN 特征"}\n" +
            $"摘要：{result.Summary}";

        StringBuilder builder = new();
        foreach (var round in result.RoundResults.Select((value, index) => new { value, index }))
        {
            var responsesScenario = FindScenario(GetScenarioResults(round.value), ProxyProbeScenarioKind.Responses);
            var structuredOutputScenario = FindScenario(GetScenarioResults(round.value), ProxyProbeScenarioKind.StructuredOutput);
            var streamScenario = FindScenario(GetScenarioResults(round.value), ProxyProbeScenarioKind.ChatCompletionsStream);

            builder.AppendLine(
                $"第 {round.index + 1} 轮：" +
                $"模型列表={(round.value.ModelsRequestSucceeded ? "成功" : "失败")}，" +
                $"普通对话 {(round.value.ChatRequestSucceeded ? "成功" : "失败")}（{FormatMilliseconds(round.value.ChatLatency)}），" +
                $"流式={(round.value.StreamRequestSucceeded ? "成功" : "失败")}（首 Token {FormatMilliseconds(round.value.StreamFirstTokenLatency)} / {FormatTokensPerSecond(streamScenario?.OutputTokensPerSecond, streamScenario?.OutputTokenCountEstimated == true, streamScenario?.OutputTokensPerSecondSampleCount ?? 1)}），" +
                $"Responses={FormatScenarioStatus(responsesScenario)}，" +
                $"结构化输出={FormatScenarioStatus(structuredOutputScenario)}，" +
                $"边缘={round.value.EdgeSignature ?? "未识别"}，" +
                $"主故障={TranslateFailureKind(round.value.PrimaryFailureKind)}，" +
                $"错误={round.value.Error ?? "无"}");
        }

        ProxyStabilityDetail = builder.Length == 0
            ? "没有采集到逐轮结果。"
            : builder.ToString().TrimEnd();
        RefreshProxyStabilityInsights(result);
        RefreshProxyOverviewSummary();

        AppendModuleOutput("中转站稳定性返回", ProxyStabilitySummary, ProxyStabilityDetail);
        RecordProxyStabilityTrend(result);
        RefreshProxyUnifiedOutput();
        SaveState();
    }

    private static string FormatMilliseconds(TimeSpan? value)
        => value?.TotalMilliseconds.ToString("F0") + " ms" ?? "--";

    private static string TranslatePingStatus(string status)
        => status switch
        {
            "Success" => "成功",
            "TimedOut" => "超时",
            "Error" => "错误",
            "DestinationHostUnreachable" => "目标主机不可达",
            "DestinationNetworkUnreachable" => "目标网络不可达",
            "DestinationPortUnreachable" => "目标端口不可达",
            "DestinationProtocolUnreachable" => "目标协议不可达",
            "BadRoute" => "路由异常",
            "TtlExpired" => "TTL 已过期",
            "TimeExceeded" => "超过时间限制",
            "PacketTooBig" => "数据包过大",
            _ => status
        };
}
