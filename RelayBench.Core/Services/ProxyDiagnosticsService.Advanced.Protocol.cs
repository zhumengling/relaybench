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


}
