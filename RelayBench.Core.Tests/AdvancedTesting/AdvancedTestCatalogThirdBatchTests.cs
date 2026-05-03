using RelayBench.Core.AdvancedTesting;
using Xunit;

namespace RelayBench.Core.Tests.AdvancedTesting;

public sealed class AdvancedTestCatalogThirdBatchTests
{
    [Fact]
    public void DefaultCatalog_ContainsThirdBatchTestCases()
    {
        var cases = AdvancedTestCatalog.CreateDefaultCases();
        var ids = cases.Select(static item => item.Definition.TestId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] expected =
        [
            "usage_accounting",
            "streaming_terminal_metadata",
            "json_markdown_fence",
            "json_escape_unicode",
            "multi_turn_format_retention",
            "reasoning_content_replay",
            "embeddings_empty_input",
            "embeddings_long_text"
        ];

        foreach (var id in expected)
        {
            Assert.Contains(id, ids);
        }

        var suites = AdvancedTestCatalog.CreateDefaultSuites(cases)
            .ToDictionary(static item => item.SuiteId, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("usage_accounting", suites["basic"].Cases.Select(static item => item.TestId));
        Assert.Contains("streaming_terminal_metadata", suites["basic"].Cases.Select(static item => item.TestId));
        Assert.Contains("json_markdown_fence", suites["json"].Cases.Select(static item => item.TestId));
        Assert.Contains("json_escape_unicode", suites["json"].Cases.Select(static item => item.TestId));
        Assert.Contains("multi_turn_format_retention", suites["agent"].Cases.Select(static item => item.TestId));
        Assert.Contains("reasoning_content_replay", suites["reasoning"].Cases.Select(static item => item.TestId));
        Assert.Contains("embeddings_empty_input", suites["rag"].Cases.Select(static item => item.TestId));
        Assert.Contains("embeddings_long_text", suites["rag"].Cases.Select(static item => item.TestId));
    }
}
