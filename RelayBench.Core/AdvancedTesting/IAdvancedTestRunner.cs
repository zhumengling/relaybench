using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting;

public interface IAdvancedTestRunner
{
    IReadOnlyList<AdvancedTestSuiteDefinition> Suites { get; }

    Task<AdvancedTestRunResult> RunAsync(
        AdvancedTestPlan plan,
        IProgress<AdvancedTestProgress>? progress,
        CancellationToken cancellationToken);
}
