using RelayBench.Core.Services;
using RelayBench.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.App.Services;
using RelayBench.App.ViewModels;

namespace RelayBench.Core.Tests;

internal static partial class TestSupport
{
    internal static int CalculateHealthScore(
        double fullSuccessRate,
        double streamSuccessRate,
        TimeSpan? averageChatLatency,
        TimeSpan? averageTtft,
        int maxConsecutiveFailures)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "CalculateHealthScore",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("CalculateHealthScore was not found.");
        }

        return (int)method.Invoke(
            null,
            [
                fullSuccessRate,
                streamSuccessRate,
                averageChatLatency,
                averageTtft,
                maxConsecutiveFailures,
                0,
                0d
            ])!;
    }

    internal static string BuildVerdictForScenarios(IReadOnlyList<ProxyProbeScenarioResult> scenarios)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildVerdict",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildVerdict was not found.");
        }

        return (string)method.Invoke(null, [scenarios])!;
    }

    internal static ProxyFailureKind ClassifyResponseFailureForTest(
        ProxyProbeScenarioKind scenario,
        int statusCode,
        string? body)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "ClassifyResponseFailure",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("ClassifyResponseFailure was not found.");
        }

        return (ProxyFailureKind)method.Invoke(null, [scenario, statusCode, body])!;
    }

    internal static ProxyFailureKind ClassifyExceptionForTest(Exception exception)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "ClassifyException",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("ClassifyException was not found.");
        }

        return (ProxyFailureKind)method.Invoke(null, [exception])!;
    }

    internal static bool IsLikelyCacheHit(double firstTtftMs, double secondTtftMs, bool outputsEqual)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "IsLikelyCacheHit",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("IsLikelyCacheHit was not found.");
        }

        return (bool)method.Invoke(null, [firstTtftMs, secondTtftMs, outputsEqual])!;
    }

    internal static string? ResolvePreferredWireApiForProtocolProbe(
        bool chatSupported,
        bool responsesSupported,
        bool anthropicSupported)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "ResolvePreferredWireApi",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(bool), typeof(bool), typeof(bool)],
            modifiers: null);

        if (method is null)
        {
            throw new InvalidOperationException("ResolvePreferredWireApi(bool, bool, bool) was not found.");
        }

        return (string?)method.Invoke(null, [chatSupported, responsesSupported, anthropicSupported]);
    }

    internal static bool ShouldProbeChatCompletionsForProtocolProbe(
        bool anthropicSupported,
        bool responsesSupported)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "ShouldProbeChatCompletionsForProtocolProbe",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("ShouldProbeChatCompletionsForProtocolProbe was not found.");
        }

        return (bool)method.Invoke(null, [anthropicSupported, responsesSupported])!;
    }

    internal static bool HasPracticalLongStreamSequenceIntegrity(int[] observedSegments, int expectedSegmentCount)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "HasPracticalLongStreamSequenceIntegrity",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("HasPracticalLongStreamSequenceIntegrity was not found.");
        }

        return (bool)method.Invoke(null, [observedSegments, expectedSegmentCount])!;
    }

    internal static int? ResolvePracticalConcurrencyLimit(IReadOnlyList<ProxyConcurrencyPressureStageResult> stages)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "ResolvePracticalConcurrencyLimit",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("ResolvePracticalConcurrencyLimit was not found.");
        }

        return (int?)method.Invoke(null, [stages]);
    }

    internal static int? ResolveStableConcurrencyLimit(IReadOnlyList<ProxyConcurrencyPressureStageResult> stages)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "ResolveStableConcurrencyLimit",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("ResolveStableConcurrencyLimit was not found.");
        }

        return (int?)method.Invoke(null, [stages]);
    }

    internal static int? ResolveHighRiskConcurrency(IReadOnlyList<ProxyConcurrencyPressureStageResult> stages)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "ResolveHighRiskConcurrency",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("ResolveHighRiskConcurrency was not found.");
        }

        return (int?)method.Invoke(null, [stages]);
    }

    internal static string BuildConcurrencyPressureSummaryForTest(
        IReadOnlyList<ProxyConcurrencyPressureStageResult> stages,
        int? stableConcurrencyLimit,
        int? practicalConcurrencyLimit,
        int? rateLimitStartConcurrency,
        int? highRiskConcurrency)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildConcurrencyPressureSummary",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildConcurrencyPressureSummary was not found.");
        }

        return (string)method.Invoke(
            null,
            [stages, stableConcurrencyLimit, practicalConcurrencyLimit, rateLimitStartConcurrency, highRiskConcurrency])!;
    }

    internal static int[] NormalizeConcurrencyPressureStagesForTest(IReadOnlyList<int>? stages)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "NormalizeConcurrencyPressureStages",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("NormalizeConcurrencyPressureStages was not found.");
        }

        return (int[])method.Invoke(null, [stages])!;
    }

    internal static string BuildProbeTraceDialogContent(ProxyProbeTrace trace)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "BuildProbeTraceDialogContent",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildProbeTraceDialogContent was not found.");
        }

        return (string)method.Invoke(null, [trace])!;
    }

    internal static string BuildToolCallDeepPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildToolCallDeepPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildToolCallDeepPayload was not found.");
        }

        return (string)method.Invoke(null, [model, "TC-DEEP-01"])!;
    }

    internal static string BuildInstructionFollowingPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildInstructionFollowingPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildInstructionFollowingPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildDataExtractionPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildDataExtractionPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildDataExtractionPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildStructuredOutputEdgePayload(string model, string scenarioId)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildStructuredOutputEdgePayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildStructuredOutputEdgePayload was not found.");
        }

        return (string)method.Invoke(null, [model, scenarioId])!;
    }

    internal static string BuildReasonMathConsistencyPayload(string model, string scenarioId)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildReasonMathConsistencyPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildReasonMathConsistencyPayload was not found.");
        }

        return (string)method.Invoke(null, [model, scenarioId])!;
    }

    internal static string BuildCodeBlockDisciplinePayload(string model, string scenarioId)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildCodeBlockDisciplinePayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildCodeBlockDisciplinePayload was not found.");
        }

        return (string)method.Invoke(null, [model, scenarioId])!;
    }

    internal static string BuildSystemPromptPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildSystemPromptPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildSystemPromptPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildFunctionCallingProbePayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildFunctionCallingProbePayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildFunctionCallingProbePayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildFunctionCallingFollowUpPayload(
        string model,
        string toolCallId,
        string functionName,
        string argumentsJson)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildFunctionCallingFollowUpPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(string), typeof(string), typeof(string)],
            modifiers: null);

        if (method is null)
        {
            throw new InvalidOperationException("BuildFunctionCallingFollowUpPayload(string, string, string, string) was not found.");
        }

        return (string)method.Invoke(null, [model, toolCallId, functionName, argumentsJson])!;
    }

    internal static string BuildErrorTransparencyPayload(string model, string wireApi)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildErrorTransparencyPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(string)],
            modifiers: null);

        if (method is null)
        {
            throw new InvalidOperationException("BuildErrorTransparencyPayload(string, string) was not found.");
        }

        return (string)method.Invoke(null, [model, wireApi])!;
    }

    internal static string BuildStreamingIntegrityPayload(string model, bool stream)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildStreamingIntegrityPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildStreamingIntegrityPayload was not found.");
        }

        return (string)method.Invoke(null, [model, stream])!;
    }

    internal static string BuildOfficialReferenceIntegrityPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildOfficialReferenceIntegrityPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildOfficialReferenceIntegrityPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildMultiModalPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildMultiModalPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildMultiModalPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildCacheProbePayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildCacheProbePayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildCacheProbePayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildCacheIsolationPayload(string model, string expectedOutput)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildCacheIsolationPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildCacheIsolationPayload was not found.");
        }

        return (string)method.Invoke(null, [model, expectedOutput])!;
    }

    internal static string BuildEmbeddingsPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildEmbeddingsPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildEmbeddingsPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildImagesPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildImagesPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildImagesPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildModerationPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildModerationPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildModerationPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildAudioSpeechPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildAudioSpeechPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildAudioSpeechPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildMultiModelSpeedPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildMultiModelSpeedPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildMultiModelSpeedPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildConcurrencyPressurePayload(string model, bool stream, string attemptTag)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildConcurrencyPressurePayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildConcurrencyPressurePayload was not found.");
        }

        return (string)method.Invoke(null, [model, stream, attemptTag])!;
    }

    internal static string BuildLongStreamingPayload(string model, int segmentCount)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildLongStreamingPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildLongStreamingPayload was not found.");
        }

        return (string)method.Invoke(null, [model, segmentCount])!;
    }

    internal static string BuildChatPayload(string model, bool stream)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildChatPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildChatPayload was not found.");
        }

        return (string)method.Invoke(null, [model, stream])!;
    }

    internal static string BuildResponsesPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildResponsesPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildResponsesPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildStructuredOutputPayload(string model)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildStructuredOutputPayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildStructuredOutputPayload was not found.");
        }

        return (string)method.Invoke(null, [model])!;
    }

    internal static string BuildAnthropicMessagesPayload(string model, bool stream)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildAnthropicMessagesPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(bool)],
            modifiers: null);

        if (method is null)
        {
            throw new InvalidOperationException("BuildAnthropicMessagesPayload(string, bool) was not found.");
        }

        return (string)method.Invoke(null, [model, stream])!;
    }

    internal static string BuildConversationWirePayloadForTest(string wireApi, string chatPayload)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildConversationWirePayload",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildConversationWirePayload was not found.");
        }

        return (string)method.Invoke(null, [wireApi, chatPayload])!;
    }

    internal static string BuildAnthropicChatPayloadForTest(ChatRequestOptions options, IReadOnlyList<ChatMessage> messages)
    {
        var type = typeof(ChatConversationService).Assembly.GetType("RelayBench.Core.Services.ChatRequestPayloadBuilder");
        var method = type?.GetMethod(
            "BuildAnthropicMessagesPayload",
            BindingFlags.Static | BindingFlags.Public);

        if (method is null)
        {
            throw new InvalidOperationException("ChatRequestPayloadBuilder.BuildAnthropicMessagesPayload was not found.");
        }

        return (string)method.Invoke(null, [options, messages])!;
    }

    internal static string? TryParseAnthropicStreamContent(string body)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "TryParseAnthropicStreamContent",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("TryParseAnthropicStreamContent was not found.");
        }

        return (string?)method.Invoke(null, [body]);
    }

    internal static bool IsAnthropicStreamDone(string body)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "IsAnthropicStreamDone",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("IsAnthropicStreamDone was not found.");
        }

        return (bool)method.Invoke(null, [body])!;
    }

    internal static bool LooksLikeTransparentBadRequest(string body)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "LooksLikeTransparentBadRequest",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("LooksLikeTransparentBadRequest was not found.");
        }

        return (bool)method.Invoke(null, [body])!;
    }

    internal static string? BuildLooseSuccessPreview(string body)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "BuildLooseSuccessPreview",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("BuildLooseSuccessPreview was not found.");
        }

        return (string?)method.Invoke(null, [body]);
    }

    internal static string? ParseEmbeddingsPreview(string body)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "ParseEmbeddingsPreview",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("ParseEmbeddingsPreview was not found.");
        }

        return (string?)method.Invoke(null, [body]);
    }

    internal static string? ParseImagesPreview(string body)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "ParseImagesPreview",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("ParseImagesPreview was not found.");
        }

        return (string?)method.Invoke(null, [body]);
    }

    internal static bool IsLikelyAudioSpeechResponse(string contentType, byte[] bytes)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "IsLikelyAudioSpeechResponse",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("IsLikelyAudioSpeechResponse was not found.");
        }

        return (bool)method.Invoke(null, [contentType, bytes])!;
    }

    internal static (bool Success, string ArgumentsJson) TryParseFunctionCallingToolResponse(string body)
    {
        var method = typeof(ProxyDiagnosticsService).GetMethod(
            "TryParseToolCallResponse",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (method is null)
        {
            throw new InvalidOperationException("TryParseToolCallResponse was not found.");
        }

        object?[] arguments = [body, null];
        var success = (bool)method.Invoke(null, arguments)!;
        if (!success || arguments[1] is null)
        {
            return (success, string.Empty);
        }

        var toolCall = arguments[1]!;
        var argumentsJson = (string)(toolCall.GetType().GetField("ArgumentsJson")?.GetValue(toolCall) ??
                                     toolCall.GetType().GetField("Item3")?.GetValue(toolCall) ??
                                     string.Empty);
        return (success, argumentsJson);
    }
}
