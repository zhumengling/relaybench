namespace RelayBench.Core.Support;

public sealed record CommandExecutionResult(
    string FileName,
    string CommandLine,
    bool Started,
    int? ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    string? Error);
