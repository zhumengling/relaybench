using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting.Scoring;

public sealed class AdvancedScoreCalculator : IScoreCalculator
{
    public AdvancedScenarioScores Calculate(IReadOnlyList<AdvancedTestCaseResult> results)
    {
        if (results.Count == 0)
        {
            return new AdvancedScenarioScores(0, 0, 0, 0, 0);
        }

        var protocolCompatibility = WeightedScore(results, AdvancedTestCategory.BasicCompatibility, AdvancedTestCategory.AgentCompatibility, AdvancedTestCategory.StructuredOutput, AdvancedTestCategory.ReasoningCompatibility);
        var stability = WeightedScore(results, AdvancedTestCategory.Stability, AdvancedTestCategory.Concurrency);
        var performance = WeightedScore(results, AdvancedTestCategory.BasicCompatibility, AdvancedTestCategory.Concurrency);
        var modelCapability = WeightedScore(results, AdvancedTestCategory.StructuredOutput, AdvancedTestCategory.ModelConsistency, AdvancedTestCategory.LongContext);
        var costTransparency = UsageScore(results);
        var networkRisk = RiskScore(results);

        var overall =
            protocolCompatibility * 0.25d +
            stability * 0.20d +
            performance * 0.20d +
            modelCapability * 0.15d +
            costTransparency * 0.10d +
            networkRisk * 0.10d;

        var toolCalling = TestScore(results, "tool_calling_basic", "tool_choice_forced", "tool_calling_stream", "tool_result_roundtrip");
        var streaming = TestScore(results, "streaming_integrity");
        var reasoning = WeightedScore(results, AdvancedTestCategory.ReasoningCompatibility);
        var context = WeightedScore(results, AdvancedTestCategory.LongContext);
        var errorPassThrough = TestScore(results, "error_pass_through");

        var codexFit =
            toolCalling * 0.30d +
            streaming * 0.25d +
            reasoning * 0.20d +
            context * 0.15d +
            errorPassThrough * 0.10d;

        var multiTurn = TestScore(results, "multi_turn_memory");
        var json = WeightedScore(results, AdvancedTestCategory.StructuredOutput);
        var concurrency = WeightedScore(results, AdvancedTestCategory.Concurrency);
        var agentFit =
            toolCalling * 0.35d +
            multiTurn * 0.20d +
            json * 0.20d +
            stability * 0.15d +
            concurrency * 0.10d;

        var embeddings = WeightedScore(results, AdvancedTestCategory.Rag);
        var longText = TestScore(results, "long_context_needle", "long_context_16k_needle", "embeddings_similarity", "embeddings_long_text");
        var ragFit =
            embeddings * 0.35d +
            longText * 0.25d +
            json * 0.20d +
            stability * 0.20d;

        var ttft = TestScore(results, "streaming_integrity");
        var throughput = performance;
        var chatExperience =
            ttft * 0.30d +
            throughput * 0.25d +
            multiTurn * 0.20d +
            stability * 0.15d +
            costTransparency * 0.10d;

        return new AdvancedScenarioScores(
            ClampScore(overall),
            ClampScore(codexFit),
            ClampScore(agentFit),
            ClampScore(ragFit),
            ClampScore(chatExperience));
    }

    private static double WeightedScore(
        IReadOnlyList<AdvancedTestCaseResult> results,
        params AdvancedTestCategory[] categories)
    {
        var selected = results
            .Where(item => categories.Contains(item.Category) && item.Status != AdvancedTestStatus.Skipped)
            .ToArray();
        return WeightedAverage(selected);
    }

    private static double TestScore(IReadOnlyList<AdvancedTestCaseResult> results, params string[] testIds)
    {
        var selected = results
            .Where(item => testIds.Contains(item.TestId, StringComparer.OrdinalIgnoreCase) && item.Status != AdvancedTestStatus.Skipped)
            .ToArray();
        return WeightedAverage(selected);
    }

    private static double WeightedAverage(IReadOnlyList<AdvancedTestCaseResult> results)
    {
        if (results.Count == 0)
        {
            return 60;
        }

        var weight = results.Sum(static item => Math.Max(item.Weight, 0.1d));
        if (weight <= 0)
        {
            return 0;
        }

        return ClampScore(results.Sum(static item => item.Score * Math.Max(item.Weight, 0.1d)) / weight);
    }

    private static double UsageScore(IReadOnlyList<AdvancedTestCaseResult> results)
    {
        var usageMissing = results.Count(static item => item.ErrorKind == AdvancedErrorKind.UsageMissing);
        if (usageMissing == 0)
        {
            return 88;
        }

        return ClampScore(88 - usageMissing * 12);
    }

    private static double RiskScore(IReadOnlyList<AdvancedTestCaseResult> results)
    {
        var penalty = results.Sum(static item => item.RiskLevel switch
        {
            AdvancedRiskLevel.Critical => 28,
            AdvancedRiskLevel.High => 18,
            AdvancedRiskLevel.Medium => 8,
            _ => 0
        });

        return ClampScore(100 - penalty);
    }

    private static double ClampScore(double value)
        => Math.Round(Math.Clamp(value, 0d, 100d), 1);
}
