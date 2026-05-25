using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static async Task<ProxyProbeScenarioResult> ProbeFunctionCallingScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<string>? firstHeaders = null;
        IReadOnlyList<string>? secondHeaders = null;
        string? firstRequestId = null;
        string? firstTraceId = null;

        try
        {
            using var firstRequest = new HttpRequestMessage(HttpMethod.Post, transport.Path)
            {
                Content = new StringContent(
                    BuildConversationWirePayload(transport.WireApi, BuildFunctionCallingProbePayload(model)),
                    Encoding.UTF8,
                    "application/json")
            };
            transport.RequestConfigurer?.Invoke(firstRequest);

            using var firstResponse = await client.SendAsync(firstRequest, cancellationToken);
            var firstStatusCode = (int)firstResponse.StatusCode;
            var firstBody = await firstResponse.Content.ReadAsStringAsync(cancellationToken);
            firstHeaders = ExtractInterestingHeaders(firstResponse);
            firstRequestId = ExtractRequestId(firstHeaders);
            firstTraceId = ExtractTraceId(firstHeaders);

            if (!firstResponse.IsSuccessStatusCode)
            {
                var failureKind = ClassifyResponseFailure(ProxyProbeScenarioKind.FunctionCalling, firstStatusCode, firstBody);
                return BuildSupplementalFailureResult(
                    ProxyProbeScenarioKind.FunctionCalling,
                    "Function Calling",
                    firstStatusCode,
                    stopwatch.Elapsed,
                    ExtractBodySample(firstBody),
                    $"Function Calling 第 1 步失败，状态码 {firstStatusCode}。",
                    $"多轮 Function Calling 首轮请求失败：{ExtractBodySample(firstBody)}",
                    firstHeaders,
                    failureKind,
                    "Function Calling",
                    firstRequestId,
                    firstTraceId);
            }

            if (!TryParseToolCallResponse(firstBody, out var toolCall))
            {
                return new ProxyProbeScenarioResult(
                    ProxyProbeScenarioKind.FunctionCalling,
                    "Function Calling",
                    "异常",
                    false,
                    firstStatusCode,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    false,
                    "Function Calling 首轮没有返回可解析的 tool_calls。",
                    BuildLooseSuccessPreview(firstBody),
                    ProxyFailureKind.ProtocolMismatch,
                    "Function Calling",
                    "当前接口返回 200，但 tool_calls 结构缺失或不兼容。",
                    firstHeaders,
                    RequestId: firstRequestId,
                    TraceId: firstTraceId);
            }

            if (!MatchesFunctionCallingArguments(toolCall.ArgumentsJson))
            {
                return new ProxyProbeScenarioResult(
                    ProxyProbeScenarioKind.FunctionCalling,
                    "Function Calling",
                    "异常",
                    false,
                    firstStatusCode,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    false,
                    "Function Calling 首轮返回了 tool_calls，但函数名或参数内容不符合预期。",
                    toolCall.ArgumentsJson,
                    ProxyFailureKind.SemanticMismatch,
                    "Function Calling",
                    "tool_calls 已返回，但函数参数存在缺失、变形或内容错误。",
                    firstHeaders,
                    RequestId: firstRequestId,
                    TraceId: firstTraceId);
            }

            using var secondRequest = new HttpRequestMessage(HttpMethod.Post, transport.Path)
            {
                Content = new StringContent(
                    BuildFunctionCallingFollowUpPayload(
                        transport.WireApi,
                        model,
                        TryReadJsonStringProperty(firstBody, "id"),
                        toolCall.Id,
                        toolCall.Name,
                        toolCall.ArgumentsJson),
                    Encoding.UTF8,
                    "application/json")
            };
            transport.RequestConfigurer?.Invoke(secondRequest);

            using var secondResponse = await client.SendAsync(secondRequest, cancellationToken);
            var secondStatusCode = (int)secondResponse.StatusCode;
            var secondBody = await secondResponse.Content.ReadAsStringAsync(cancellationToken);
            secondHeaders = ExtractInterestingHeaders(secondResponse);
            var secondRequestId = ExtractRequestId(secondHeaders);
            var secondTraceId = ExtractTraceId(secondHeaders);

            if (!secondResponse.IsSuccessStatusCode)
            {
                var failureKind = ClassifyResponseFailure(ProxyProbeScenarioKind.FunctionCalling, secondStatusCode, secondBody);
                return BuildSupplementalFailureResult(
                    ProxyProbeScenarioKind.FunctionCalling,
                    "Function Calling",
                    secondStatusCode,
                    stopwatch.Elapsed,
                    ExtractBodySample(secondBody),
                    $"Function Calling 第 2 步失败，状态码 {secondStatusCode}。",
                    $"多轮 Function Calling 回填工具结果后失败：{ExtractBodySample(secondBody)}",
                    MergeHeaders(firstHeaders, secondHeaders),
                    failureKind,
                    "Function Calling",
                    secondRequestId ?? firstRequestId,
                    secondTraceId ?? firstTraceId);
            }

            var finalPreview = transport.JsonPreviewParser(secondBody) ?? BuildLooseSuccessPreview(secondBody);
            var semanticMatch = MatchesFunctionCallingFinalAnswer(finalPreview);
            var outputMetrics = BuildOutputMetrics(finalPreview, TryExtractOutputTokenCount(secondBody), stopwatch.Elapsed);

            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.FunctionCalling,
                "Function Calling",
                semanticMatch ? "支持" : "异常",
                semanticMatch,
                secondStatusCode,
                stopwatch.Elapsed,
                null,
                null,
                false,
                0,
                semanticMatch,
                semanticMatch
                    ? "多轮 Function Calling 兼容正常，工具调用与回填后的最终回答都符合预期。"
                    : "多轮 Function Calling 已走通，但最终回答不符合预期，存在协议转换风险。",
                finalPreview,
                semanticMatch ? null : ProxyFailureKind.SemanticMismatch,
                "Function Calling",
                semanticMatch ? null : "工具调用已返回，但工具结果回填后的最终回答异常。",
                MergeHeaders(firstHeaders, secondHeaders),
                OutputTokenCount: outputMetrics.OutputTokenCount,
                OutputTokenCountEstimated: outputMetrics.OutputTokenCountEstimated,
                OutputCharacterCount: outputMetrics.OutputCharacterCount,
                GenerationDuration: outputMetrics.GenerationDuration,
                OutputTokensPerSecond: outputMetrics.OutputTokensPerSecond,
                EndToEndTokensPerSecond: outputMetrics.EndToEndTokensPerSecond,
                RequestId: secondRequestId ?? firstRequestId,
                TraceId: secondTraceId ?? firstTraceId);
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            var failureKind = ClassifyException(ex);
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.FunctionCalling,
                "Function Calling",
                DescribeCapability(failureKind, false),
                false,
                null,
                stopwatch.Elapsed,
                null,
                null,
                false,
                0,
                null,
                $"Function Calling 请求失败：{DescribeFailureKind(failureKind)}。",
                null,
                failureKind,
                "Function Calling",
                ex.Message,
                MergeHeaders(firstHeaders, secondHeaders),
                RequestId: firstRequestId,
                TraceId: firstTraceId);
        }
    }

    private static async Task<ProxyProbeScenarioResult> ProbeErrorTransparencyScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, transport.Path)
            {
                Content = new StringContent(BuildErrorTransparencyPayload(model, transport.WireApi), Encoding.UTF8, "application/json")
            };
            transport.RequestConfigurer?.Invoke(request);

            using var response = await client.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var headers = ExtractInterestingHeaders(response);
            var requestId = ExtractRequestId(headers);
            var traceId = ExtractTraceId(headers);
            var preview = ExtractErrorPreview(body);

            if (response.IsSuccessStatusCode)
            {
                return new ProxyProbeScenarioResult(
                    ProxyProbeScenarioKind.ErrorTransparency,
                    "错误透传",
                    "待复核",
                    false,
                    statusCode,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    false,
                    "故意构造的错误请求返回了成功状态，需要人工复核代理是否做了参数修正或错误吞并。",
                    BuildLooseSuccessPreview(body),
                    ProxyFailureKind.ProtocolMismatch,
                    "错误透传",
                    "bad request 场景没有返回 4xx；可能是代理补全了参数，也可能是错误校验被吞并，建议查看原始响应后再判断。",
                    headers,
                    RequestId: requestId,
                    TraceId: traceId);
            }

            var semanticMatch = statusCode is >= 400 and < 500 && LooksLikeTransparentBadRequest(body);
            ProxyFailureKind? failureKind = semanticMatch
                ? null
                : ClassifyResponseFailure(ProxyProbeScenarioKind.ErrorTransparency, statusCode, body);

            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.ErrorTransparency,
                "错误透传",
                semanticMatch ? "支持" : DescribeCapability(failureKind, false),
                semanticMatch,
                statusCode,
                stopwatch.Elapsed,
                null,
                null,
                false,
                0,
                semanticMatch,
                semanticMatch
                    ? $"错误透传正常，bad request 返回 {statusCode}，并保留了可读错误信息。"
                    : $"错误透传异常，bad request 返回 {statusCode}，但错误语义不清晰或被吞成泛化报错。",
                preview,
                failureKind,
                "错误透传",
                semanticMatch ? null : $"构造 bad request 后返回：{preview ?? ExtractBodySample(body)}",
                headers,
                RequestId: requestId,
                TraceId: traceId);
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            stopwatch.Stop();
            var failureKind = ClassifyException(ex);
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.ErrorTransparency,
                "错误透传",
                DescribeCapability(failureKind, false),
                false,
                null,
                stopwatch.Elapsed,
                null,
                null,
                false,
                0,
                null,
                $"错误透传探针请求失败：{DescribeFailureKind(failureKind)}。",
                null,
                failureKind,
                "错误透传",
                ex.Message,
                RequestId: null,
                TraceId: null);
        }
    }
}
