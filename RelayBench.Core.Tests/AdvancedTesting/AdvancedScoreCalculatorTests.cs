using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.AdvancedTesting.Scoring;
using Xunit;

namespace RelayBench.Core.Tests.AdvancedTesting;

public sealed class AdvancedScoreCalculatorTests
{
    [Fact]
    public void Calculate_GivesHighCodexScoreWhenCoreAgentSignalsPass()
    {
        var calculator = new AdvancedScoreCalculator();
        var results = new[]
        {
            Result("tool_calling_basic", AdvancedTestCategory.AgentCompatibility, 100),
            Result("tool_choice_forced", AdvancedTestCategory.AgentCompatibility, 90),
            Result("streaming_integrity", AdvancedTestCategory.BasicCompatibility, 100),
            Result("reasoning_responses_probe", AdvancedTestCategory.ReasoningCompatibility, 80),
            Result("long_context_needle", AdvancedTestCategory.LongContext, 70),
            Result("error_pass_through", AdvancedTestCategory.BasicCompatibility, 100)
        };

        var scores = calculator.Calculate(results);

        Assert.True(scores.CodexFit >= 85, $"CodexFit was {scores.CodexFit}");
        Assert.InRange(scores.Overall, 0, 100);
    }

    [Fact]
    public void Calculate_PenalizesHighRiskFailures()
    {
        var calculator = new AdvancedScoreCalculator();
        var results = new[]
        {
            Result("chat_non_stream", AdvancedTestCategory.BasicCompatibility, 100),
            Result("tool_calling_basic", AdvancedTestCategory.AgentCompatibility, 0, AdvancedTestStatus.Failed, AdvancedRiskLevel.High),
            Result("streaming_integrity", AdvancedTestCategory.BasicCompatibility, 0, AdvancedTestStatus.Failed, AdvancedRiskLevel.High)
        };

        var scores = calculator.Calculate(results);

        Assert.True(scores.Overall < 80, $"Overall was {scores.Overall}");
        Assert.True(scores.AgentFit < 70, $"AgentFit was {scores.AgentFit}");
    }

    private static AdvancedTestCaseResult Result(
        string id,
        AdvancedTestCategory category,
        double score,
        AdvancedTestStatus status = AdvancedTestStatus.Passed,
        AdvancedRiskLevel risk = AdvancedRiskLevel.Low)
        => new(
            id,
            id,
            category,
            status,
            score,
            1,
            TimeSpan.FromMilliseconds(10),
            "request",
            "response",
            null,
            null,
            AdvancedErrorKind.None,
            string.Empty,
            string.Empty,
            risk,
            Array.Empty<string>(),
            Array.Empty<AdvancedCheckResult>());
}
