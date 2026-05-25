using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static async Task<ProxyProbeScenarioResult> ProbeCacheMechanismScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        var firstProbe = await ProbeStreamingConversationScenarioAsync(
            client,
            transport,
            BuildCacheProbePayload(model),
            ProxyProbeScenarioKind.CacheMechanism,
            "缓存机制首轮",
            MatchesCacheProbeExpectation,
            cancellationToken);

        if (!firstProbe.Success)
        {
            return BuildSupplementalFailureResult(
                ProxyProbeScenarioKind.CacheMechanism,
                "缓存机制",
                firstProbe.StatusCode,
                firstProbe.Latency ?? firstProbe.Duration ?? TimeSpan.Zero,
                firstProbe.Preview,
                "缓存命中测试首轮失败，无法继续做二次对比。",
                firstProbe.Error ?? firstProbe.Summary,
                firstProbe.ResponseHeaders,
                firstProbe.FailureKind,
                "缓存机制",
                firstProbe.RequestId,
                firstProbe.TraceId);
        }

        var secondProbe = await ProbeStreamingConversationScenarioAsync(
            client,
            transport,
            BuildCacheProbePayload(model),
            ProxyProbeScenarioKind.CacheMechanism,
            "缓存机制复测",
            MatchesCacheProbeExpectation,
            cancellationToken);

        if (!secondProbe.Success)
        {
            return BuildSupplementalFailureResult(
                ProxyProbeScenarioKind.CacheMechanism,
                "缓存机制",
                secondProbe.StatusCode,
                secondProbe.Latency ?? secondProbe.Duration ?? TimeSpan.Zero,
                secondProbe.Preview,
                "缓存命中测试复测失败，无法判断是否存在缓存加速。",
                secondProbe.Error ?? secondProbe.Summary,
                MergeHeaders(firstProbe.ResponseHeaders, secondProbe.ResponseHeaders),
                secondProbe.FailureKind,
                "缓存机制",
                secondProbe.RequestId ?? firstProbe.RequestId,
                secondProbe.TraceId ?? firstProbe.TraceId);
        }

        var firstPreview = firstProbe.Preview;
        var secondPreview = secondProbe.Preview;
        var outputsCorrect = MatchesCacheProbeExpectation(firstPreview) && MatchesCacheProbeExpectation(secondPreview);
        if (!outputsCorrect)
        {
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.CacheMechanism,
                "缓存机制",
                "异常",
                false,
                secondProbe.StatusCode,
                secondProbe.Latency,
                secondProbe.FirstTokenLatency,
                secondProbe.Duration,
                secondProbe.ReceivedDone,
                secondProbe.ChunkCount,
                false,
                "缓存命中测试完成，但首轮或复测输出不符合预期，无法判断缓存是否生效。",
                $"首轮：{firstPreview ?? "（无）"}；复测：{secondPreview ?? "（无）"}",
                ProxyFailureKind.SemanticMismatch,
                "缓存机制",
                "缓存探针要求两次都返回 cache-probe-ok，但实际输出不一致或被改写。",
                MergeHeaders(firstProbe.ResponseHeaders, secondProbe.ResponseHeaders),
                OutputTokenCount: secondProbe.OutputTokenCount,
                OutputTokenCountEstimated: secondProbe.OutputTokenCountEstimated,
                OutputCharacterCount: secondProbe.OutputCharacterCount,
                GenerationDuration: secondProbe.GenerationDuration,
                OutputTokensPerSecond: secondProbe.OutputTokensPerSecond,
                EndToEndTokensPerSecond: secondProbe.EndToEndTokensPerSecond,
                MaxChunkGapMilliseconds: secondProbe.MaxChunkGapMilliseconds,
                AverageChunkGapMilliseconds: secondProbe.AverageChunkGapMilliseconds,
                RequestId: secondProbe.RequestId ?? firstProbe.RequestId,
                TraceId: secondProbe.TraceId ?? firstProbe.TraceId);
        }

        var firstTtftMs = firstProbe.FirstTokenLatency?.TotalMilliseconds;
        var secondTtftMs = secondProbe.FirstTokenLatency?.TotalMilliseconds;
        var outputsEqual = string.Equals(
            NormalizeProbeText(firstPreview),
            NormalizeProbeText(secondPreview),
            StringComparison.Ordinal);
        var likelyHit = IsLikelyCacheHit(firstTtftMs, secondTtftMs, outputsEqual);
        var summary = likelyHit
            ? $"疑似命中缓存：首轮 TTFT {FormatMillisecondsValue(firstProbe.FirstTokenLatency)}，复测 TTFT {FormatMillisecondsValue(secondProbe.FirstTokenLatency)}，输出一致。"
            : $"未观察到明显缓存命中：首轮 TTFT {FormatMillisecondsValue(firstProbe.FirstTokenLatency)}，复测 TTFT {FormatMillisecondsValue(secondProbe.FirstTokenLatency)}，输出一致但加速不明显。";

        return new ProxyProbeScenarioResult(
            ProxyProbeScenarioKind.CacheMechanism,
            "缓存机制",
            likelyHit ? "疑似命中" : "未观察到",
            true,
            secondProbe.StatusCode,
            secondProbe.Latency,
            secondProbe.FirstTokenLatency,
            secondProbe.Duration,
            secondProbe.ReceivedDone,
            secondProbe.ChunkCount,
            likelyHit,
            summary,
            $"首轮 TTFT {FormatMillisecondsValue(firstProbe.FirstTokenLatency)}；复测 TTFT {FormatMillisecondsValue(secondProbe.FirstTokenLatency)}；输出 {(outputsEqual ? "一致" : "不一致")}",
            null,
            "缓存机制",
            null,
            MergeHeaders(firstProbe.ResponseHeaders, secondProbe.ResponseHeaders),
            OutputTokenCount: secondProbe.OutputTokenCount,
            OutputTokenCountEstimated: secondProbe.OutputTokenCountEstimated,
            OutputCharacterCount: secondProbe.OutputCharacterCount,
            GenerationDuration: secondProbe.GenerationDuration,
            OutputTokensPerSecond: secondProbe.OutputTokensPerSecond,
            EndToEndTokensPerSecond: secondProbe.EndToEndTokensPerSecond,
            MaxChunkGapMilliseconds: secondProbe.MaxChunkGapMilliseconds,
            AverageChunkGapMilliseconds: secondProbe.AverageChunkGapMilliseconds,
            RequestId: secondProbe.RequestId ?? firstProbe.RequestId,
            TraceId: secondProbe.TraceId ?? firstProbe.TraceId);
    }

    private static async Task<ProxyProbeScenarioResult> ProbeCacheIsolationScenarioAsync(
        HttpClient primaryClient,
        HttpClient alternateClient,
        ConversationProbeTransport primaryTransport,
        ConversationProbeTransport alternateTransport,
        string model,
        CancellationToken cancellationToken)
    {
        var secretA = $"iso-{Guid.NewGuid():N}"[..16];
        var expectedPrimary = BuildCacheIsolationExpectedOutput("A", secretA);
        var expectedAlternate = BuildCacheIsolationExpectedOutput("B", "none");

        var primaryOutcome = await ProbeJsonConversationScenarioAsync(
            primaryClient,
            primaryTransport,
            BuildCacheIsolationPayload(model, expectedPrimary),
            ProxyProbeScenarioKind.CacheIsolation,
            "缓存隔离-A",
            cancellationToken);

        if (!primaryOutcome.ScenarioResult.Success)
        {
            return BuildSupplementalFailureResult(
                ProxyProbeScenarioKind.CacheIsolation,
                "缓存隔离",
                primaryOutcome.ScenarioResult.StatusCode,
                primaryOutcome.ScenarioResult.Latency ?? TimeSpan.Zero,
                primaryOutcome.ScenarioResult.Preview,
                "缓存隔离测试失败：账户 A 预热请求未通过。",
                primaryOutcome.ScenarioResult.Error ?? primaryOutcome.ScenarioResult.Summary,
                primaryOutcome.ScenarioResult.ResponseHeaders,
                primaryOutcome.ScenarioResult.FailureKind,
                "缓存隔离",
                primaryOutcome.ScenarioResult.RequestId,
                primaryOutcome.ScenarioResult.TraceId);
        }

        var alternateOutcome = await ProbeJsonConversationScenarioAsync(
            alternateClient,
            alternateTransport,
            BuildCacheIsolationPayload(model, expectedAlternate),
            ProxyProbeScenarioKind.CacheIsolation,
            "缓存隔离-B",
            cancellationToken);

        if (!alternateOutcome.ScenarioResult.Success)
        {
            return BuildSupplementalFailureResult(
                ProxyProbeScenarioKind.CacheIsolation,
                "缓存隔离",
                alternateOutcome.ScenarioResult.StatusCode,
                alternateOutcome.ScenarioResult.Latency ?? TimeSpan.Zero,
                alternateOutcome.ScenarioResult.Preview,
                "缓存隔离测试失败：账户 B 复测未通过。",
                alternateOutcome.ScenarioResult.Error ?? alternateOutcome.ScenarioResult.Summary,
                MergeHeaders(primaryOutcome.ScenarioResult.ResponseHeaders, alternateOutcome.ScenarioResult.ResponseHeaders),
                alternateOutcome.ScenarioResult.FailureKind,
                "缓存隔离",
                alternateOutcome.ScenarioResult.RequestId ?? primaryOutcome.ScenarioResult.RequestId,
                alternateOutcome.ScenarioResult.TraceId ?? primaryOutcome.ScenarioResult.TraceId);
        }

        var primaryPreview = primaryOutcome.Preview ?? primaryOutcome.ScenarioResult.Preview;
        var alternatePreview = alternateOutcome.Preview ?? alternateOutcome.ScenarioResult.Preview;
        var primaryMatches = MatchesCacheIsolationExpectation(primaryPreview, expectedPrimary);
        var alternateMatches = MatchesCacheIsolationExpectation(alternatePreview, expectedAlternate);
        var leakedPrimarySecret = NormalizeProbeText(alternatePreview).Contains(NormalizeProbeText(secretA), StringComparison.Ordinal);
        var previewsEqual = string.Equals(
            NormalizeIntegrityOutput(primaryPreview),
            NormalizeIntegrityOutput(alternatePreview),
            StringComparison.Ordinal);
        var digest = BuildCacheIsolationDigest(primaryPreview, alternatePreview, secretA, leakedPrimarySecret, previewsEqual);
        var outputMetrics = BuildOutputMetrics(alternatePreview, null, alternateOutcome.ScenarioResult.Latency ?? TimeSpan.Zero);

        if (primaryMatches && alternateMatches && !leakedPrimarySecret)
        {
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.CacheIsolation,
                "缓存隔离",
                "支持",
                true,
                alternateOutcome.ScenarioResult.StatusCode,
                alternateOutcome.ScenarioResult.Latency,
                null,
                null,
                false,
                0,
                true,
                "A/B 账户隔离正常，账户 B 没有读到账户 A 的私有标记。",
                digest,
                null,
                "缓存隔离",
                null,
                MergeHeaders(primaryOutcome.ScenarioResult.ResponseHeaders, alternateOutcome.ScenarioResult.ResponseHeaders),
                OutputTokenCount: outputMetrics.OutputTokenCount,
                OutputTokenCountEstimated: outputMetrics.OutputTokenCountEstimated,
                OutputCharacterCount: outputMetrics.OutputCharacterCount,
                GenerationDuration: outputMetrics.GenerationDuration,
                OutputTokensPerSecond: outputMetrics.OutputTokensPerSecond,
                EndToEndTokensPerSecond: outputMetrics.EndToEndTokensPerSecond,
                RequestId: alternateOutcome.ScenarioResult.RequestId ?? primaryOutcome.ScenarioResult.RequestId,
                TraceId: alternateOutcome.ScenarioResult.TraceId ?? primaryOutcome.ScenarioResult.TraceId);
        }

        return new ProxyProbeScenarioResult(
            ProxyProbeScenarioKind.CacheIsolation,
            "缓存隔离",
            leakedPrimarySecret || previewsEqual ? "异常" : "待复核",
            false,
            alternateOutcome.ScenarioResult.StatusCode,
            alternateOutcome.ScenarioResult.Latency,
            null,
            null,
            false,
            0,
            false,
            leakedPrimarySecret
                ? "账户 B 返回中包含账户 A 的私有标记，疑似存在跨账户缓存穿透。"
                : previewsEqual
                    ? "账户 A 与账户 B 返回完全一致，且账户 B 未体现自身隔离上下文，疑似缓存键过粗。"
                    : "A/B 账户隔离测试未通过，建议复测并检查缓存键是否包含账户与 system 上下文。",
            digest,
            ProxyFailureKind.SemanticMismatch,
            "缓存隔离",
            leakedPrimarySecret
                ? "账户 B 响应出现了账户 A 的私有 secret，建议立刻排查跨账户缓存隔离。"
                : "账户 B 没有稳定返回自身隔离结果，建议排查缓存键、system prompt 参与度和跨账户隔离策略。",
            MergeHeaders(primaryOutcome.ScenarioResult.ResponseHeaders, alternateOutcome.ScenarioResult.ResponseHeaders),
            RequestId: alternateOutcome.ScenarioResult.RequestId ?? primaryOutcome.ScenarioResult.RequestId,
            TraceId: alternateOutcome.ScenarioResult.TraceId ?? primaryOutcome.ScenarioResult.TraceId);
    }

    private static ProxyProbeScenarioResult CreateSkippedSupplementalScenario(
        ProxyProbeScenarioKind scenario,
        string displayName,
        string summary,
        string capabilityStatus = "前置不足",
        ProxyFailureKind? failureKind = ProxyFailureKind.ConfigurationInvalid)
        => new(
            scenario,
            displayName,
            capabilityStatus,
            false,
            null,
            null,
            null,
            null,
            false,
            0,
            null,
            summary,
            null,
            failureKind,
            displayName,
            failureKind is ProxyFailureKind.ConfigurationInvalid ? summary : null);

    private static ProxyProbeScenarioResult CreateInformationalSupplementalScenario(
        ProxyProbeScenarioKind scenario,
        string displayName,
        string capabilityStatus,
        string summary,
        string? preview,
        int? statusCode,
        TimeSpan? latency,
        IReadOnlyList<string>? headers,
        string? error,
        string? requestId,
        string? traceId,
        int? outputTokenCount = null,
        bool outputTokenCountEstimated = false,
        int? outputCharacterCount = null,
        TimeSpan? generationDuration = null,
        double? outputTokensPerSecond = null,
        double? endToEndTokensPerSecond = null)
        => new(
            scenario,
            displayName,
            capabilityStatus,
            false,
            statusCode,
            latency,
            null,
            null,
            false,
            0,
            null,
            summary,
            preview,
            null,
            displayName,
            error,
            headers,
            outputTokenCount,
            outputTokenCountEstimated,
            outputCharacterCount,
            generationDuration,
            outputTokensPerSecond,
            endToEndTokensPerSecond,
            RequestId: requestId,
            TraceId: traceId);

    private static ProxyProbeScenarioResult BuildSupplementalFailureResult(
        ProxyProbeScenarioKind scenario,
        string displayName,
        int? statusCode,
        TimeSpan latency,
        string? preview,
        string summary,
        string error,
        IReadOnlyList<string>? headers,
        ProxyFailureKind? failureKind,
        string failureStage,
        string? requestId,
        string? traceId)
        => new(
            scenario,
            displayName,
            DescribeCapability(failureKind, false),
            false,
            statusCode,
            latency,
            null,
            null,
            false,
            0,
            false,
            summary,
            preview,
            failureKind,
            failureStage,
            error,
            headers,
            RequestId: requestId,
            TraceId: traceId);
}
