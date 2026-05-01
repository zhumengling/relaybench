using RelayBench.Core.Tests;

var failed = TestRunner.Run(TestCatalog.All(), TestRunOptions.FromArgs(args));
if (failed > 0)
{
    Environment.ExitCode = 1;
}
