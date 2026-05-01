namespace RelayBench.Core.Tests;

internal static class TestCatalog
{
    public static IReadOnlyList<TestCase> All()
    {
        List<TestCase> tests = [];
        tests.AddRange(TestRunnerBehaviorTests.Create());
        tests.AddRange(ChatSseParserTests.Create());
        tests.AddRange(ChatRequestPayloadBuilderTests.Create());
        tests.AddRange(ChatMarkdownBlockParserTests.Create());
        tests.AddRange(ChatConversationServiceTests.Create());
        tests.AddRange(SemanticProbeEvaluatorTests.Create());
        tests.AddRange(ToolCallProbeEvaluatorTests.Create());
        tests.AddRange(ProtocolProbeTests.Create());
        tests.AddRange(ProxyEndpointModelCacheServiceTests.Create());
        tests.AddRange(ClientApplyTests.Create());
        tests.AddRange(NetworkDiagnosticsServiceTests.Create());
        tests.AddRange(ProxyDiagnosticsProbeTests.Create());
        tests.AddRange(ChartProjectionTests.Create());
        tests.AddRange(ProbeTraceTests.Create());
        tests.AddRange(ModelChatWorkflowTests.Create());
        tests.AddRange(UiWorkflowTests.Create());

        return tests;
    }
}
