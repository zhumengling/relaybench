using RelayBench.Core.AdvancedTesting;
using Xunit;

namespace RelayBench.Core.Tests.AdvancedTesting;

public sealed class AdvancedTestCatalogSecondBatchTests
{
    [Fact]
    public void DefaultCatalog_ContainsSecondBatchTestCases()
    {
        var cases = AdvancedTestCatalog.CreateDefaultCases();
        var ids = cases.Select(static item => item.Definition.TestId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] expected =
        [
            "tool_calling_stream",
            "tool_result_roundtrip",
            "json_streaming_integrity",
            "reasoning_chat_parameter",
            "long_context_16k_needle",
            "concurrency_staircase",
            "embeddings_similarity"
        ];

        foreach (var id in expected)
        {
            Assert.Contains(id, ids);
        }

        var suites = AdvancedTestCatalog.CreateDefaultSuites(cases)
            .ToDictionary(static item => item.SuiteId, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("tool_calling_stream", suites["agent"].Cases.Select(static item => item.TestId));
        Assert.Contains("tool_result_roundtrip", suites["agent"].Cases.Select(static item => item.TestId));
        Assert.Contains("json_streaming_integrity", suites["json"].Cases.Select(static item => item.TestId));
        Assert.Contains("reasoning_chat_parameter", suites["reasoning"].Cases.Select(static item => item.TestId));
        Assert.Contains("long_context_16k_needle", suites["capacity"].Cases.Select(static item => item.TestId));
        Assert.Contains("concurrency_staircase", suites["capacity"].Cases.Select(static item => item.TestId));
        Assert.Contains("embeddings_similarity", suites["rag"].Cases.Select(static item => item.TestId));
    }
}
