namespace RelayBench.Core.Tests;

internal sealed class TestCase
{
    private readonly Func<CancellationToken, Task> _body;

    public TestCase(
        string name,
        Action body,
        string? group = null,
        TimeSpan? timeout = null)
        : this(
            name,
            _ =>
            {
                body();
                return Task.CompletedTask;
            },
            group,
            timeout)
    {
    }

    public TestCase(
        string name,
        Func<Task> body,
        string? group = null,
        TimeSpan? timeout = null)
        : this(name, _ => body(), group, timeout)
    {
    }

    public TestCase(
        string name,
        Func<CancellationToken, Task> body,
        string? group = null,
        TimeSpan? timeout = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name.Trim();
        Group = string.IsNullOrWhiteSpace(group) ? "default" : group.Trim();
        Timeout = timeout;
        _body = body;
    }

    public string Name { get; }

    public string Group { get; }

    public TimeSpan? Timeout { get; }

    public Task RunAsync(CancellationToken cancellationToken)
        => _body(cancellationToken);
}

internal sealed record TestRunOptions(
    string? GroupFilter = null,
    string? NameFilter = null,
    TimeSpan? DefaultTimeout = null,
    bool Quiet = false)
{
    public static TestRunOptions FromArgs(string[] args)
    {
        string? group = null;
        string? name = null;
        TimeSpan? timeout = null;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.Equals("--group", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                group = args[++index];
                continue;
            }

            if (arg.Equals("--name", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                name = args[++index];
                continue;
            }

            if (arg.Equals("--timeout-ms", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length &&
                int.TryParse(args[++index], out var milliseconds) &&
                milliseconds > 0)
            {
                timeout = TimeSpan.FromMilliseconds(milliseconds);
            }
        }

        return new TestRunOptions(group, name, timeout);
    }
}
