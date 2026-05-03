using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.AdvancedTesting.Models;
using Xunit;

namespace RelayBench.Core.Tests.AdvancedTesting;

public sealed class AdvancedTestCatalogRedTeamTests
{
    [Fact]
    public void DefaultCatalog_ContainsRedTeamSuiteAndCases()
    {
        var cases = AdvancedTestCatalog.CreateDefaultCases();
        var byId = cases.ToDictionary(static item => item.Definition.TestId, StringComparer.OrdinalIgnoreCase);

        string[] expected =
        [
            "redteam_system_prompt_leak",
            "redteam_privacy_echo",
            "redteam_tool_overreach",
            "redteam_prompt_injection",
            "redteam_rag_poisoning",
            "redteam_malicious_url_command",
            "redteam_jailbreak_boundary"
        ];

        foreach (var id in expected)
        {
            Assert.Contains(id, byId.Keys);
            Assert.Equal(AdvancedTestCategory.SecurityRedTeam, byId[id].Definition.Category);
        }

        Assert.False(byId["redteam_jailbreak_boundary"].Definition.IsEnabledByDefault);
        foreach (var id in expected.Where(static id => id != "redteam_jailbreak_boundary"))
        {
            Assert.True(byId[id].Definition.IsEnabledByDefault);
        }

        var suites = AdvancedTestCatalog.CreateDefaultSuites(cases)
            .ToDictionary(static item => item.SuiteId, StringComparer.OrdinalIgnoreCase);
        var suite = suites["security-red-team"];

        Assert.Equal(AdvancedRiskLevel.Critical, suite.RiskLevel);
        foreach (var id in expected)
        {
            Assert.Contains(id, suite.Cases.Select(static item => item.TestId));
        }
    }
}
