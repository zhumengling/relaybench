using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static async Task<ModelsProbeOutcome> ProbeModelsAsync(
        HttpClient client,
        string modelPath,
        string requestedModel,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client.GetAsync(modelPath, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var headers = ExtractInterestingHeaders(response);
            var requestId = ExtractRequestId(headers);
            var traceId = ExtractTraceId(headers);

            if (!response.IsSuccessStatusCode)
            {
                var failureKind = ClassifyResponseFailure(ProxyProbeScenarioKind.Models, statusCode, content);
                var bodySample = ExtractBodySample(content);
                var error = $"GET {modelPath} 返回 {statusCode} {response.ReasonPhrase}。{bodySample}";
                return new ModelsProbeOutcome(
                    new ProxyProbeScenarioResult(
                        ProxyProbeScenarioKind.Models,
                        "模型列表",
                        DescribeCapability(failureKind, false),
                        false,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        $"模型列表失败，状态码 {statusCode}。",
                        bodySample,
                        failureKind,
                        "模型列表",
                        error,
                        headers,
                        RequestId: requestId,
                        TraceId: traceId),
                    0,
                    Array.Empty<string>());
            }

            IReadOnlyList<string> sampleModels;
            try
            {
                sampleModels = ParseModelIds(content);
            }
            catch (Exception ex)
            {
                var error = $"模型列表返回 200，但结果结构无法按 OpenAI 兼容格式解析：{ex.Message}";
                return new ModelsProbeOutcome(
                    new ProxyProbeScenarioResult(
                        ProxyProbeScenarioKind.Models,
                        "模型列表",
                        "异常",
                        false,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        "模型列表返回异常结构，待复核。",
                        ExtractBodySample(content),
                        ProxyFailureKind.ProtocolMismatch,
                        "模型列表",
                        error,
                        headers),
                    0,
                    Array.Empty<string>());
            }

            var preview = sampleModels.Count == 0
                ? "未返回可解析的模型标识。"
                : string.Join(", ", sampleModels.Take(6));
            var containsRequestedModel =
                string.IsNullOrWhiteSpace(requestedModel) ||
                sampleModels.Contains(requestedModel, StringComparer.OrdinalIgnoreCase);
            var summary = sampleModels.Count == 0
                ? "模型列表请求成功，但未解析到模型标识。"
                : containsRequestedModel
                    ? $"模型列表可用，已解析 {sampleModels.Count} 个示例模型。"
                    : $"模型列表可用，已解析 {sampleModels.Count} 个示例模型；未在示例集中看到请求模型。";

            return new ModelsProbeOutcome(
                new ProxyProbeScenarioResult(
                    ProxyProbeScenarioKind.Models,
                    "模型列表",
                    sampleModels.Count > 0 ? "支持" : "待复核",
                    sampleModels.Count > 0,
                    statusCode,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    sampleModels.Count > 0,
                    summary,
                    preview,
                        sampleModels.Count > 0 ? null : ProxyFailureKind.ProtocolMismatch,
                        "模型列表",
                        sampleModels.Count > 0 ? null : "模型列表返回成功，但没有解析到模型标识。",
                        headers,
                        RequestId: requestId,
                        TraceId: traceId),
                sampleModels.Count,
                sampleModels);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failureKind = ClassifyException(ex);
            return new ModelsProbeOutcome(
                new ProxyProbeScenarioResult(
                    ProxyProbeScenarioKind.Models,
                    "模型列表",
                    DescribeCapability(failureKind, false),
                    false,
                    null,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    null,
                    $"模型列表请求失败：{DescribeFailureKind(failureKind)}。",
                    null,
                    failureKind,
                    "模型列表",
                    $"GET {modelPath} 请求失败：{ex.Message}",
                    RequestId: null,
                    TraceId: null),
                0,
                Array.Empty<string>());
        }
    }

    private static async Task<JsonProbeOutcome> ProbeJsonScenarioAsync(
        HttpClient client,
        string path,
        string payload,
        ProxyProbeScenarioKind scenario,
        string displayName,
        Func<string, string?> previewParser,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var headers = ExtractInterestingHeaders(response);
            var requestId = ExtractRequestId(headers);
            var traceId = ExtractTraceId(headers);

            if (!response.IsSuccessStatusCode)
            {
                var responseFailureKind = ClassifyResponseFailure(scenario, statusCode, content);
                var bodySample = ExtractBodySample(content);
                var responseError = $"POST {path} 返回 {statusCode} {response.ReasonPhrase}。{bodySample}";
                return new JsonProbeOutcome(
                    new ProxyProbeScenarioResult(
                        scenario,
                        displayName,
                        DescribeCapability(responseFailureKind, false),
                        false,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        $"{displayName}失败，状态码 {statusCode}。",
                        bodySample,
                        responseFailureKind,
                        displayName,
                        responseError,
                        headers,
                        RequestId: requestId,
                        TraceId: traceId),
                    null);
            }

            string? preview;
            try
            {
                preview = previewParser(content);
            }
            catch (Exception ex)
            {
                if (scenario is not ProxyProbeScenarioKind.StructuredOutput)
                {
                    var fallbackPreview = BuildLooseSuccessPreview(content);
                    if (!string.IsNullOrWhiteSpace(fallbackPreview))
                    {
                        return new JsonProbeOutcome(
                            new ProxyProbeScenarioResult(
                                scenario,
                                displayName,
                                "支持",
                                true,
                                statusCode,
                                stopwatch.Elapsed,
                                null,
                                null,
                                false,
                                0,
                                null,
                                $"{displayName}返回成功，结构与标准格式略有差异，但已拿到可读内容。",
                                fallbackPreview,
                                null,
                                displayName,
                                null,
                                headers,
                                RequestId: requestId,
                                TraceId: traceId,
                                OutputTokenCount: BuildOutputMetrics(fallbackPreview, TryExtractOutputTokenCount(content), stopwatch.Elapsed).OutputTokenCount,
                                OutputTokenCountEstimated: BuildOutputMetrics(fallbackPreview, TryExtractOutputTokenCount(content), stopwatch.Elapsed).OutputTokenCountEstimated,
                                OutputCharacterCount: BuildOutputMetrics(fallbackPreview, TryExtractOutputTokenCount(content), stopwatch.Elapsed).OutputCharacterCount,
                                GenerationDuration: BuildOutputMetrics(fallbackPreview, TryExtractOutputTokenCount(content), stopwatch.Elapsed).GenerationDuration,
                                OutputTokensPerSecond: BuildOutputMetrics(fallbackPreview, TryExtractOutputTokenCount(content), stopwatch.Elapsed).OutputTokensPerSecond,
                                EndToEndTokensPerSecond: BuildOutputMetrics(fallbackPreview, TryExtractOutputTokenCount(content), stopwatch.Elapsed).EndToEndTokensPerSecond),
                            fallbackPreview);
                    }
                }

                var error = $"{displayName}返回 200，但结构无法按兼容格式解析：{ex.Message}";
                return new JsonProbeOutcome(
                    new ProxyProbeScenarioResult(
                        scenario,
                        displayName,
                        "异常",
                        false,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        $"{displayName}返回异常结构，待复核。",
                        ExtractBodySample(content),
                        ProxyFailureKind.ProtocolMismatch,
                        displayName,
                        error,
                        headers,
                        RequestId: requestId,
                        TraceId: traceId),
                    null);
            }

            if (scenario is not ProxyProbeScenarioKind.StructuredOutput)
            {
                var normalizedPreview = string.IsNullOrWhiteSpace(preview)
                    ? BuildLooseSuccessPreview(content)
                    : preview;

                if (!string.IsNullOrWhiteSpace(normalizedPreview))
                {
                    var outputMetrics = BuildOutputMetrics(normalizedPreview, TryExtractOutputTokenCount(content), stopwatch.Elapsed);
                    return new JsonProbeOutcome(
                        new ProxyProbeScenarioResult(
                            scenario,
                            displayName,
                            "支持",
                            true,
                            statusCode,
                            stopwatch.Elapsed,
                            null,
                            null,
                            false,
                            0,
                            null,
                            $"{displayName}可用，已拿到可读返回内容。",
                            normalizedPreview,
                            null,
                            displayName,
                            null,
                            headers,
                            OutputTokenCount: outputMetrics.OutputTokenCount,
                            OutputTokenCountEstimated: outputMetrics.OutputTokenCountEstimated,
                            OutputCharacterCount: outputMetrics.OutputCharacterCount,
                            GenerationDuration: outputMetrics.GenerationDuration,
                            OutputTokensPerSecond: outputMetrics.OutputTokensPerSecond,
                            EndToEndTokensPerSecond: outputMetrics.EndToEndTokensPerSecond,
                            RequestId: requestId,
                            TraceId: traceId),
                        normalizedPreview);
                }

                return new JsonProbeOutcome(
                    new ProxyProbeScenarioResult(
                        scenario,
                        displayName,
                        "异常",
                        false,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        $"{displayName}返回成功，但没有解析到可读内容，建议复核。",
                        ExtractBodySample(content),
                        ProxyFailureKind.ProtocolMismatch,
                        displayName,
                        $"{displayName}返回 200，但没有解析到可读内容。",
                        headers,
                        RequestId: requestId,
                        TraceId: traceId),
                    null);
            }

            var semanticMatch = MatchProbeExpectation(preview);
            ProxyFailureKind? semanticFailureKind = semanticMatch ? null : ProxyFailureKind.SemanticMismatch;
            var errorMessage = semanticMatch ? null : $"{displayName}虽然返回成功，但结果不符合预期探针内容。";
            var summary = semanticMatch
                ? $"{displayName}可用，语义校验通过。"
                : $"{displayName}返回成功，但语义校验未通过。";
            var structuredOutputMetrics = BuildOutputMetrics(preview, TryExtractOutputTokenCount(content), stopwatch.Elapsed);

            return new JsonProbeOutcome(
                new ProxyProbeScenarioResult(
                    scenario,
                    displayName,
                    semanticMatch ? "支持" : "异常",
                    semanticMatch,
                    statusCode,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    semanticMatch,
                    summary,
                    preview,
                    semanticFailureKind,
                    displayName,
                    errorMessage,
                    headers,
                    OutputTokenCount: structuredOutputMetrics.OutputTokenCount,
                    OutputTokenCountEstimated: structuredOutputMetrics.OutputTokenCountEstimated,
                    OutputCharacterCount: structuredOutputMetrics.OutputCharacterCount,
                    GenerationDuration: structuredOutputMetrics.GenerationDuration,
                    OutputTokensPerSecond: structuredOutputMetrics.OutputTokensPerSecond,
                    EndToEndTokensPerSecond: structuredOutputMetrics.EndToEndTokensPerSecond,
                    RequestId: requestId,
                    TraceId: traceId),
                preview);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failureKind = ClassifyException(ex);
            return new JsonProbeOutcome(
                new ProxyProbeScenarioResult(
                    scenario,
                    displayName,
                    DescribeCapability(failureKind, false),
                    false,
                    null,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    null,
                    $"{displayName}请求失败：{DescribeFailureKind(failureKind)}。",
                    null,
                    failureKind,
                    displayName,
                    $"POST {path} 请求失败：{ex.Message}",
                    RequestId: null,
                    TraceId: null),
                null);
        }
    }

    private static async Task<ProxyProbeScenarioResult> ProbeStreamingScenarioAsync(
        HttpClient client,
        string path,
        string payload,
        ProxyProbeScenarioKind scenario,
        string displayName,
        Func<string, string?> streamContentParser,
        Func<string?, bool>? semanticMatcher,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var headers = ExtractInterestingHeaders(response);
            var requestId = ExtractRequestId(headers);
            var traceId = ExtractTraceId(headers);

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                stopwatch.Stop();
                var responseFailureKind = ClassifyResponseFailure(scenario, statusCode, content);
                var bodySample = ExtractBodySample(content);
                var responseError = $"流式 POST {path} 返回 {statusCode} {response.ReasonPhrase}。{bodySample}";
                return new ProxyProbeScenarioResult(
                    scenario,
                    displayName,
                    DescribeCapability(responseFailureKind, false),
                    false,
                    statusCode,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    null,
                    $"{displayName}失败，状态码 {statusCode}。",
                    bodySample,
                    responseFailureKind,
                    displayName,
                    responseError,
                    headers,
                    RequestId: requestId,
                    TraceId: traceId);
            }

            StreamingProbeOutcome streamOutcome;
            try
            {
                streamOutcome = await ReadStreamingResponseAsync(response, stopwatch, streamContentParser, cancellationToken);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ProxyProbeScenarioResult(
                    scenario,
                    displayName,
                    "异常",
                    false,
                    statusCode,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    null,
                    $"{displayName}在读取流式结果时中断。",
                    null,
                    ProxyFailureKind.StreamBroken,
                    displayName,
                    $"流式响应读取失败：{ex.Message}",
                    headers,
                    RequestId: requestId,
                    TraceId: traceId);
            }

            var semanticMatch = (semanticMatcher ?? MatchProbeExpectation)(streamOutcome.Preview);
            ProxyFailureKind? failureKind = null;
            string? error = null;

            if (streamOutcome.FirstTokenLatency is null)
            {
                failureKind = ProxyFailureKind.StreamNoFirstToken;
                error = "流式连接建立成功，但没有读到首个增量内容。";
            }
            else if (!streamOutcome.ReceivedDone)
            {
                failureKind = ProxyFailureKind.StreamNoDone;
                error = "流式输出读到了内容，但没有收到 [DONE] 结束标记。";
            }
            else if (!semanticMatch)
            {
                failureKind = ProxyFailureKind.SemanticMismatch;
                error = "流式输出完成，但返回内容不符合预期探针文本。";
            }

            var success = failureKind is null;
            var summary = success
                ? $"{displayName}可用，首 Token 与结束标记都正常。"
                : $"{displayName}存在异常：{DescribeFailureKind(failureKind)}。";

            return new ProxyProbeScenarioResult(
                scenario,
                displayName,
                success ? "支持" : DescribeCapability(failureKind, false),
                success,
                statusCode,
                stopwatch.Elapsed,
                streamOutcome.FirstTokenLatency,
                streamOutcome.Duration,
                streamOutcome.ReceivedDone,
                streamOutcome.ChunkCount,
                semanticMatch,
                summary,
                streamOutcome.Preview,
                failureKind,
                displayName,
                error,
                headers,
                OutputTokenCount: streamOutcome.OutputTokenCount,
                OutputTokenCountEstimated: streamOutcome.OutputTokenCountEstimated,
                OutputCharacterCount: streamOutcome.OutputCharacterCount,
                GenerationDuration: streamOutcome.GenerationDuration,
                OutputTokensPerSecond: streamOutcome.OutputTokensPerSecond,
                EndToEndTokensPerSecond: streamOutcome.EndToEndTokensPerSecond,
                MaxChunkGapMilliseconds: streamOutcome.MaxChunkGapMilliseconds,
                AverageChunkGapMilliseconds: streamOutcome.AverageChunkGapMilliseconds,
                RequestId: requestId,
                TraceId: traceId);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failureKind = ClassifyException(ex);
            return new ProxyProbeScenarioResult(
                scenario,
                displayName,
                DescribeCapability(failureKind, false),
                false,
                null,
                stopwatch.Elapsed,
                null,
                null,
                false,
                0,
                null,
                $"{displayName}请求失败：{DescribeFailureKind(failureKind)}。",
                null,
                failureKind,
                displayName,
                $"流式 POST {path} 请求失败：{ex.Message}",
                RequestId: null,
                TraceId: null);
        }
    }

}
