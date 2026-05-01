using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class TestRunnerBehaviorTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("test runner filters cases by group", () =>
    {
        var chatRuns = 0;
        var protocolRuns = 0;
        var failed = TestRunner.Run(
        [
            new TestCase("chat case", () => chatRuns++, group: "chat"),
            new TestCase("protocol case", () => protocolRuns++, group: "protocol")
        ],
            new TestRunOptions(GroupFilter: "chat", Quiet: true));

        AssertTrue(failed == 0, $"Expected no nested failures, got {failed}.");
        AssertTrue(chatRuns == 1, $"Expected chat case to run once, got {chatRuns}.");
        AssertTrue(protocolRuns == 0, $"Expected protocol case to be skipped, got {protocolRuns}.");
        }, group: "runner");

        yield return new TestCase("test runner fails async cases that exceed timeout", () =>
    {
        var failed = TestRunner.Run(
        [
            new TestCase(
                "slow async case",
                async cancellationToken =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                },
                group: "runner",
                timeout: TimeSpan.FromMilliseconds(20))
        ],
            new TestRunOptions(Quiet: true));

        AssertTrue(failed == 1, $"Expected the nested slow case to fail, got {failed} failures.");
        }, group: "runner");
    }
}
