using RelayBench.Core.Services;
using Xunit;

namespace RelayBench.Core.Tests;

public sealed class SemanticProbeEvaluatorReasonMathTests
{
    [Theory]
    [InlineData("ANSWER: 34.50\nCHECKS: subtotal=120.00, tax=9.60, tip=8.40, total=138.00, split=4")]
    [InlineData("ANSWER 34.50 CNY\nCHECKS tax=9.60, tip=8.40, subtotal=120.00, total=138.00, split=34.50")]
    [InlineData("ANSWER\n34.50\nCHECKS\nSubtotal: 120.00, Tax: 9.60, Tip: 8.40, Total: 138.00, Per person: 34.50")]
    public void EvaluateReasonMathConsistency_AcceptsParseableAnswerAndCheckFormats(string output)
    {
        var result = SemanticProbeEvaluator.EvaluateReasonMathConsistency("RM-CONS-01", output);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void EvaluateReasonMathConsistency_StillRejectsWrongFinalAnswer()
    {
        const string output = "ANSWER: 34.20\nCHECKS: subtotal=120.00, tax=9.60, tip=8.40, total=138.00, per person=34.50";

        var result = SemanticProbeEvaluator.EvaluateReasonMathConsistency("RM-CONS-01", output);

        Assert.False(result.Success);
        Assert.Contains("drift", string.Join(" ", result.Checks?.Select(static item => item.Detail) ?? []), StringComparison.OrdinalIgnoreCase);
    }
}
