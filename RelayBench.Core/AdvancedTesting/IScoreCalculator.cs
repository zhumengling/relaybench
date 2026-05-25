using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting;

public interface IScoreCalculator
{
    AdvancedScenarioScores Calculate(IReadOnlyList<AdvancedTestCaseResult> results);
}
