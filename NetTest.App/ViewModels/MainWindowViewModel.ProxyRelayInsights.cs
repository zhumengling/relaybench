using System.Text;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _proxyVerdictSummary = "填写默认入口、默认密钥和默认模型后，这里会先给出一句话结论。";
    private string _proxyCapabilityMatrixSummary = "单次探测完成后，这里会显示基础五项能力，以及按需追加的 System Prompt / Function Calling / 错误透传 / 流式完整性 / 官方对照完整性 / 多模态 / 缓存机制 / 缓存隔离状态。";
    private string _proxySingleCapabilityDetailSummary = "运行基础或深度单次诊断后，这里会逐项显示 /models、普通对话、流式对话、Responses、结构化输出及对应探针的状态码、耗时与预览。";
    private string _proxyKeyMetricsSummary = "这里会显示普通延迟、TTFT、tok/s、输出量、长流简测、可追溯性和高级探针状态码。";
    private string _proxyIssueSummary = "这里会显示当前中转站最主要的问题定位。";
    private string _proxyHeadersSummary = "这里会显示各探测步骤采集到的关键响应头。";
    private string _proxyStabilityInsightSummary = "运行稳定性序列后，这里会给出适合与否、波动来源和失败分布。";
    private string _proxyBatchRecommendationSummary = "运行入口组检测后，这里会给出最推荐的测试项和推荐理由。";

    public string ProxyVerdictSummary
    {
        get => _proxyVerdictSummary;
        private set => SetProperty(ref _proxyVerdictSummary, value);
    }

    public string ProxyCapabilityMatrixSummary
    {
        get => _proxyCapabilityMatrixSummary;
        private set => SetProperty(ref _proxyCapabilityMatrixSummary, value);
    }

    public string ProxySingleCapabilityDetailSummary
    {
        get => _proxySingleCapabilityDetailSummary;
        private set => SetProperty(ref _proxySingleCapabilityDetailSummary, value);
    }

    public string ProxyKeyMetricsSummary
    {
        get => _proxyKeyMetricsSummary;
        private set => SetProperty(ref _proxyKeyMetricsSummary, value);
    }

    public string ProxyIssueSummary
    {
        get => _proxyIssueSummary;
        private set => SetProperty(ref _proxyIssueSummary, value);
    }

    public string ProxyHeadersSummary
    {
        get => _proxyHeadersSummary;
        private set => SetProperty(ref _proxyHeadersSummary, value);
    }

    public string ProxyStabilityInsightSummary
    {
        get => _proxyStabilityInsightSummary;
        private set => SetProperty(ref _proxyStabilityInsightSummary, value);
    }

    public string ProxyBatchRecommendationSummary
    {
        get => _proxyBatchRecommendationSummary;
        private set => SetProperty(ref _proxyBatchRecommendationSummary, value);
    }

    private void RefreshProxyRelayInsights(ProxyDiagnosticsResult result)
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

        var showProtocolCompatibility = ShouldShowScenario(systemPrompt) ||
                                        ShouldShowScenario(functionCalling);
        var showErrorTransparency = ShouldShowScenario(errorTransparency);
        var showStreamingIntegrity = ShouldShowScenario(streamingIntegrity);
        var showOfficialReferenceIntegrity = ShouldShowScenario(officialReferenceIntegrity);
        var showMultiModal = ShouldShowScenario(multiModal);
        var showCacheMechanism = ShouldShowScenario(cacheMechanism);
        var showCacheIsolation = ShouldShowScenario(cacheIsolation);

        ProxyVerdictSummary =
            $"总判定：{result.Verdict ?? "待复核"}\n" +
            $"建议用途：{result.Recommendation ?? "请结合稳定性和批量对比结果再判断。"}\n" +
            $"主要结论：{result.Summary}";

        StringBuilder capabilityBuilder = new();
        capabilityBuilder.AppendLine($"模型列表：{FormatScenarioStatus(models)}");
        capabilityBuilder.AppendLine($"普通对话：{FormatScenarioStatus(chat)}");
        capabilityBuilder.AppendLine($"流式对话：{FormatScenarioStatus(stream)}");
        capabilityBuilder.AppendLine($"Responses：{FormatScenarioStatus(responses)}");
        capabilityBuilder.Append($"结构化输出：{FormatScenarioStatus(structuredOutput)}");
        if (showProtocolCompatibility)
        {
            capabilityBuilder.AppendLine();
            capabilityBuilder.AppendLine($"System Prompt：{FormatScenarioStatus(systemPrompt)}");
            capabilityBuilder.Append($"Function Calling：{FormatScenarioStatus(functionCalling)}");
        }

        if (showErrorTransparency)
        {
            capabilityBuilder.AppendLine();
            capabilityBuilder.Append($"错误透传：{FormatScenarioStatus(errorTransparency)}");
        }

        if (showStreamingIntegrity)
        {
            capabilityBuilder.AppendLine();
            capabilityBuilder.Append($"流式完整性：{FormatScenarioStatus(streamingIntegrity)}");
        }

        if (showOfficialReferenceIntegrity)
        {
            capabilityBuilder.AppendLine();
            capabilityBuilder.Append($"官方对照完整性：{FormatScenarioStatus(officialReferenceIntegrity)}");
        }

        if (showMultiModal)
        {
            capabilityBuilder.AppendLine();
            capabilityBuilder.Append($"多模态：{FormatScenarioStatus(multiModal)}");
        }

        if (showCacheMechanism)
        {
            capabilityBuilder.AppendLine();
            capabilityBuilder.Append($"缓存机制：{FormatScenarioStatus(cacheMechanism)}");
        }

        if (showCacheIsolation)
        {
            capabilityBuilder.AppendLine();
            capabilityBuilder.Append($"缓存隔离：{FormatScenarioStatus(cacheIsolation)}");
        }

        ProxyCapabilityMatrixSummary = capabilityBuilder.ToString();

        StringBuilder capabilityDetailBuilder = new();
        capabilityDetailBuilder.AppendLine($"/models：{BuildSingleScenarioDigest(models, $"模型数 {result.ModelCount} / 示例 {FormatSampleModels(result.SampleModels)}", fallbackStatusCode: result.ModelsStatusCode, fallbackLatency: result.ModelsLatency)}");
        capabilityDetailBuilder.AppendLine($"普通对话：{BuildSingleScenarioDigest(chat, PreviewLabel(result.ChatPreview), fallbackStatusCode: result.ChatStatusCode, fallbackLatency: result.ChatLatency)}");
        capabilityDetailBuilder.AppendLine($"流式对话：{BuildSingleScenarioDigest(stream, BuildStreamPreviewLabel(result.StreamPreview), fallbackStatusCode: result.StreamStatusCode, fallbackLatency: result.StreamDuration, fallbackFirstTokenLatency: result.StreamFirstTokenLatency)}");
        capabilityDetailBuilder.AppendLine($"Responses：{BuildSingleScenarioDigest(responses, PreviewLabel(responses?.Preview), fallbackLatency: responses?.Latency)}");
        capabilityDetailBuilder.Append($"结构化输出：{BuildSingleScenarioDigest(structuredOutput, PreviewLabel(structuredOutput?.Preview), fallbackLatency: structuredOutput?.Latency)}");
        if (showProtocolCompatibility)
        {
            capabilityDetailBuilder.AppendLine();
            capabilityDetailBuilder.AppendLine($"System Prompt：{BuildSingleScenarioDigest(systemPrompt, PreviewLabel(systemPrompt?.Preview), fallbackLatency: systemPrompt?.Latency)}");
            capabilityDetailBuilder.Append($"Function Calling：{BuildSingleScenarioDigest(functionCalling, PreviewLabel(functionCalling?.Preview), fallbackLatency: functionCalling?.Latency)}");
        }

        if (showErrorTransparency)
        {
            capabilityDetailBuilder.AppendLine();
            capabilityDetailBuilder.Append($"错误透传：{BuildSingleScenarioDigest(errorTransparency, PreviewLabel(errorTransparency?.Preview), fallbackLatency: errorTransparency?.Latency)}");
        }

        if (showStreamingIntegrity)
        {
            capabilityDetailBuilder.AppendLine();
            capabilityDetailBuilder.Append($"流式完整性：{BuildSingleScenarioDigest(streamingIntegrity, PreviewLabel(BuildStreamingIntegrityDigest(streamingIntegrity)), fallbackLatency: streamingIntegrity?.Latency, fallbackFirstTokenLatency: streamingIntegrity?.FirstTokenLatency)}");
        }

        if (showOfficialReferenceIntegrity)
        {
            capabilityDetailBuilder.AppendLine();
            capabilityDetailBuilder.Append($"官方对照完整性：{BuildSingleScenarioDigest(officialReferenceIntegrity, PreviewLabel(BuildOfficialReferenceIntegrityDigest(officialReferenceIntegrity)), fallbackLatency: officialReferenceIntegrity?.Latency)}");
        }

        if (showMultiModal)
        {
            capabilityDetailBuilder.AppendLine();
            capabilityDetailBuilder.Append($"多模态：{BuildSingleScenarioDigest(multiModal, PreviewLabel(multiModal?.Preview), fallbackLatency: multiModal?.Latency)}");
        }

        if (showCacheMechanism)
        {
            capabilityDetailBuilder.AppendLine();
            capabilityDetailBuilder.Append($"缓存机制：{BuildSingleScenarioDigest(cacheMechanism, PreviewLabel(BuildCacheMechanismDigest(cacheMechanism)), fallbackLatency: cacheMechanism?.Latency, fallbackFirstTokenLatency: cacheMechanism?.FirstTokenLatency)}");
        }

        if (showCacheIsolation)
        {
            capabilityDetailBuilder.AppendLine();
            capabilityDetailBuilder.Append($"缓存隔离：{BuildSingleScenarioDigest(cacheIsolation, PreviewLabel(BuildCacheIsolationDigest(cacheIsolation)), fallbackLatency: cacheIsolation?.Latency)}");
        }

        ProxySingleCapabilityDetailSummary = capabilityDetailBuilder.ToString();

        StringBuilder keyMetricsBuilder = new();
        keyMetricsBuilder.AppendLine($"普通对话延迟：{FormatMilliseconds(result.ChatLatency)}");
        keyMetricsBuilder.AppendLine($"流式 TTFT：{FormatMilliseconds(result.StreamFirstTokenLatency)}");
        keyMetricsBuilder.AppendLine($"流式总耗时：{FormatMilliseconds(result.StreamDuration)}");
        keyMetricsBuilder.AppendLine($"流式输出速率：{FormatTokensPerSecond(stream?.OutputTokensPerSecond, stream?.OutputTokenCountEstimated == true)}");
        keyMetricsBuilder.AppendLine($"流式端到端速率：{FormatTokensPerSecond(stream?.EndToEndTokensPerSecond, stream?.OutputTokenCountEstimated == true)}");
        keyMetricsBuilder.AppendLine($"流式输出量：{FormatOutputCount(stream)}");
        keyMetricsBuilder.AppendLine($"流式最大 chunk 间隔：{FormatMillisecondsDoubleValue(stream?.MaxChunkGapMilliseconds)}");
        keyMetricsBuilder.AppendLine($"Responses 延迟：{FormatMilliseconds(responses?.Latency)}");
        keyMetricsBuilder.AppendLine($"Responses 输出速率：{FormatTokensPerSecond(responses?.OutputTokensPerSecond, responses?.OutputTokenCountEstimated == true)}");
        keyMetricsBuilder.AppendLine($"Responses 输出量：{FormatOutputCount(responses)}");
        keyMetricsBuilder.AppendLine($"结构化输出延迟：{FormatMilliseconds(structuredOutput?.Latency)}");
        keyMetricsBuilder.AppendLine($"结构化输出速率：{FormatTokensPerSecond(structuredOutput?.OutputTokensPerSecond, structuredOutput?.OutputTokenCountEstimated == true)}");
        keyMetricsBuilder.AppendLine($"结构化输出量：{FormatOutputCount(structuredOutput)}");
        if (showProtocolCompatibility)
        {
            keyMetricsBuilder.AppendLine($"System Prompt 状态码：{systemPrompt?.StatusCode?.ToString() ?? "--"}");
            keyMetricsBuilder.AppendLine($"Function Calling 状态码：{functionCalling?.StatusCode?.ToString() ?? "--"}");
        }

        if (showErrorTransparency)
        {
            keyMetricsBuilder.AppendLine($"错误透传状态码：{errorTransparency?.StatusCode?.ToString() ?? "--"}");
        }

        if (showStreamingIntegrity)
        {
            keyMetricsBuilder.AppendLine($"流式完整性状态码：{streamingIntegrity?.StatusCode?.ToString() ?? "--"}");
            keyMetricsBuilder.AppendLine($"流式完整性摘要：{BuildStreamingIntegrityDigest(streamingIntegrity)}");
        }

        if (showOfficialReferenceIntegrity)
        {
            keyMetricsBuilder.AppendLine($"官方对照完整性状态码：{officialReferenceIntegrity?.StatusCode?.ToString() ?? "--"}");
            keyMetricsBuilder.AppendLine($"官方对照完整性观察：{officialReferenceIntegrity?.CapabilityStatus ?? "未探测"}");
            keyMetricsBuilder.AppendLine($"官方对照完整性摘要：{BuildOfficialReferenceIntegrityDigest(officialReferenceIntegrity)}");
        }

        if (showMultiModal)
        {
            keyMetricsBuilder.AppendLine($"多模态状态码：{multiModal?.StatusCode?.ToString() ?? "--"}");
        }

        if (showCacheMechanism)
        {
            keyMetricsBuilder.AppendLine($"缓存机制状态码：{cacheMechanism?.StatusCode?.ToString() ?? "--"}");
            keyMetricsBuilder.AppendLine($"缓存机制观察：{cacheMechanism?.CapabilityStatus ?? "未探测"}");
            keyMetricsBuilder.AppendLine($"缓存机制摘要：{BuildCacheMechanismDigest(cacheMechanism)}");
        }

        if (showCacheIsolation)
        {
            keyMetricsBuilder.AppendLine($"缓存隔离状态码：{cacheIsolation?.StatusCode?.ToString() ?? "--"}");
            keyMetricsBuilder.AppendLine($"缓存隔离观察：{cacheIsolation?.CapabilityStatus ?? "未探测"}");
            keyMetricsBuilder.AppendLine($"缓存隔离摘要：{BuildCacheIsolationDigest(cacheIsolation)}");
        }

        keyMetricsBuilder.AppendLine($"长流稳定简测：{BuildLongStreamingDigest(result.LongStreamingResult)}");
        keyMetricsBuilder.AppendLine($"可追溯性：{result.TraceabilitySummary ?? "未识别"}");
        keyMetricsBuilder.AppendLine($"Request-ID：{result.RequestId ?? "--"}");
        keyMetricsBuilder.AppendLine($"Trace-ID：{result.TraceId ?? "--"}");
        keyMetricsBuilder.AppendLine($"解析地址：{(result.ResolvedAddresses is { Count: > 0 } ? string.Join(", ", result.ResolvedAddresses) : "未获取")}");
        keyMetricsBuilder.AppendLine($"边缘签名：{result.EdgeSignature ?? "未识别"}");
        keyMetricsBuilder.AppendLine($"CDN 观察：{result.CdnSummary ?? "无明显特征"}");
        keyMetricsBuilder.AppendLine($"模型列表状态码：{models?.StatusCode?.ToString() ?? "--"}");
        keyMetricsBuilder.AppendLine($"普通对话状态码：{chat?.StatusCode?.ToString() ?? "--"}");
        keyMetricsBuilder.AppendLine($"流式对话状态码：{stream?.StatusCode?.ToString() ?? "--"}");
        keyMetricsBuilder.AppendLine($"Responses 状态码：{responses?.StatusCode?.ToString() ?? "--"}");
        keyMetricsBuilder.Append($"结构化输出状态码：{structuredOutput?.StatusCode?.ToString() ?? "--"}");
        ProxyKeyMetricsSummary = keyMetricsBuilder.ToString();

        ProxyIssueSummary =
            $"主要问题：{result.PrimaryIssue ?? "单次探测未发现明显问题。"}\n" +
            $"故障类型：{TranslateFailureKind(result.PrimaryFailureKind)}\n" +
            $"故障阶段：{result.PrimaryFailureStage ?? "无"}\n" +
            $"当前错误：{result.Error ?? "无"}";

        ProxyHeadersSummary = string.IsNullOrWhiteSpace(result.ResponseHeadersSummary)
            ? "本次没有采集到关键响应头。"
            : result.ResponseHeadersSummary!;
    }

    private static string BuildSingleScenarioDigest(
        ProxyProbeScenarioResult? scenario,
        string? trailingDetail = null,
        int? fallbackStatusCode = null,
        TimeSpan? fallbackLatency = null,
        TimeSpan? fallbackFirstTokenLatency = null)
    {
        var status = scenario?.CapabilityStatus ?? "未探测";
        var statusCode = scenario?.StatusCode ?? fallbackStatusCode;
        var latency = scenario?.Latency ?? fallbackLatency;
        var firstTokenLatency = scenario?.FirstTokenLatency ?? fallbackFirstTokenLatency;
        var preview = trailingDetail;
        var donePart = scenario?.ReceivedDone == true ? " / DONE" : string.Empty;
        var codePart = $"状态码 {statusCode?.ToString() ?? "--"}";
        var latencyPart = firstTokenLatency is not null
            ? $"TTFT {FormatMillisecondsValue(firstTokenLatency)} / 总耗时 {FormatMillisecondsValue(latency)}"
            : $"耗时 {FormatMillisecondsValue(latency)}";

        if (string.IsNullOrWhiteSpace(preview))
        {
            preview = PreviewLabel(scenario?.Preview);
        }

        var throughputPart = scenario?.OutputTokensPerSecond is not null
            ? $"；速率 {FormatTokensPerSecond(scenario.OutputTokensPerSecond, scenario.OutputTokenCountEstimated)}；输出 {FormatOutputCount(scenario)}"
            : string.Empty;

        return $"{status}{donePart}；{codePart}；{latencyPart}{throughputPart}；{preview}";
    }

    private static string PreviewLabel(string? preview)
        => string.IsNullOrWhiteSpace(preview)
            ? "预览 （无）"
            : $"预览 {preview}";

    private static string BuildStreamPreviewLabel(string? preview)
    {
        return PreviewLabel(preview);
    }

    private static string FormatSampleModels(IReadOnlyList<string> sampleModels)
        => sampleModels.Count == 0
            ? "无"
            : string.Join(", ", sampleModels.Take(3));

    private static bool ShouldShowScenario(ProxyProbeScenarioResult? scenario)
        => scenario is not null;

    private static string BuildCacheMechanismDigest(ProxyProbeScenarioResult? scenario)
    {
        if (scenario is null)
        {
            return "未探测";
        }

        if (!string.IsNullOrWhiteSpace(scenario.Preview))
        {
            return scenario.Preview!;
        }

        return string.IsNullOrWhiteSpace(scenario.Summary)
            ? "未提供补充说明"
            : scenario.Summary;
    }

    private static string BuildStreamingIntegrityDigest(ProxyProbeScenarioResult? scenario)
    {
        if (scenario is null)
        {
            return "未探测";
        }

        if (!string.IsNullOrWhiteSpace(scenario.Preview))
        {
            return scenario.Preview!;
        }

        return string.IsNullOrWhiteSpace(scenario.Summary)
            ? "未提供补充说明"
            : scenario.Summary;
    }

    private static string BuildOfficialReferenceIntegrityDigest(ProxyProbeScenarioResult? scenario)
    {
        if (scenario is null)
        {
            return "未探测";
        }

        if (!string.IsNullOrWhiteSpace(scenario.Preview))
        {
            return scenario.Preview!;
        }

        return string.IsNullOrWhiteSpace(scenario.Summary)
            ? "未提供补充说明"
            : scenario.Summary;
    }

    private static string BuildCacheIsolationDigest(ProxyProbeScenarioResult? scenario)
    {
        if (scenario is null)
        {
            return "未探测";
        }

        if (!string.IsNullOrWhiteSpace(scenario.Preview))
        {
            return scenario.Preview!;
        }

        return string.IsNullOrWhiteSpace(scenario.Summary)
            ? "未提供补充说明"
            : scenario.Summary;
    }

    private void RefreshProxyStabilityInsights(ProxyStabilityResult result)
    {
        var responsesScenarioRounds = result.RoundResults
            .Select(round => FindScenario(GetScenarioResults(round), ProxyProbeScenarioKind.Responses))
            .Where(round => round is not null)
            .Cast<ProxyProbeScenarioResult>()
            .ToArray();
        var structuredScenarioRounds = result.RoundResults
            .Select(round => FindScenario(GetScenarioResults(round), ProxyProbeScenarioKind.StructuredOutput))
            .Where(round => round is not null)
            .Cast<ProxyProbeScenarioResult>()
            .ToArray();

        var responsesSuccessCount = responsesScenarioRounds.Count(round => round.Success);
        var structuredSuccessCount = structuredScenarioRounds.Count(round => round.Success);
        var failureDistribution = result.FailureDistributions ?? Array.Empty<ProxyFailureDistributionItem>();
        var primaryFailure = failureDistribution.FirstOrDefault();
        var failureSummary = failureDistribution.Count == 0
            ? "未观察到明确的主故障类型。"
            : string.Join("；", failureDistribution.Take(4).Select(item => item.Summary));

        var usageAdvice = result.HealthScore switch
        {
            >= 85 => "适合长期挂载和日常使用。",
            >= 70 => "适合日常使用，建议继续观察晚高峰波动。",
            >= 50 => "可用但有波动，建议先做批量对比再决定长期使用。",
            _ => "不建议继续使用，优先更换中转站。"
        };

        ProxyStabilityInsightSummary =
            $"稳定性结论：{usageAdvice}\n" +
            $"完整成功率：{result.FullSuccessRate:F1}%\n" +
            $"流式成功率：{result.StreamSuccessRate:F1}%\n" +
            $"Responses 成功：{responsesSuccessCount}/{responsesScenarioRounds.Length}\n" +
            $"结构化输出成功：{structuredSuccessCount}/{structuredScenarioRounds.Length}\n" +
            $"平均 Responses 延迟：{FormatMilliseconds(result.AverageResponsesLatency)}\n" +
            $"平均结构化输出延迟：{FormatMilliseconds(result.AverageStructuredOutputLatency)}\n" +
            $"最大连续失败：{result.MaxConsecutiveFailures}\n" +
            $"主要失败类型：{TranslateFailureKind(primaryFailure?.FailureKind)}\n" +
            $"失败分布：{failureSummary}\n" +
            $"CDN / 边缘：{result.CdnStabilitySummary ?? "无明显 CDN 特征"}";
    }

    private void RefreshProxyBatchRecommendation(IReadOnlyList<ProxyBatchAggregateRow> rows)
    {
        if (rows.Count == 0)
        {
            ProxyBatchRecommendationSummary = "入口组检测尚未运行。";
            return;
        }

        var best = rows[0];
        var risk = best.LatestResult.PrimaryIssue ?? "未发现明显风险。";
        var structuredOutput = FindScenario(GetScenarioResults(best.LatestResult), ProxyProbeScenarioKind.StructuredOutput);
        ProxyBatchRecommendationSummary =
            $"当前推荐项：{best.Entry.Name}\n" +
            $"节点地址：{best.Entry.BaseUrl}\n" +
            $"密钥：{best.Entry.ApiKeyAlias} / {MaskApiKey(best.Entry.ApiKey)}\n" +
            $"累计整组轮次：{best.RunCount}\n" +
            $"稳定性：{BuildBatchStabilityLabel(best)}\n" +
            $"综合能力：{FormatBatchDisplayedCapabilityAverage(best)}\n" +
            $"基础均值：{FormatCapabilityAverage(best.AveragePassedCapabilityCount)}/5\n" +
            $"满 5 项轮次：{best.FullPassRounds}/{best.RunCount}\n" +
            $"平均普通延迟：{FormatMillisecondsValue(best.AverageChatLatencyMs)}\n" +
            $"平均 TTFT：{FormatMillisecondsValue(best.AverageTtftMs)}\n" +
            $"平均流式速率：{FormatTokensPerSecond(best.AverageStreamTokensPerSecond)}\n" +
            (ProxyBatchEnableLongStreamingTest
                ? $"增强长流：{(best.LongStreamingExecutedRounds > 0 ? $"{best.LongStreamingPassRounds}/{best.LongStreamingExecutedRounds} 轮通过" : "未执行")}\n"
                : string.Empty) +
            "深度测试：入口组模式不聚合，需查看单次诊断图表。\n" +
            $"最近一轮结构化输出：{FormatScenarioStatus(structuredOutput)}\n" +
            (ProxyBatchEnableLongStreamingTest ? $"最近一轮长流：{BuildLongStreamingDigest(best.LatestResult.LongStreamingResult)}\n" : string.Empty) +
            $"可追溯性：{best.LatestResult.TraceabilitySummary ?? "未识别"}\n" +
            $"最近一轮五项：{BuildBatchCapabilityMatrix(best.LatestResult)}\n" +
            $"CDN / 边缘：{best.LatestResult.CdnSummary ?? "未识别"}\n" +
            $"推荐理由：{best.LatestResult.Recommendation ?? best.LatestResult.Summary}\n" +
            $"主要风险：{risk}";
    }

    private static string FormatOutputCount(ProxyProbeScenarioResult? scenario)
    {
        if (scenario?.OutputTokenCount is not int tokenCount || tokenCount <= 0)
        {
            return "--";
        }

        var estimateSuffix = scenario.OutputTokenCountEstimated ? "（估算）" : string.Empty;
        var charPart = scenario.OutputCharacterCount is > 0 ? $" / {scenario.OutputCharacterCount} 字符" : string.Empty;
        return $"{tokenCount} token{estimateSuffix}{charPart}";
    }

    private static string BuildLongStreamingDigest(ProxyStreamingStabilityResult? result)
    {
        if (result is null)
        {
            return "未运行";
        }

        return $"{(result.Success ? "通过" : "异常")} / {result.ActualSegmentCount}/{result.ExpectedSegmentCount} / DONE {(result.ReceivedDone ? "已收到" : "缺失")} / {FormatTokensPerSecond(result.OutputTokensPerSecond, result.OutputTokenCountEstimated)}";
    }

    private static IReadOnlyList<ProxyProbeScenarioResult> GetScenarioResults(ProxyDiagnosticsResult result)
        => result.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>();

    private static ProxyProbeScenarioResult? FindScenario(
        IReadOnlyList<ProxyProbeScenarioResult> scenarios,
        ProxyProbeScenarioKind scenario)
        => scenarios.FirstOrDefault(item => item.Scenario == scenario);

    private static string FormatScenarioStatus(ProxyProbeScenarioResult? scenario)
    {
        if (scenario is null)
        {
            return "未探测";
        }

        var latencyPart = scenario.FirstTokenLatency is not null
            ? $" / TTFT {FormatMillisecondsValue(scenario.FirstTokenLatency)}"
            : scenario.Latency is not null
                ? $" / {FormatMillisecondsValue(scenario.Latency)}"
                : string.Empty;

        var extraPart = scenario.ReceivedDone ? " / DONE" : string.Empty;
        return $"{scenario.CapabilityStatus}{latencyPart}{extraPart}";
    }

    private static string TranslateFailureKind(ProxyFailureKind? failureKind)
        => failureKind switch
        {
            ProxyFailureKind.ConfigurationInvalid => "参数无效",
            ProxyFailureKind.DnsFailure => "DNS 解析失败",
            ProxyFailureKind.TcpConnectFailure => "TCP 连接失败",
            ProxyFailureKind.TlsHandshakeFailure => "TLS 握手失败",
            ProxyFailureKind.Timeout => "请求超时",
            ProxyFailureKind.AuthRejected => "鉴权失败",
            ProxyFailureKind.RateLimited => "触发限流",
            ProxyFailureKind.ModelNotFound => "模型不存在",
            ProxyFailureKind.UnsupportedEndpoint => "接口不支持",
            ProxyFailureKind.Http4xx => "HTTP 4xx",
            ProxyFailureKind.Http5xx => "HTTP 5xx",
            ProxyFailureKind.ProtocolMismatch => "协议不兼容",
            ProxyFailureKind.StreamNoFirstToken => "流式无首 Token",
            ProxyFailureKind.StreamNoDone => "流式未正常结束",
            ProxyFailureKind.StreamBroken => "流式中途断开",
            ProxyFailureKind.SemanticMismatch => "语义不匹配",
            _ => "无"
        };

    private static string FormatMillisecondsValue(TimeSpan? value)
        => value?.TotalMilliseconds.ToString("F0") + " ms" ?? "--";
}
