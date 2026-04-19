using System.Diagnostics;
using System.IO;
using System.Text;

namespace NetTest.Core.Support;

public static class CommandLineRunner
{
    public static async Task<CommandExecutionResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => await RunStreamingAsync(
            fileName,
            arguments,
            timeout,
            null,
            null,
            cancellationToken);

    public static async Task<CommandExecutionResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string>? standardOutputLineReceived = null,
        Action<string>? standardErrorLineReceived = null,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new()
        {
            StartInfo = startInfo
        };

        var commandLine = BuildCommandLine(fileName, arguments);

        try
        {
            if (!process.Start())
            {
                return new CommandExecutionResult(
                    fileName,
                    commandLine,
                    false,
                    null,
                    false,
                    string.Empty,
                    string.Empty,
                    "The process could not be started.");
            }
        }
        catch (Exception ex)
        {
            return new CommandExecutionResult(
                fileName,
                commandLine,
                false,
                null,
                false,
                string.Empty,
                string.Empty,
                ex.Message);
        }

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutTask = ConsumeLinesAsync(process.StandardOutput, stdoutBuilder, standardOutputLineReceived);
        var stderrTask = ConsumeLinesAsync(process.StandardError, stderrBuilder, standardErrorLineReceived);
        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(timeout);

        try
        {
            var completedTask = await Task.WhenAny(waitForExitTask, timeoutTask);
            if (completedTask == timeoutTask)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);

                return new CommandExecutionResult(
                    fileName,
                    commandLine,
                    true,
                    process.ExitCode,
                    true,
                    await stdoutTask,
                    await stderrTask,
                    $"The command timed out after {timeout.TotalSeconds:F0} seconds.");
            }

            await waitForExitTask;
            await Task.WhenAll(stdoutTask, stderrTask);
            return new CommandExecutionResult(
                fileName,
                commandLine,
                true,
                process.ExitCode,
                false,
                await stdoutTask,
                await stderrTask,
                null);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }
    }

    private static async Task<string> ConsumeLinesAsync(
        StreamReader reader,
        StringBuilder builder,
        Action<string>? onLineReceived)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line);

            if (onLineReceived is null)
            {
                continue;
            }

            try
            {
                onLineReceived(line);
            }
            catch
            {
                // Swallow callback exceptions so command collection can continue.
            }
        }

        return builder.ToString();
    }

    private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        StringBuilder builder = new(fileName);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(QuoteArgument(argument));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        if (!argument.Any(static character => char.IsWhiteSpace(character) || character is '"'))
        {
            return argument;
        }

        return $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore kill failures and rely on the caller to report the command outcome.
        }
    }
}
