using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    public async Task<ProxyDiagnosticsResult> RunSupplementalScenariosAsync(

        ProxyEndpointSettings settings,

        ProxyDiagnosticsResult baselineResult,

        bool includeProtocolCompatibility,

        bool includeErrorTransparency,

        bool includeStreamingIntegrity,

        bool includeOfficialReferenceIntegrity,

        string? officialReferenceBaseUrl,

        string? officialReferenceApiKey,

        string? officialReferenceModel,

        bool includeMultiModal,

        bool includeCacheMechanism,

        bool includeCacheIsolation,

        string? cacheIsolationAlternateApiKey,

        bool includeInstructionFollowing,

        bool includeDataExtraction,

        bool includeStructuredOutputEdge,

        bool includeToolCallDeep,

        bool includeReasonMathConsistency,

        bool includeCodeBlockDiscipline,

        IProgress<ProxyDiagnosticsLiveProgress>? progress = null,

        CancellationToken cancellationToken = default)

    {

        if ((!includeProtocolCompatibility &&

             !includeErrorTransparency &&

             !includeStreamingIntegrity &&

             !includeOfficialReferenceIntegrity &&

             !includeMultiModal &&

             !includeCacheMechanism &&

             !includeCacheIsolation &&

             !includeInstructionFollowing &&

             !includeDataExtraction &&

             !includeStructuredOutputEdge &&

             !includeToolCallDeep &&

             !includeReasonMathConsistency &&

             !includeCodeBlockDiscipline) ||

            !TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out _))

        {

            return baselineResult;

        }



        var effectiveModel = string.IsNullOrWhiteSpace(baselineResult.EffectiveModel)

            ? normalizedSettings.Model

            : baselineResult.EffectiveModel.Trim();



        if (string.IsNullOrWhiteSpace(effectiveModel) || !baselineResult.ChatRequestSucceeded)

        {

            return baselineResult;

        }



        using var client = CreateClient(baseUri, normalizedSettings with { Model = effectiveModel });

        var conversationTransport = await ResolveConversationProbeTransportAsync(
            client,
            baseUri,
            effectiveModel,
            baselineResult,
            cancellationToken);

        List<ProxyProbeScenarioResult> mergedScenarioResults = (baselineResult.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>())

            .Where(result => !IsSupplementalScenario(result.Scenario))

            .ToList();

        var totalScenarioCount = mergedScenarioResults.Count + CountPlannedSupplementalScenarioCount(

            includeProtocolCompatibility,

            includeErrorTransparency,

            includeStreamingIntegrity,

            includeOfficialReferenceIntegrity,

            includeMultiModal,

            includeCacheMechanism,

            includeCacheIsolation,

            includeInstructionFollowing,

            includeDataExtraction,

            includeStructuredOutputEdge,

            includeToolCallDeep,

            includeReasonMathConsistency,

            includeCodeBlockDiscipline);



        void ReportSupplementalProgress(ProxyProbeScenarioResult scenarioResult)

            => ReportSingleProgress(

                progress,

                baseUri,

                normalizedSettings,

                effectiveModel,

                baselineResult.ModelCount,

                baselineResult.SampleModels,

                scenarioResult,

                mergedScenarioResults,

                totalScenarioCount);



        void AddSupplementalScenario(ProxyProbeScenarioResult scenarioResult)

        {

            mergedScenarioResults.Add(scenarioResult);

            ReportSupplementalProgress(scenarioResult);

        }



        if (includeProtocolCompatibility)

        {

            AddSupplementalScenario(await ProbeSystemPromptMappingScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

            AddSupplementalScenario(await ProbeFunctionCallingScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

        }



        if (includeErrorTransparency)

        {

            AddSupplementalScenario(await ProbeErrorTransparencyScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

        }



        if (includeStreamingIntegrity)

        {

            if (baselineResult.StreamRequestSucceeded)

            {

                AddSupplementalScenario(await ProbeStreamingIntegrityScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

            }

            else

            {

                AddSupplementalScenario(CreateSkippedSupplementalScenario(

                    ProxyProbeScenarioKind.StreamingIntegrity,

                    "\u6d41\u5f0f\u5b8c\u6574\u6027",

                    "\u6d41\u5f0f\u5b8c\u6574\u6027\u6d4b\u8bd5\u5df2\u8df3\u8fc7\uff1a\u57fa\u7840\u6d41\u5f0f\u5bf9\u8bdd\u672a\u901a\u8fc7\uff0c\u65e0\u6cd5\u505a\u6d41\u5f0f\u4e0e\u975e\u6d41\u5f0f\u5bf9\u7167\u3002",
                    capabilityStatus: "\u672a\u6267\u884c",
                    failureKind: null));

            }

        }



        if (includeOfficialReferenceIntegrity)

        {

            if (!baselineResult.ChatRequestSucceeded)

            {

                AddSupplementalScenario(CreateSkippedSupplementalScenario(

                    ProxyProbeScenarioKind.OfficialReferenceIntegrity,

                    "\u5b98\u65b9\u5bf9\u7167\u5b8c\u6574\u6027",

                    "\u5b98\u65b9\u5bf9\u7167\u5b8c\u6574\u6027\u6d4b\u8bd5\u5df2\u8df3\u8fc7\uff1a\u57fa\u7840\u666e\u901a\u5bf9\u8bdd\u672a\u901a\u8fc7\uff0c\u65e0\u6cd5\u4e0e\u5b98\u65b9\u53c2\u8003\u7aef\u505a\u56fa\u5b9a\u6a21\u677f\u5bf9\u7167\u3002",
                    capabilityStatus: "\u672a\u6267\u884c",
                    failureKind: null));

            }

            else if (string.IsNullOrWhiteSpace(officialReferenceBaseUrl) ||

                     string.IsNullOrWhiteSpace(officialReferenceApiKey))

            {

                AddSupplementalScenario(CreateSkippedSupplementalScenario(

                    ProxyProbeScenarioKind.OfficialReferenceIntegrity,

                    "\u5b98\u65b9\u5bf9\u7167\u5b8c\u6574\u6027",

                    "\u5b98\u65b9\u5bf9\u7167\u5b8c\u6574\u6027\u6d4b\u8bd5\u5df2\u8df3\u8fc7\uff1a\u5c1a\u672a\u586b\u5199\u5b98\u65b9\u53c2\u8003 Base URL \u6216 API Key\u3002"));

            }

            else

            {

                var resolvedOfficialModel = string.IsNullOrWhiteSpace(officialReferenceModel)

                    ? effectiveModel

                    : officialReferenceModel.Trim();

                var officialSettings = new ProxyEndpointSettings(

                    officialReferenceBaseUrl.Trim(),

                    officialReferenceApiKey.Trim(),

                    resolvedOfficialModel,

                    normalizedSettings.IgnoreTlsErrors,

                    normalizedSettings.TimeoutSeconds);



                if (!TryValidateSettings(officialSettings, out var normalizedOfficialSettings, out var officialBaseUri, out var officialValidationError))

                {

                    AddSupplementalScenario(CreateSkippedSupplementalScenario(

                        ProxyProbeScenarioKind.OfficialReferenceIntegrity,

                        "\u5b98\u65b9\u5bf9\u7167\u5b8c\u6574\u6027",

                        $"\u5b98\u65b9\u5bf9\u7167\u5b8c\u6574\u6027\u6d4b\u8bd5\u5df2\u8df3\u8fc7\uff1a{officialValidationError}"));

                }

                else if (Uri.Compare(

                             baseUri,

                             officialBaseUri,

                             UriComponents.SchemeAndServer | UriComponents.Path,

                             UriFormat.SafeUnescaped,

                             StringComparison.OrdinalIgnoreCase) == 0)

                {

                    AddSupplementalScenario(CreateSkippedSupplementalScenario(

                        ProxyProbeScenarioKind.OfficialReferenceIntegrity,

                        "\u5b98\u65b9\u5bf9\u7167\u5b8c\u6574\u6027",

                        "\u5b98\u65b9\u5bf9\u7167\u5b8c\u6574\u6027\u6d4b\u8bd5\u5df2\u8df3\u8fc7\uff1a\u5b98\u65b9\u53c2\u8003 Base URL \u4e0e\u5f53\u524d\u4e2d\u8f6c\u5730\u5740\u76f8\u540c\uff0c\u65e0\u6cd5\u5f62\u6210\u72ec\u7acb\u5bf9\u7167\u3002"));

                }

                else

                {

                    using var officialClient = CreateClient(officialBaseUri, normalizedOfficialSettings);

                    var officialTransport = CreateConversationProbeTransport(officialClient, officialBaseUri, "chat");

                    AddSupplementalScenario(await ProbeOfficialReferenceIntegrityScenarioAsync(

                        client,

                        officialClient,

                        conversationTransport,

                        officialTransport,

                        effectiveModel,

                        normalizedOfficialSettings.Model,

                        cancellationToken));

                }

            }

        }



        if (includeMultiModal)

        {

            AddSupplementalScenario(await ProbeMultiModalScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

        }



        if (includeCacheMechanism)

        {

            if (baselineResult.StreamRequestSucceeded)

            {

                AddSupplementalScenario(await ProbeCacheMechanismScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

            }

            else

            {

                AddSupplementalScenario(CreateSkippedSupplementalScenario(

                    ProxyProbeScenarioKind.CacheMechanism,

                    "\u7f13\u5b58\u673a\u5236",

                    "\u7f13\u5b58\u547d\u4e2d\u6d4b\u8bd5\u5df2\u8df3\u8fc7\uff1a\u57fa\u7840\u6d41\u5f0f\u5bf9\u8bdd\u672a\u901a\u8fc7\uff0c\u65e0\u6cd5\u6bd4\u8f83\u9996 Token \u65f6\u95f4\u3002",
                    capabilityStatus: "\u672a\u6267\u884c",
                    failureKind: null));

            }

        }



        if (includeCacheIsolation)

        {

            var alternateApiKey = cacheIsolationAlternateApiKey?.Trim() ?? string.Empty;

            if (!baselineResult.ChatRequestSucceeded)

            {

                AddSupplementalScenario(CreateSkippedSupplementalScenario(

                    ProxyProbeScenarioKind.CacheIsolation,

                    "\u7f13\u5b58\u9694\u79bb",

                    "\u7f13\u5b58\u9694\u79bb\u6d4b\u8bd5\u5df2\u8df3\u8fc7\uff1a\u57fa\u7840\u666e\u901a\u5bf9\u8bdd\u672a\u901a\u8fc7\uff0c\u65e0\u6cd5\u505a A/B \u8d26\u6237\u9694\u79bb\u9a8c\u8bc1\u3002",
                    capabilityStatus: "\u672a\u6267\u884c",
                    failureKind: null));

            }

            else if (string.IsNullOrWhiteSpace(alternateApiKey))

            {

                AddSupplementalScenario(CreateSkippedSupplementalScenario(

                    ProxyProbeScenarioKind.CacheIsolation,

                    "\u7f13\u5b58\u9694\u79bb",

                    "\u7f13\u5b58\u9694\u79bb\u6d4b\u8bd5\u5df2\u8df3\u8fc7\uff1a\u5c1a\u672a\u586b\u5199\u8d26\u6237 B Key\u3002"));

            }

            else if (string.Equals(alternateApiKey, normalizedSettings.ApiKey, StringComparison.Ordinal))

            {

                AddSupplementalScenario(CreateSkippedSupplementalScenario(

                    ProxyProbeScenarioKind.CacheIsolation,

                    "\u7f13\u5b58\u9694\u79bb",

                    "\u7f13\u5b58\u9694\u79bb\u6d4b\u8bd5\u5df2\u8df3\u8fc7\uff1a\u8d26\u6237 B Key \u4e0e\u4e3b Key \u76f8\u540c\uff0c\u65e0\u6cd5\u5f62\u6210 A/B \u8d26\u6237\u5bf9\u7167\u3002"));

            }

            else

            {

                using var alternateClient = CreateClient(

                    baseUri,

                    normalizedSettings with

                    {

                        ApiKey = alternateApiKey,

                        Model = effectiveModel

                    });

                AddSupplementalScenario(await ProbeCacheIsolationScenarioAsync(

                    client,

                    alternateClient,

                    conversationTransport,

                    CreateConversationProbeTransport(alternateClient, baseUri, conversationTransport.WireApi),

                    effectiveModel,

                    cancellationToken));

            }

        }



        if (includeInstructionFollowing)

        {

            AddSupplementalScenario(await ProbeInstructionFollowingScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

        }



        if (includeDataExtraction)

        {

            AddSupplementalScenario(await ProbeDataExtractionScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

        }

        if (includeStructuredOutputEdge)

        {

            AddSupplementalScenario(await ProbeStructuredOutputEdgeScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

        }

        if (includeToolCallDeep)

        {

            AddSupplementalScenario(await ProbeToolCallDeepScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

        }

        if (includeReasonMathConsistency)

        {

            AddSupplementalScenario(await ProbeReasonMathConsistencyScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

        }

        if (includeCodeBlockDiscipline)

        {

            AddSupplementalScenario(await ProbeCodeBlockDisciplineScenarioAsync(client, conversationTransport, effectiveModel, cancellationToken));

        }



        return RebuildDiagnosticsResult(baselineResult, mergedScenarioResults);

    }



    private static int CountPlannedSupplementalScenarioCount(

        bool includeProtocolCompatibility,

        bool includeErrorTransparency,

        bool includeStreamingIntegrity,

        bool includeOfficialReferenceIntegrity,

        bool includeMultiModal,

        bool includeCacheMechanism,

        bool includeCacheIsolation,

        bool includeInstructionFollowing,

        bool includeDataExtraction,

        bool includeStructuredOutputEdge,

        bool includeToolCallDeep,

        bool includeReasonMathConsistency,

        bool includeCodeBlockDiscipline)

        => (includeProtocolCompatibility ? 2 : 0) +

           (includeErrorTransparency ? 1 : 0) +

           (includeStreamingIntegrity ? 1 : 0) +

           (includeOfficialReferenceIntegrity ? 1 : 0) +

           (includeMultiModal ? 1 : 0) +

           (includeCacheMechanism ? 1 : 0) +

           (includeCacheIsolation ? 1 : 0) +

           (includeInstructionFollowing ? 1 : 0) +

           (includeDataExtraction ? 1 : 0) +

           (includeStructuredOutputEdge ? 1 : 0) +

           (includeToolCallDeep ? 1 : 0) +

           (includeReasonMathConsistency ? 1 : 0) +

           (includeCodeBlockDiscipline ? 1 : 0);



    private static bool IsSupplementalScenario(ProxyProbeScenarioKind scenario)
        => scenario is ProxyProbeScenarioKind.SystemPromptMapping or
            ProxyProbeScenarioKind.FunctionCalling or
            ProxyProbeScenarioKind.ErrorTransparency or
            ProxyProbeScenarioKind.StreamingIntegrity or
            ProxyProbeScenarioKind.OfficialReferenceIntegrity or
            ProxyProbeScenarioKind.MultiModal or
            ProxyProbeScenarioKind.CacheMechanism or
            ProxyProbeScenarioKind.CacheIsolation or
            ProxyProbeScenarioKind.InstructionFollowing or
            ProxyProbeScenarioKind.DataExtraction or
            ProxyProbeScenarioKind.StructuredOutputEdge or
            ProxyProbeScenarioKind.ToolCallDeep or
            ProxyProbeScenarioKind.ReasonMathConsistency or
            ProxyProbeScenarioKind.CodeBlockDiscipline;

    private static ProxyDiagnosticsResult RebuildDiagnosticsResult(
        ProxyDiagnosticsResult baselineResult,
        IReadOnlyList<ProxyProbeScenarioResult> scenarioResults)
    {
        var primaryFailure = SelectPrimaryFailure(scenarioResults);
        var verdict = BuildVerdict(scenarioResults);
        var recommendation = BuildRecommendation(scenarioResults);
        var primaryIssue = BuildPrimaryIssue(scenarioResults);
        var headersSummary = BuildHeadersSummary(scenarioResults);
        var traceability = BuildTraceabilityObservation(scenarioResults);
        var summary = BuildOverallSummary(verdict, recommendation, scenarioResults, primaryIssue, baselineResult.CdnSummary);

        return baselineResult with
        {
            ScenarioResults = scenarioResults.ToArray(),
            Summary = summary,
            Error = primaryFailure?.Error,
            PrimaryFailureKind = primaryFailure?.FailureKind,
            PrimaryFailureStage = primaryFailure?.FailureStage,
            Verdict = verdict,
            Recommendation = recommendation,
            PrimaryIssue = primaryIssue,
            ResponseHeadersSummary = headersSummary,
            RequestId = traceability.RequestId,
            TraceId = traceability.TraceId,
            TraceabilitySummary = traceability.Summary
        };
    }

    private static async Task<ProxyProbeScenarioResult> ProbeSystemPromptMappingScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        var outcome = await ProbeJsonConversationScenarioAsync(
            client,
            transport,
            BuildSystemPromptPayload(model),
            ProxyProbeScenarioKind.SystemPromptMapping,
            "System Prompt",
            cancellationToken);

        if (!outcome.ScenarioResult.Success)
        {
            return outcome.ScenarioResult;
        }

        var preview = string.IsNullOrWhiteSpace(outcome.Preview)
            ? outcome.ScenarioResult.Preview
            : outcome.Preview;
        var semanticMatch = MatchesSystemPromptExpectation(preview);
        if (semanticMatch)
        {
            return outcome.ScenarioResult with
            {
                SemanticMatch = true,
                Summary = "System 角色映射正常，用户覆盖指令没有压过系统提示。"
            };
        }

        return outcome.ScenarioResult with
        {
            CapabilityStatus = "异常",
            Success = false,
            SemanticMatch = false,
            Summary = "System Prompt 映射异常，返回结果更像是跟随了用户覆盖指令。",
            FailureKind = ProxyFailureKind.SemanticMismatch,
            FailureStage = "System Prompt",
            Error = "系统提示词没有稳定生效，建议排查当前接口在模型转换时是否丢失或拼接了 system 角色。"
        };
    }

    private static async Task<ProxyProbeScenarioResult> ProbeInstructionFollowingScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        var outcome = await ProbeJsonConversationScenarioAsync(
            client,
            transport,
            BuildInstructionFollowingPayload(model),
            ProxyProbeScenarioKind.InstructionFollowing,
            "指令遵循",
            cancellationToken);

        return ApplySemanticProbeEvaluation(
            outcome,
            "指令遵循",
            SemanticProbeEvaluator.EvaluateInstructionFollowing);
    }

    private static async Task<ProxyProbeScenarioResult> ProbeDataExtractionScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        var outcome = await ProbeJsonConversationScenarioAsync(
            client,
            transport,
            BuildDataExtractionPayload(model),
            ProxyProbeScenarioKind.DataExtraction,
            "数据抽取",
            cancellationToken);

        return ApplySemanticProbeEvaluation(
            outcome,
            "数据抽取",
            SemanticProbeEvaluator.EvaluateDataExtraction);
    }

    private static async Task<ProxyProbeScenarioResult> ProbeStructuredOutputEdgeScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        const string scenarioId = "SO-EDGE-01";
        var outcome = await ProbeJsonConversationScenarioAsync(
            client,
            transport,
            BuildStructuredOutputEdgePayload(model, scenarioId),
            ProxyProbeScenarioKind.StructuredOutputEdge,
            "结构化边界",
            cancellationToken);

        return ApplySemanticProbeEvaluation(
            outcome,
            "结构化边界",
            preview => SemanticProbeEvaluator.EvaluateStructuredOutputEdge(scenarioId, preview));
    }

    private static async Task<ProxyProbeScenarioResult> ProbeToolCallDeepScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        var outcome = await ProbeJsonConversationScenarioAsync(
            client,
            transport,
            BuildToolCallDeepPayload(model, "TC-DEEP-01"),
            ProxyProbeScenarioKind.ToolCallDeep,
            "ToolCall 深测",
            cancellationToken,
            static json => json);

        if (!outcome.ScenarioResult.Success)
        {
            return outcome.ScenarioResult;
        }

        var evaluation = ToolCallProbeEvaluator.Evaluate(
            "TC-DEEP-01",
            outcome.Preview ?? outcome.ScenarioResult.Preview ?? string.Empty,
            [new ToolCallExpectation("search_docs", new Dictionary<string, object?> { ["query"] = "relay cache isolation", ["limit"] = 5 })]);

        if (evaluation.Success)
        {
            return outcome.ScenarioResult with
            {
                SemanticMatch = true,
                Summary = evaluation.Summary,
                Preview = evaluation.NormalizedPreview ?? outcome.ScenarioResult.Preview,
                Trace = ApplyToolCallEvaluationToTrace(outcome.ScenarioResult.Trace, evaluation)
            };
        }

        return outcome.ScenarioResult with
        {
            CapabilityStatus = "异常",
            Success = false,
            SemanticMatch = false,
            Summary = evaluation.Summary,
            Preview = evaluation.NormalizedPreview ?? outcome.ScenarioResult.Preview,
            FailureKind = ProxyFailureKind.SemanticMismatch,
            FailureStage = "ToolCall 深测",
            Error = evaluation.Error ?? evaluation.Summary,
            Trace = ApplyToolCallEvaluationToTrace(outcome.ScenarioResult.Trace, evaluation)
        };
    }

    private static async Task<ProxyProbeScenarioResult> ProbeReasonMathConsistencyScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        const string scenarioId = "RM-CONS-01";
        var outcome = await ProbeJsonConversationScenarioAsync(
            client,
            transport,
            BuildReasonMathConsistencyPayload(model, scenarioId),
            ProxyProbeScenarioKind.ReasonMathConsistency,
            "推理一致性",
            cancellationToken);

        return ApplySemanticProbeEvaluation(
            outcome,
            "推理一致性",
            preview => SemanticProbeEvaluator.EvaluateReasonMathConsistency(scenarioId, preview));
    }

    private static async Task<ProxyProbeScenarioResult> ProbeCodeBlockDisciplineScenarioAsync(
        HttpClient client,
        ConversationProbeTransport transport,
        string model,
        CancellationToken cancellationToken)
    {
        const string scenarioId = "CB-DISC-01";
        var outcome = await ProbeJsonConversationScenarioAsync(
            client,
            transport,
            BuildCodeBlockDisciplinePayload(model, scenarioId),
            ProxyProbeScenarioKind.CodeBlockDiscipline,
            "代码块纪律",
            cancellationToken);

        return ApplySemanticProbeEvaluation(
            outcome,
            "代码块纪律",
            preview => SemanticProbeEvaluator.EvaluateCodeBlockDiscipline(scenarioId, preview));
    }

    private static ProxyProbeScenarioResult ApplySemanticProbeEvaluation(
        JsonProbeOutcome outcome,
        string displayName,
        Func<string?, SemanticProbeEvaluation> evaluator)
    {
        if (!outcome.ScenarioResult.Success)
        {
            return outcome.ScenarioResult;
        }

        var preview = string.IsNullOrWhiteSpace(outcome.Preview)
            ? outcome.ScenarioResult.Preview
            : outcome.Preview;
        var evaluation = evaluator(preview);
        if (evaluation.Success)
        {
            return outcome.ScenarioResult with
            {
                SemanticMatch = true,
                Summary = evaluation.Summary,
                Preview = evaluation.NormalizedPreview ?? preview,
                Trace = ApplySemanticEvaluationToTrace(outcome.ScenarioResult.Trace, evaluation)
            };
        }

        return outcome.ScenarioResult with
        {
            CapabilityStatus = "异常",
            Success = false,
            SemanticMatch = false,
            Summary = evaluation.Summary,
            Preview = evaluation.NormalizedPreview ?? preview,
            FailureKind = ProxyFailureKind.SemanticMismatch,
            FailureStage = displayName,
            Error = evaluation.Error ?? evaluation.Summary,
            Trace = ApplySemanticEvaluationToTrace(outcome.ScenarioResult.Trace, evaluation)
        };
    }

    private static ProxyProbeTrace? ApplySemanticEvaluationToTrace(
        ProxyProbeTrace? trace,
        SemanticProbeEvaluation evaluation)
        => trace is null
            ? null
            : trace with
            {
                ExtractedOutput = evaluation.NormalizedPreview ?? trace.ExtractedOutput,
                Checks = evaluation.Checks ?? trace.Checks,
                Verdict = evaluation.Success ? "通过" : "异常",
                FailureReason = evaluation.Success ? null : evaluation.Error ?? evaluation.Summary
            };

    private static ProxyProbeTrace? ApplyToolCallEvaluationToTrace(
        ProxyProbeTrace? trace,
        ToolCallProbeEvaluation evaluation)
        => trace is null
            ? null
            : trace with
            {
                ExtractedOutput = evaluation.NormalizedPreview ?? trace.ExtractedOutput,
                Checks = evaluation.Checks,
                Verdict = evaluation.Success ? "通过" : "异常",
                FailureReason = evaluation.Success ? null : evaluation.Error ?? evaluation.Summary
            };

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

    private static bool MatchesSystemPromptExpectation(string? preview)
    {
        var normalized = NormalizeProbeText(preview);
        return normalized.Contains("systemmappingok", StringComparison.Ordinal) &&
               !normalized.Contains("useroverridefail", StringComparison.Ordinal);
    }

    private static bool MatchesFunctionCallingArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            return root.TryGetProperty("status", out var statusElement) &&
                   statusElement.ValueKind == JsonValueKind.String &&
                   string.Equals(statusElement.GetString(), "proxy-ok", StringComparison.OrdinalIgnoreCase) &&
                   root.TryGetProperty("channel", out var channelElement) &&
                   channelElement.ValueKind == JsonValueKind.String &&
                   string.Equals(channelElement.GetString(), "function-calling", StringComparison.OrdinalIgnoreCase) &&
                   root.TryGetProperty("round", out var roundElement) &&
                   roundElement.ValueKind == JsonValueKind.Number &&
                   roundElement.TryGetInt32(out var round) &&
                   round == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesFunctionCallingFinalAnswer(string? preview)
        => NormalizeProbeText(preview).Contains("functioncallfinishok", StringComparison.Ordinal);

    private static bool MatchesMultiModalExpectation(string? preview)
        => NormalizeProbeText(preview).Contains("multimodalok", StringComparison.Ordinal);

    private static bool MatchesCacheProbeExpectation(string? preview)
        => NormalizeProbeText(preview).Contains("cacheprobeok", StringComparison.Ordinal);

    private static bool MatchesCacheIsolationExpectation(string? preview, string expectedOutput)
        => string.Equals(
            NormalizeProbeText(preview),
            NormalizeProbeText(expectedOutput),
            StringComparison.Ordinal);

    private static string PreviewLabelForSupplementalScenario(string? preview)
        => string.IsNullOrWhiteSpace(preview)
            ? "（无）"
            : preview.Trim();

    private static string NormalizeIntegrityOutput(string? value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();

    private static string BuildStreamingIntegrityDigest(string nonStreamText, string streamText, bool outputsEqual)
    {
        var digestBuilder = new StringBuilder();
        digestBuilder.Append($"非流式 {CountIntegrityLines(nonStreamText)} 行 / {nonStreamText.Length} 字符；");
        digestBuilder.Append($"流式 {CountIntegrityLines(streamText)} 行 / {streamText.Length} 字符；");
        digestBuilder.Append(outputsEqual
            ? "输出一致"
            : $"首处差异：{DescribeFirstDifference(nonStreamText, streamText)}");
        return digestBuilder.ToString();
    }

    private static string BuildOfficialReferenceIntegrityDigest(
        string relayText,
        string officialText,
        bool relayMatchesTemplate,
        bool officialMatchesTemplate,
        bool outputsEqual)
    {
        var digestBuilder = new StringBuilder();
        digestBuilder.Append($"待测接口 {CountIntegrityLines(relayText)} 行 / {relayText.Length} 字符 / {(relayMatchesTemplate ? "命中模板" : "未命中模板")}；");
        digestBuilder.Append($"官方 {CountIntegrityLines(officialText)} 行 / {officialText.Length} 字符 / {(officialMatchesTemplate ? "命中模板" : "未命中模板")}；");
        digestBuilder.Append(outputsEqual
            ? "待测接口与官方输出一致"
            : $"首处差异：{DescribeFirstDifference(relayText, officialText, "待测接口", "官方")}");
        return digestBuilder.ToString();
    }

    private static string BuildCacheIsolationDigest(
        string? primaryPreview,
        string? alternatePreview,
        string secretA,
        bool leakedPrimarySecret,
        bool previewsEqual)
    {
        var primary = string.IsNullOrWhiteSpace(primaryPreview) ? "（无）" : primaryPreview.Trim();
        var alternate = string.IsNullOrWhiteSpace(alternatePreview) ? "（无）" : alternatePreview.Trim();
        return $"A={primary}；B={alternate}；A-secret={(leakedPrimarySecret ? "出现在 B 返回中" : "未出现在 B 返回中")}；A/B {(previewsEqual ? "完全一致" : "不同")}";
    }

    private static int CountIntegrityLines(string value)
        => string.IsNullOrWhiteSpace(value)
            ? 0
            : value.Split('\n').Length;

    private static string DescribeFirstDifference(string left, string right)
        => DescribeFirstDifference(left, right, "非流式", "流式");

    private static string DescribeFirstDifference(string left, string right, string leftLabel, string rightLabel)
    {
        var sharedLength = Math.Min(left.Length, right.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            if (left[index] == right[index])
            {
                continue;
            }

            return $"位置 {index + 1}，{leftLabel} {EscapeDifferenceChar(left[index])}，{rightLabel} {EscapeDifferenceChar(right[index])}";
        }

        if (left.Length != right.Length)
        {
            return $"长度不同（{leftLabel} {left.Length}，{rightLabel} {right.Length}）";
        }

        return "未定位到明显差异";
    }

    private static string EscapeDifferenceChar(char value)
        => value switch
        {
            '\n' => "LF",
            '\r' => "CR",
            '\t' => "TAB",
            _ => $"“{value}”"
        };

    private static bool IsLikelyCacheHit(double? firstTtftMs, double? secondTtftMs, bool outputsEqual)
    {
        if (!outputsEqual || !firstTtftMs.HasValue || !secondTtftMs.HasValue)
        {
            return false;
        }

        return firstTtftMs.Value >= 500 &&
               secondTtftMs.Value <= 400 &&
               secondTtftMs.Value <= firstTtftMs.Value * 0.55 &&
               secondTtftMs.Value + 120 < firstTtftMs.Value;
    }

    private static string NormalizeProbeText(string? value)
        => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string FormatMillisecondsValue(TimeSpan? value)
        => value?.TotalMilliseconds.ToString("F0") + " ms" ?? "--";

    private static bool TryParseToolCallResponse(string json, out (string Id, string Name, string ArgumentsJson) toolCall)
    {
        toolCall = default;

        using var document = JsonDocument.Parse(json);
        if (TryParseOpenAiToolCallResponse(document.RootElement, out toolCall))
        {
            return true;
        }

        if (TryParseAnthropicToolCallResponse(document.RootElement, out toolCall))
        {
            return true;
        }

        return TryParseResponsesToolCallResponse(document.RootElement, out toolCall);
    }

    private static bool TryParseOpenAiToolCallResponse(JsonElement root, out (string Id, string Name, string ArgumentsJson) toolCall)
    {
        toolCall = default;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("tool_calls", out var toolCalls) ||
                toolCalls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var toolCallElement in toolCalls.EnumerateArray())
            {
                if (!toolCallElement.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !toolCallElement.TryGetProperty("function", out var functionElement) ||
                    functionElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!functionElement.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                toolCall = (
                    idElement.GetString() ?? "tool-call-1",
                    nameElement.GetString() ?? string.Empty,
                    TryExtractToolCallArgumentsJson(functionElement));
                return !string.IsNullOrWhiteSpace(toolCall.Name);
            }
        }

        return false;
    }

    private static bool TryParseAnthropicToolCallResponse(JsonElement root, out (string Id, string Name, string ArgumentsJson) toolCall)
    {
        toolCall = default;
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? "tool-call-1"
                : "tool-call-1";
            var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var argumentsJson = item.TryGetProperty("input", out var inputElement)
                ? inputElement.ValueKind == JsonValueKind.String
                    ? inputElement.GetString() ?? "{}"
                    : inputElement.GetRawText()
                : "{}";

            if (!string.IsNullOrWhiteSpace(name))
            {
                toolCall = (id, name, argumentsJson);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseResponsesToolCallResponse(JsonElement root, out (string Id, string Name, string ArgumentsJson) toolCall)
    {
        toolCall = default;
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = item.TryGetProperty("call_id", out var callIdElement) && callIdElement.ValueKind == JsonValueKind.String
                ? callIdElement.GetString() ?? "tool-call-1"
                : item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString() ?? "tool-call-1"
                    : "tool-call-1";
            var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var argumentsJson = item.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.ValueKind == JsonValueKind.String
                    ? argumentsElement.GetString() ?? "{}"
                    : argumentsElement.GetRawText()
                : "{}";

            if (!string.IsNullOrWhiteSpace(name))
            {
                toolCall = (id, name, argumentsJson);
                return true;
            }
        }

        return false;
    }

    private static string TryExtractToolCallArgumentsJson(JsonElement functionElement)
    {
        if (!functionElement.TryGetProperty("arguments", out var argumentsElement))
        {
            return "{}";
        }

        return argumentsElement.ValueKind == JsonValueKind.String
            ? argumentsElement.GetString() ?? "{}"
            : argumentsElement.GetRawText();
    }

    private static string? ExtractErrorPreview(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String)
                {
                    return errorElement.GetString();
                }

                if (errorElement.ValueKind == JsonValueKind.Object)
                {
                    if (errorElement.TryGetProperty("message", out var messageElement) &&
                        messageElement.ValueKind == JsonValueKind.String)
                    {
                        return messageElement.GetString();
                    }

                    if (errorElement.TryGetProperty("type", out var typeElement) &&
                        typeElement.ValueKind == JsonValueKind.String)
                    {
                        return typeElement.GetString();
                    }
                }
            }

            if (root.TryGetProperty("message", out var rootMessage) &&
                rootMessage.ValueKind == JsonValueKind.String)
            {
                return rootMessage.GetString();
            }
        }
        catch
        {
        }

        return ExtractBodySample(body);
    }

    private static bool LooksLikeTransparentBadRequest(string? body)
    {
        var normalized = NormalizeProbeText(ExtractErrorPreview(body));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.Contains("internalservererror", StringComparison.Ordinal) ||
            normalized.Contains("unexpectederror", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("invalid", StringComparison.Ordinal) ||
               normalized.Contains("required", StringComparison.Ordinal) ||
               normalized.Contains("missing", StringComparison.Ordinal) ||
               normalized.Contains("mustprovide", StringComparison.Ordinal) ||
               normalized.Contains("mustbeprovided", StringComparison.Ordinal) ||
               normalized.Contains("oneof", StringComparison.Ordinal) ||
               normalized.Contains("messages", StringComparison.Ordinal) ||
               normalized.Contains("input", StringComparison.Ordinal) ||
               normalized.Contains("previousresponseid", StringComparison.Ordinal) ||
               normalized.Contains("parameter", StringComparison.Ordinal) ||
               normalized.Contains("schema", StringComparison.Ordinal) ||
               normalized.Contains("json", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string>? MergeHeaders(params IReadOnlyList<string>?[] headerGroups)
    {
        var merged = headerGroups
            .Where(group => group is { Count: > 0 })
            .SelectMany(group => group!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return merged.Length == 0 ? null : merged;
    }
}
