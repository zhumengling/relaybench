using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting;

public interface IAdvancedTestCase
{
    AdvancedTestCaseDefinition Definition { get; }

    Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken);
}
