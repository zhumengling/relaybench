using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
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
}
