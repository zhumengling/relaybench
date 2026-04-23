using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static async Task<ProxyDiagnosticsResult> RunSingleCoreAsync(
        ProxyEndpointSettings settings,
        Uri baseUri,
        IProgress<ProxyDiagnosticsLiveProgress>? progress,
        CancellationToken cancellationToken,
        int streamThroughputSampleCount = 1)
    {
        using var client = CreateClient(baseUri, settings);
        var resolvedAddresses = await ResolveAddressesAsync(baseUri, cancellationToken);

        var modelPath = BuildApiPath(baseUri, "models");
        var chatPath = BuildApiPath(baseUri, "chat/completions");
        var responsesPath = BuildApiPath(baseUri, "responses");

        List<ProxyProbeScenarioResult> scenarioResults = [];

        var modelsProbe = await ProbeModelsAsync(client, modelPath, settings.Model, cancellationToken);
        scenarioResults.Add(modelsProbe.ScenarioResult);

        var effectiveModel = string.IsNullOrWhiteSpace(settings.Model)
            ? modelsProbe.SampleModels.FirstOrDefault() ?? "gpt-4o-mini"
            : settings.Model;
        ReportSingleProgress(
            progress,
            baseUri,
            settings,
            effectiveModel,
            modelsProbe.ModelCount,
            modelsProbe.SampleModels,
            modelsProbe.ScenarioResult,
            scenarioResults);

        var chatProbe = await ProbeJsonScenarioAsync(
            client,
            chatPath,
            BuildChatPayload(effectiveModel, stream: false),
            ProxyProbeScenarioKind.ChatCompletions,
            "普通对话",
            ParseChatPreview,
            cancellationToken);
        scenarioResults.Add(chatProbe.ScenarioResult);
        ReportSingleProgress(
            progress,
            baseUri,
            settings,
            effectiveModel,
            modelsProbe.ModelCount,
            modelsProbe.SampleModels,
            chatProbe.ScenarioResult,
            scenarioResults);

        var streamPayload = BuildChatPayload(effectiveModel, stream: true);
        var streamProbe = await ProbeStreamingScenarioAsync(
            client,
            chatPath,
            streamPayload,
            ProxyProbeScenarioKind.ChatCompletionsStream,
            "流式对话",
            TryParseChatStreamContent,
            MatchProbeExpectation,
            cancellationToken);
        streamProbe = await SampleStreamingThroughputAsync(
            client,
            chatPath,
            streamPayload,
            streamProbe,
            "流式对话",
            TryParseChatStreamContent,
            MatchProbeExpectation,
            streamThroughputSampleCount,
            cancellationToken);
        scenarioResults.Add(streamProbe);
        ReportSingleProgress(
            progress,
            baseUri,
            settings,
            effectiveModel,
            modelsProbe.ModelCount,
            modelsProbe.SampleModels,
            streamProbe,
            scenarioResults);

        var responsesProbe = await ProbeJsonScenarioAsync(
            client,
            responsesPath,
            BuildResponsesPayload(effectiveModel),
            ProxyProbeScenarioKind.Responses,
            "Responses",
            ParseResponsesPreview,
            cancellationToken);
        scenarioResults.Add(responsesProbe.ScenarioResult);
        ReportSingleProgress(
            progress,
            baseUri,
            settings,
            effectiveModel,
            modelsProbe.ModelCount,
            modelsProbe.SampleModels,
            responsesProbe.ScenarioResult,
            scenarioResults);

        var structuredOutputProbe = await ProbeJsonScenarioAsync(
            client,
            responsesPath,
            BuildStructuredOutputPayload(effectiveModel),
            ProxyProbeScenarioKind.StructuredOutput,
            "结构化输出",
            ParseStructuredOutputPreview,
            cancellationToken);
        scenarioResults.Add(structuredOutputProbe.ScenarioResult);
        ReportSingleProgress(
            progress,
            baseUri,
            settings,
            effectiveModel,
            modelsProbe.ModelCount,
            modelsProbe.SampleModels,
            structuredOutputProbe.ScenarioResult,
            scenarioResults);

        var primaryFailure = SelectPrimaryFailure(scenarioResults);
        var verdict = BuildVerdict(scenarioResults);
        var recommendation = BuildRecommendation(scenarioResults);
        var primaryIssue = BuildPrimaryIssue(scenarioResults);
        var headersSummary = BuildHeadersSummary(scenarioResults);
        var traceability = BuildTraceabilityObservation(scenarioResults);
        var edgeObservation = BuildEdgeObservation(baseUri, scenarioResults, resolvedAddresses);
        var summary = BuildOverallSummary(verdict, recommendation, scenarioResults, primaryIssue, edgeObservation.CdnSummary);

        return new ProxyDiagnosticsResult(
            DateTimeOffset.Now,
            baseUri.ToString(),
            settings.Model,
            effectiveModel,
            modelsProbe.ScenarioResult.Success,
            modelsProbe.ScenarioResult.StatusCode,
            modelsProbe.ModelCount,
            modelsProbe.SampleModels,
            modelsProbe.ScenarioResult.Latency,
            chatProbe.ScenarioResult.Success,
            chatProbe.ScenarioResult.StatusCode,
            chatProbe.ScenarioResult.Latency,
            chatProbe.Preview,
            streamProbe.Success,
            streamProbe.StatusCode,
            streamProbe.FirstTokenLatency,
            streamProbe.Duration,
            streamProbe.Preview,
            summary,
            primaryFailure?.Error,
            scenarioResults,
            primaryFailure?.FailureKind,
            primaryFailure?.FailureStage,
            verdict,
            recommendation,
            primaryIssue,
            headersSummary,
            resolvedAddresses,
            edgeObservation.CdnProvider,
            edgeObservation.EdgeSignature,
            edgeObservation.CdnSummary,
            null,
            null,
            traceability.RequestId,
            traceability.TraceId,
            traceability.Summary);
    }

    private static void ReportSingleProgress(
        IProgress<ProxyDiagnosticsLiveProgress>? progress,
        Uri baseUri,
        ProxyEndpointSettings settings,
        string effectiveModel,
        int modelCount,
        IReadOnlyList<string> sampleModels,
        ProxyProbeScenarioResult currentScenarioResult,
        List<ProxyProbeScenarioResult> scenarioResults,
        int totalScenarioCount = 5)
    {
        if (progress is null)
        {
            return;
        }

        progress.Report(new ProxyDiagnosticsLiveProgress(
            DateTimeOffset.Now,
            baseUri.ToString(),
            settings.Model,
            effectiveModel,
            scenarioResults.Count,
            totalScenarioCount,
            modelCount,
            sampleModels,
            currentScenarioResult.Scenario,
            currentScenarioResult,
            scenarioResults.ToArray()));
    }

}
