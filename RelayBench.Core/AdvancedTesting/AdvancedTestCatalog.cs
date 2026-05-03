using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.AdvancedTesting.TestCases;

namespace RelayBench.Core.AdvancedTesting;

public static class AdvancedTestCatalog
{
    public static IReadOnlyList<IAdvancedTestCase> CreateDefaultCases()
        =>
        [
            new ModelsEndpointTestCase(),
            new ChatCompletionTestCase(),
            new StreamingIntegrityTestCase(),
            new UsageAccountingTestCase(),
            new StreamingTerminalMetadataTestCase(),
            new ErrorPassThroughTestCase(),
            new ToolCallingBasicTestCase(),
            new ToolChoiceForcedTestCase(),
            new ToolCallingStreamTestCase(),
            new ToolResultRoundtripTestCase(),
            new JsonStructuredOutputTestCase(),
            new JsonStreamingIntegrityTestCase(),
            new JsonMarkdownFenceTestCase(),
            new JsonEscapeUnicodeTestCase(),
            new ReasoningCompatibilityTestCase(),
            new ReasoningChatParameterTestCase(),
            new ReasoningContentReplayTestCase(),
            new MultiTurnMemoryTestCase(),
            new MultiTurnFormatRetentionTestCase(),
            new ConcurrencyLimitTestCase(),
            new ConcurrencyStaircaseTestCase(),
            new EmbeddingsEndpointTestCase(),
            new EmbeddingsSimilarityTestCase(),
            new EmbeddingsEmptyInputTestCase(),
            new EmbeddingsLongTextTestCase(),
            new ModelFingerprintTestCase(),
            new LongContextNeedleTestCase(),
            new LongContext16KNeedleTestCase(),
            new SystemPromptLeakTestCase(),
            new PrivacyEchoTestCase(),
            new ToolOverreachTestCase(),
            new PromptInjectionTestCase(),
            new RagPoisoningTestCase(),
            new MaliciousUrlCommandTestCase(),
            new JailbreakBoundaryTestCase()
        ];

    public static IReadOnlyList<AdvancedTestSuiteDefinition> CreateDefaultSuites(IReadOnlyList<IAdvancedTestCase> cases)
    {
        var byId = cases.ToDictionary(static item => item.Definition.TestId, static item => item.Definition, StringComparer.OrdinalIgnoreCase);
        return
        [
            BuildSuite(
                byId,
                "basic",
                "基础兼容",
                "模型列表、非流式、流式、usage 与错误透传。",
                AdvancedRiskLevel.Low,
                "models_endpoint",
                "chat_non_stream",
                "streaming_integrity",
                "usage_accounting",
                "streaming_terminal_metadata",
                "error_pass_through"),
            BuildSuite(
                byId,
                "agent",
                "Agent 兼容",
                "Tool Calling、指定 tool_choice、多轮上下文与流式路径。",
                AdvancedRiskLevel.High,
                "tool_calling_basic",
                "tool_choice_forced",
                "tool_calling_stream",
                "tool_result_roundtrip",
                "multi_turn_memory",
                "multi_turn_format_retention",
                "streaming_integrity"),
            BuildSuite(
                byId,
                "json",
                "JSON 结构化",
                "JSON required、enum、嵌套对象、数组和中文字段。",
                AdvancedRiskLevel.Medium,
                "json_schema_required_enum",
                "json_streaming_integrity",
                "json_markdown_fence",
                "json_escape_unicode"),
            BuildSuite(
                byId,
                "reasoning",
                "Reasoning 兼容",
                "Responses API 与 reasoning.effort 轻量探测。",
                AdvancedRiskLevel.Medium,
                "reasoning_responses_probe",
                "reasoning_chat_parameter",
                "reasoning_content_replay"),
            BuildSuite(
                byId,
                "capacity",
                "稳定与容量",
                "多轮记忆、轻量并发限流和 8K needle 召回。",
                AdvancedRiskLevel.Medium,
                "multi_turn_memory",
                "concurrency_mini",
                "concurrency_staircase",
                "long_context_needle",
                "long_context_16k_needle"),
            BuildSuite(
                byId,
                "rag",
                "RAG 能力",
                "Embeddings 基础可用性与长文本后续扩展入口。",
                AdvancedRiskLevel.Medium,
                "embeddings_basic",
                "embeddings_similarity",
                "embeddings_empty_input",
                "embeddings_long_text",
                "json_schema_required_enum"),
            BuildSuite(
                byId,
                "model-risk",
                "模型风险",
                "模型自报、固定问题指纹和疑似不一致风险提示。",
                AdvancedRiskLevel.Medium,
                "model_fingerprint_light"),
            BuildSuite(
                byId,
                "security-red-team",
                "数据安全",
                "Prompt 注入、系统提示泄露、隐私回显、工具越权、RAG 污染、恶意 URL / 命令诱导和 Jailbreak 边界。",
                AdvancedRiskLevel.Critical,
                "redteam_system_prompt_leak",
                "redteam_privacy_echo",
                "redteam_tool_overreach",
                "redteam_prompt_injection",
                "redteam_rag_poisoning",
                "redteam_malicious_url_command",
                "redteam_jailbreak_boundary")
        ];
    }

    private static AdvancedTestSuiteDefinition BuildSuite(
        IReadOnlyDictionary<string, AdvancedTestCaseDefinition> definitions,
        string suiteId,
        string displayName,
        string description,
        AdvancedRiskLevel riskLevel,
        params string[] testIds)
        => new(
            suiteId,
            displayName,
            description,
            riskLevel,
            testIds.Where(definitions.ContainsKey).Select(id => definitions[id]).ToArray());
}
