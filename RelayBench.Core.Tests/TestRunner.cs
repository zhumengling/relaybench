using System.Diagnostics;

namespace RelayBench.Core.Tests;

internal static class TestRunner
{
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(30);

    public static int Run(IReadOnlyList<TestCase> tests, TestRunOptions? options = null)
        => RunAsync(tests, options).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(IReadOnlyList<TestCase> tests, TestRunOptions? options = null)
    {
        options ??= new TestRunOptions();
        var selected = tests.Where(test => ShouldRun(test, options)).ToArray();
        var skipped = tests.Count - selected.Length;
        var failed = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (var test in selected)
        {
            var caseStopwatch = Stopwatch.StartNew();
            try
            {
                await RunWithTimeoutAsync(test, options);
                if (!options.Quiet)
                {
                    Console.WriteLine($"PASS [{test.Group}] {test.Name} ({caseStopwatch.ElapsedMilliseconds} ms)");
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (!options.Quiet)
                {
                    Console.Error.WriteLine($"FAIL [{test.Group}] {test.Name} ({caseStopwatch.ElapsedMilliseconds} ms): {ex.Message}");
                }
            }
        }

        stopwatch.Stop();
        if (!options.Quiet)
        {
            Console.WriteLine($"RESULT total={selected.Length} failed={failed} skipped={skipped} elapsed={stopwatch.ElapsedMilliseconds} ms");
        }
        if (failed > 0)
        {
            if (!options.Quiet)
            {
                Console.Error.WriteLine($"{failed}/{selected.Length} tests failed.");
            }
        }

        return failed;
    }

    private static bool ShouldRun(TestCase test, TestRunOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.GroupFilter) &&
            !test.Group.Contains(options.GroupFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.NameFilter) &&
            !test.Name.Contains(options.NameFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static async Task RunWithTimeoutAsync(TestCase test, TestRunOptions options)
    {
        var timeout = test.Timeout ?? options.DefaultTimeout ?? FallbackTimeout;
        using var cancellationSource = new CancellationTokenSource(timeout);
        try
        {
            await test.RunAsync(cancellationSource.Token).WaitAsync(timeout);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            throw new TimeoutException($"Test exceeded timeout {timeout.TotalMilliseconds:F0} ms.");
        }
        catch (TimeoutException)
        {
            cancellationSource.Cancel();
            throw new TimeoutException($"Test exceeded timeout {timeout.TotalMilliseconds:F0} ms.");
        }
    }
}
