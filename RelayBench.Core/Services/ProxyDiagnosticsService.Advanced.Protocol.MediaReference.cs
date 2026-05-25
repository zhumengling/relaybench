using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static async Task<ProxyProbeScenarioResult> ProbeMultiModalScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        var outcome = await ProbeJsonConversationScenarioAsync(
            client,
            transport,
            BuildMultiModalPayload(model),
            ProxyProbeScenarioKind.MultiModal,
            "多模态",
            cancellationToken);

        if (!outcome.ScenarioResult.Success)
        {
            return outcome.ScenarioResult;
        }

        var preview = string.IsNullOrWhiteSpace(outcome.Preview)
            ? outcome.ScenarioResult.Preview
            : outcome.Preview;
        var semanticMatch = MatchesMultiModalExpectation(preview);
        if (semanticMatch)
        {
            return outcome.ScenarioResult with
            {
                SemanticMatch = true,
                Summary = "多模态 Base64 双图请求兼容正常，图片内容判断符合预期。"
            };
        }

        return outcome.ScenarioResult with
        {
            CapabilityStatus = "异常",
            Success = false,
            SemanticMatch = false,
            Summary = "多模态请求返回成功，但图片内容判断不符合预期，可能存在图片透传或协议转换问题。",
            FailureKind = ProxyFailureKind.SemanticMismatch,
            FailureStage = "多模态",
            Error = "多模态请求已返回 200，但模型没有正确识别红/蓝双图，建议排查 image_url Base64、图片数组顺序或上游模型映射。"
        };
    }

    private static async Task<ProxyProbeScenarioResult> ProbeStreamingIntegrityScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        var nonStreamOutcome = await ProbeJsonConversationScenarioAsync(
            client,
            transport,
            BuildStreamingIntegrityPayload(model, stream: false),
            ProxyProbeScenarioKind.StreamingIntegrity,
            "流式完整性基准",
            cancellationToken);

        if (!nonStreamOutcome.ScenarioResult.Success)
        {
            return BuildSupplementalFailureResult(
                ProxyProbeScenarioKind.StreamingIntegrity,
                "流式完整性",
                nonStreamOutcome.ScenarioResult.StatusCode,
                nonStreamOutcome.ScenarioResult.Latency ?? TimeSpan.Zero,
                nonStreamOutcome.ScenarioResult.Preview,
                "流式完整性测试失败：非流式基准请求未通过。",
                nonStreamOutcome.ScenarioResult.Error ?? nonStreamOutcome.ScenarioResult.Summary,
                nonStreamOutcome.ScenarioResult.ResponseHeaders,
                nonStreamOutcome.ScenarioResult.FailureKind,
                "流式完整性",
                nonStreamOutcome.ScenarioResult.RequestId,
                nonStreamOutcome.ScenarioResult.TraceId);
        }

        var streamOutcome = await ProbeStreamingConversationScenarioAsync(
            client,
            transport,
            BuildStreamingIntegrityPayload(model, stream: true),
            ProxyProbeScenarioKind.StreamingIntegrity,
            "流式完整性流式复测",
            static preview => !string.IsNullOrWhiteSpace(preview),
            cancellationToken);

        if (!streamOutcome.Success)
        {
            return BuildSupplementalFailureResult(
                ProxyProbeScenarioKind.StreamingIntegrity,
                "流式完整性",
                streamOutcome.StatusCode,
                streamOutcome.Latency ?? streamOutcome.Duration ?? TimeSpan.Zero,
                streamOutcome.Preview,
                "流式完整性测试失败：流式复测未通过。",
                streamOutcome.Error ?? streamOutcome.Summary,
                MergeHeaders(nonStreamOutcome.ScenarioResult.ResponseHeaders, streamOutcome.ResponseHeaders),
                streamOutcome.FailureKind,
                "流式完整性",
                streamOutcome.RequestId ?? nonStreamOutcome.ScenarioResult.RequestId,
                streamOutcome.TraceId ?? nonStreamOutcome.ScenarioResult.TraceId);
        }

        var expectedOutput = NormalizeIntegrityOutput(GetStreamingIntegrityExpectedOutput());
        var nonStreamText = NormalizeIntegrityOutput(nonStreamOutcome.Preview ?? nonStreamOutcome.ScenarioResult.Preview);
        var streamText = NormalizeIntegrityOutput(streamOutcome.Preview);
        var nonStreamMatches = string.Equals(nonStreamText, expectedOutput, StringComparison.Ordinal);
        var streamMatches = string.Equals(streamText, expectedOutput, StringComparison.Ordinal);
        var outputsEqual = string.Equals(nonStreamText, streamText, StringComparison.Ordinal);
        var preview = BuildStreamingIntegrityDigest(nonStreamText, streamText, outputsEqual);

        if (nonStreamMatches && streamMatches && outputsEqual)
        {
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.StreamingIntegrity,
                "流式完整性",
                "支持",
                true,
                streamOutcome.StatusCode,
                streamOutcome.Latency,
                streamOutcome.FirstTokenLatency,
                streamOutcome.Duration,
                streamOutcome.ReceivedDone,
                streamOutcome.ChunkCount,
                true,
                "流式与非流式输出完全一致，未观察到吞字、额外换行或格式破坏。",
                preview,
                null,
                "流式完整性",
                null,
                MergeHeaders(nonStreamOutcome.ScenarioResult.ResponseHeaders, streamOutcome.ResponseHeaders),
                OutputTokenCount: streamOutcome.OutputTokenCount,
                OutputTokenCountEstimated: streamOutcome.OutputTokenCountEstimated,
                OutputCharacterCount: streamOutcome.OutputCharacterCount,
                GenerationDuration: streamOutcome.GenerationDuration,
                OutputTokensPerSecond: streamOutcome.OutputTokensPerSecond,
                EndToEndTokensPerSecond: streamOutcome.EndToEndTokensPerSecond,
                MaxChunkGapMilliseconds: streamOutcome.MaxChunkGapMilliseconds,
                AverageChunkGapMilliseconds: streamOutcome.AverageChunkGapMilliseconds,
                RequestId: streamOutcome.RequestId ?? nonStreamOutcome.ScenarioResult.RequestId,
                TraceId: streamOutcome.TraceId ?? nonStreamOutcome.ScenarioResult.TraceId);
        }

        var sameButOffTemplate = outputsEqual && !nonStreamMatches && !streamMatches;
        return new ProxyProbeScenarioResult(
            ProxyProbeScenarioKind.StreamingIntegrity,
            "流式完整性",
            sameButOffTemplate ? "待复核" : "异常",
            false,
            streamOutcome.StatusCode,
            streamOutcome.Latency,
            streamOutcome.FirstTokenLatency,
            streamOutcome.Duration,
            streamOutcome.ReceivedDone,
            streamOutcome.ChunkCount,
            false,
            sameButOffTemplate
                ? "流式与非流式输出一致，但两路都没有完全按模板返回固定文本，建议复测确认。"
                : "流式与非流式输出不一致，疑似存在换行、字符拼接或内容截断问题。",
            preview,
            ProxyFailureKind.SemanticMismatch,
            "流式完整性",
            sameButOffTemplate
                ? "完整性探针的两路返回一致，但至少一侧没有严格回显固定模板。"
                : "同一段固定文本在流式与非流式下输出不一致，建议排查 SSE 拼接、换行处理或代理截断。",
            MergeHeaders(nonStreamOutcome.ScenarioResult.ResponseHeaders, streamOutcome.ResponseHeaders),
            OutputTokenCount: streamOutcome.OutputTokenCount,
            OutputTokenCountEstimated: streamOutcome.OutputTokenCountEstimated,
            OutputCharacterCount: streamOutcome.OutputCharacterCount,
            GenerationDuration: streamOutcome.GenerationDuration,
            OutputTokensPerSecond: streamOutcome.OutputTokensPerSecond,
            EndToEndTokensPerSecond: streamOutcome.EndToEndTokensPerSecond,
            MaxChunkGapMilliseconds: streamOutcome.MaxChunkGapMilliseconds,
            AverageChunkGapMilliseconds: streamOutcome.AverageChunkGapMilliseconds,
            RequestId: streamOutcome.RequestId ?? nonStreamOutcome.ScenarioResult.RequestId,
            TraceId: streamOutcome.TraceId ?? nonStreamOutcome.ScenarioResult.TraceId);
    }

    private static async Task<ProxyProbeScenarioResult> ProbeOfficialReferenceIntegrityScenarioAsync(
        HttpClient relayClient,
        HttpClient officialClient,
        ConversationProbeTransport relayTransport,
        ConversationProbeTransport officialTransport,
        string relayModel,
        string officialModel,
        CancellationToken cancellationToken)
    {
        var relayOutcome = await ProbeJsonConversationScenarioAsync(
            relayClient,
            relayTransport,
            BuildOfficialReferenceIntegrityPayload(relayModel),
            ProxyProbeScenarioKind.OfficialReferenceIntegrity,
            "官方对照-待测接口",
            cancellationToken);

        if (!relayOutcome.ScenarioResult.Success)
        {
            return BuildSupplementalFailureResult(
                ProxyProbeScenarioKind.OfficialReferenceIntegrity,
                "官方对照完整性",
                relayOutcome.ScenarioResult.StatusCode,
                relayOutcome.ScenarioResult.Latency ?? TimeSpan.Zero,
                relayOutcome.ScenarioResult.Preview,
                "官方对照完整性测试失败：待测接口对照请求未通过。",
                relayOutcome.ScenarioResult.Error ?? relayOutcome.ScenarioResult.Summary,
                relayOutcome.ScenarioResult.ResponseHeaders,
                relayOutcome.ScenarioResult.FailureKind,
                "官方对照完整性",
                relayOutcome.ScenarioResult.RequestId,
                relayOutcome.ScenarioResult.TraceId);
        }

        var officialOutcome = await ProbeJsonConversationScenarioAsync(
            officialClient,
            officialTransport,
            BuildOfficialReferenceIntegrityPayload(officialModel),
            ProxyProbeScenarioKind.OfficialReferenceIntegrity,
            "官方对照-官方",
            cancellationToken);

        if (!officialOutcome.ScenarioResult.Success)
        {
            return CreateInformationalSupplementalScenario(
                ProxyProbeScenarioKind.OfficialReferenceIntegrity,
                "官方对照完整性",
                "未执行",
                "官方参考端请求未通过，当前先不判定待测接口与官方输出差异。",
                PreviewLabelForSupplementalScenario(officialOutcome.ScenarioResult.Preview),
                officialOutcome.ScenarioResult.StatusCode,
                officialOutcome.ScenarioResult.Latency,
                MergeHeaders(relayOutcome.ScenarioResult.ResponseHeaders, officialOutcome.ScenarioResult.ResponseHeaders),
                officialOutcome.ScenarioResult.Error ?? officialOutcome.ScenarioResult.Summary,
                officialOutcome.ScenarioResult.RequestId ?? relayOutcome.ScenarioResult.RequestId,
                officialOutcome.ScenarioResult.TraceId ?? relayOutcome.ScenarioResult.TraceId,
                relayOutcome.ScenarioResult.OutputTokenCount,
                relayOutcome.ScenarioResult.OutputTokenCountEstimated,
                relayOutcome.ScenarioResult.OutputCharacterCount,
                relayOutcome.ScenarioResult.GenerationDuration,
                relayOutcome.ScenarioResult.OutputTokensPerSecond,
                relayOutcome.ScenarioResult.EndToEndTokensPerSecond);
        }

        var expectedOutput = NormalizeIntegrityOutput(GetOfficialReferenceIntegrityExpectedOutput());
        var relayText = NormalizeIntegrityOutput(relayOutcome.Preview ?? relayOutcome.ScenarioResult.Preview);
        var officialText = NormalizeIntegrityOutput(officialOutcome.Preview ?? officialOutcome.ScenarioResult.Preview);
        var relayMatches = string.Equals(relayText, expectedOutput, StringComparison.Ordinal);
        var officialMatches = string.Equals(officialText, expectedOutput, StringComparison.Ordinal);
        var outputsEqual = string.Equals(relayText, officialText, StringComparison.Ordinal);
        var preview = BuildOfficialReferenceIntegrityDigest(relayText, officialText, relayMatches, officialMatches, outputsEqual);

        if (relayMatches && officialMatches && outputsEqual)
        {
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.OfficialReferenceIntegrity,
                "官方对照完整性",
                "支持",
                true,
                relayOutcome.ScenarioResult.StatusCode,
                relayOutcome.ScenarioResult.Latency,
                null,
                null,
                false,
                0,
                true,
                "待测接口与官方参考端对同一固定模板的输出完全一致，未观察到吞字、乱码或额外换行。",
                preview,
                null,
                "官方对照完整性",
                null,
                MergeHeaders(relayOutcome.ScenarioResult.ResponseHeaders, officialOutcome.ScenarioResult.ResponseHeaders),
                OutputTokenCount: relayOutcome.ScenarioResult.OutputTokenCount,
                OutputTokenCountEstimated: relayOutcome.ScenarioResult.OutputTokenCountEstimated,
                OutputCharacterCount: relayOutcome.ScenarioResult.OutputCharacterCount,
                GenerationDuration: relayOutcome.ScenarioResult.GenerationDuration,
                OutputTokensPerSecond: relayOutcome.ScenarioResult.OutputTokensPerSecond,
                EndToEndTokensPerSecond: relayOutcome.ScenarioResult.EndToEndTokensPerSecond,
                RequestId: relayOutcome.ScenarioResult.RequestId ?? officialOutcome.ScenarioResult.RequestId,
                TraceId: relayOutcome.ScenarioResult.TraceId ?? officialOutcome.ScenarioResult.TraceId);
        }

        if (officialMatches)
        {
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.OfficialReferenceIntegrity,
                "官方对照完整性",
                "异常",
                false,
                relayOutcome.ScenarioResult.StatusCode,
                relayOutcome.ScenarioResult.Latency,
                null,
                null,
                false,
                0,
                false,
                "官方参考端已稳定回显固定模板，但待测接口输出与官方不一致，疑似存在文本破坏或协议转换差异。",
                preview,
                ProxyFailureKind.SemanticMismatch,
                "官方对照完整性",
                "官方端命中固定模板，而待测接口回包发生了字符、换行或内容层面的偏差。",
                MergeHeaders(relayOutcome.ScenarioResult.ResponseHeaders, officialOutcome.ScenarioResult.ResponseHeaders),
                OutputTokenCount: relayOutcome.ScenarioResult.OutputTokenCount,
                OutputTokenCountEstimated: relayOutcome.ScenarioResult.OutputTokenCountEstimated,
                OutputCharacterCount: relayOutcome.ScenarioResult.OutputCharacterCount,
                GenerationDuration: relayOutcome.ScenarioResult.GenerationDuration,
                OutputTokensPerSecond: relayOutcome.ScenarioResult.OutputTokensPerSecond,
                EndToEndTokensPerSecond: relayOutcome.ScenarioResult.EndToEndTokensPerSecond,
                RequestId: relayOutcome.ScenarioResult.RequestId ?? officialOutcome.ScenarioResult.RequestId,
                TraceId: relayOutcome.ScenarioResult.TraceId ?? officialOutcome.ScenarioResult.TraceId);
        }

        return CreateInformationalSupplementalScenario(
            ProxyProbeScenarioKind.OfficialReferenceIntegrity,
            "官方对照完整性",
            "待复核",
            outputsEqual
                ? "待测接口与官方参考端输出一致，但官方端本次没有严格回显固定模板，当前对照结果建议复测确认。"
                : "官方参考端本次没有严格回显固定模板，暂时无法把待测接口与官方的差异直接归因为协议转换问题。",
            preview,
            relayOutcome.ScenarioResult.StatusCode,
            relayOutcome.ScenarioResult.Latency,
            MergeHeaders(relayOutcome.ScenarioResult.ResponseHeaders, officialOutcome.ScenarioResult.ResponseHeaders),
            outputsEqual
                ? null
                : "官方参考端未稳定命中固定模板，本次对照结论仅供参考，建议更换参考模型或重试。",
            relayOutcome.ScenarioResult.RequestId ?? officialOutcome.ScenarioResult.RequestId,
            relayOutcome.ScenarioResult.TraceId ?? officialOutcome.ScenarioResult.TraceId,
            relayOutcome.ScenarioResult.OutputTokenCount,
            relayOutcome.ScenarioResult.OutputTokenCountEstimated,
            relayOutcome.ScenarioResult.OutputCharacterCount,
            relayOutcome.ScenarioResult.GenerationDuration,
            relayOutcome.ScenarioResult.OutputTokensPerSecond,
            relayOutcome.ScenarioResult.EndToEndTokensPerSecond);
    }
}
