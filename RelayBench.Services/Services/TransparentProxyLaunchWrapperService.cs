using System.IO;
using System.Text;
using RelayBench.Services.Infrastructure;

namespace RelayBench.Services;

internal sealed class TransparentProxyLaunchWrapperService
{
    private readonly string _rootDirectory;
    private readonly TransparentProxyCliEnvironmentService _environmentService = new();
    private static readonly TransparentProxyLaunchWrapperTarget[] BuiltInTargets =
    [
        new("codex-cli", "Codex CLI", "codex"),
        new("claude-cli", "Claude CLI", "claude")
    ];

    public TransparentProxyLaunchWrapperService()
        : this(Path.Combine(RelayBenchPaths.DataDirectory, "transparent-proxy-launchers"))
    {
    }

    public TransparentProxyLaunchWrapperService(string rootDirectory)
    {
        _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(RelayBenchPaths.DataDirectory, "transparent-proxy-launchers")
            : Path.GetFullPath(rootDirectory);
    }

    public TransparentProxyLaunchWrapperPreview Preview(string id, string displayName, string command, int port)
    {
        var safeId = NormalizeId(id);
        var normalizedCommand = NormalizeCommand(command);
        var env = _environmentService.Build(port);
        var powerShellPath = Path.Combine(_rootDirectory, $"{safeId}.ps1");
        var cmdPath = Path.Combine(_rootDirectory, $"{safeId}.cmd");
        var useCodexCliOverride = IsCodexLauncher(safeId, normalizedCommand);
        return new TransparentProxyLaunchWrapperPreview(
            safeId,
            string.IsNullOrWhiteSpace(displayName) ? safeId : displayName.Trim(),
            normalizedCommand,
            powerShellPath,
            cmdPath,
            BuildPowerShellScript(normalizedCommand, env, useCodexCliOverride),
            BuildCmdScript(normalizedCommand, env, useCodexCliOverride));
    }

    public TransparentProxyLaunchWrapperWriteResult Write(string id, string displayName, string command, int port)
    {
        var preview = Preview(id, displayName, command, port);
        Directory.CreateDirectory(_rootDirectory);
        File.WriteAllText(preview.PowerShellPath, preview.PowerShellScript, new UTF8Encoding(false));
        File.WriteAllText(preview.CmdPath, preview.CmdScript, new UTF8Encoding(false));
        return new TransparentProxyLaunchWrapperWriteResult(
            true,
            $"已生成 {preview.DisplayName} 临时启动器；只影响通过该启动器启动的进程。",
            preview.PowerShellPath,
            preview.CmdPath);
    }

    public IReadOnlyList<TransparentProxyLaunchWrapperArtifact> ScanKnownLaunchers()
        => BuiltInTargets
            .Select(target =>
            {
                var safeId = NormalizeId(target.Id);
                var powerShellPath = Path.Combine(_rootDirectory, $"{safeId}.ps1");
                var cmdPath = Path.Combine(_rootDirectory, $"{safeId}.cmd");
                return new TransparentProxyLaunchWrapperArtifact(
                    safeId,
                    target.DisplayName,
                    powerShellPath,
                    cmdPath,
                    File.Exists(powerShellPath),
                    File.Exists(cmdPath));
            })
            .ToArray();

    public TransparentProxyLaunchWrapperCleanupResult DeleteKnownLaunchers()
    {
        var artifacts = ScanKnownLaunchers();
        List<string> deleted = [];
        List<string> failed = [];

        foreach (var path in artifacts.SelectMany(static item => item.ExistingPaths))
        {
            try
            {
                File.Delete(path);
                deleted.Add(path);
            }
            catch
            {
                failed.Add(path);
            }
        }

        var summary = failed.Count > 0
            ? $"临时启动器清理完成：删除 {deleted.Count} 个，失败 {failed.Count} 个。"
            : deleted.Count > 0
                ? $"已删除 {deleted.Count} 个 RelayBench 临时启动器。"
                : "未发现 RelayBench 临时启动器。";
        return new TransparentProxyLaunchWrapperCleanupResult(
            failed.Count == 0,
            deleted.Count,
            deleted,
            failed,
            summary);
    }

    private static string BuildPowerShellScript(
        string command,
        TransparentProxyCliEnvironmentSnapshot env,
        bool useCodexCliOverride)
    {
        List<string> lines =
        [
            "# RelayBench temporary launcher. This does not change system proxy or global environment variables.",
            $"$env:OPENAI_BASE_URL = '{EscapePowerShell(env.OpenAiBaseUrl)}'",
            $"$env:OPENAI_API_KEY = '{EscapePowerShell(env.LocalToken)}'",
            $"$env:ANTHROPIC_BASE_URL = '{EscapePowerShell(env.AnthropicBaseUrl)}'",
            $"$env:ANTHROPIC_AUTH_TOKEN = '{EscapePowerShell(env.LocalToken)}'",
            $"$env:RELAYBENCH_BASE_URL = '{EscapePowerShell(env.OpenAiBaseUrl)}'"
        ];

        if (useCodexCliOverride)
        {
            lines.Add($"$relayBenchCodexConfig = @('-c', \"openai_base_url='{EscapePowerShell(env.OpenAiBaseUrl)}'\")");
            lines.Add($"& '{EscapePowerShell(command)}' @relayBenchCodexConfig @args");
        }
        else
        {
            lines.Add($"& '{EscapePowerShell(command)}' @args");
        }

        lines.Add("exit $LASTEXITCODE");
        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildCmdScript(
        string command,
        TransparentProxyCliEnvironmentSnapshot env,
        bool useCodexCliOverride)
    {
        List<string> lines =
        [
            "@echo off",
            "rem RelayBench temporary launcher. This does not change system proxy or global environment variables.",
            $"set OPENAI_BASE_URL={env.OpenAiBaseUrl}",
            $"set OPENAI_API_KEY={env.LocalToken}",
            $"set ANTHROPIC_BASE_URL={env.AnthropicBaseUrl}",
            $"set ANTHROPIC_AUTH_TOKEN={env.LocalToken}",
            $"set RELAYBENCH_BASE_URL={env.OpenAiBaseUrl}"
        ];

        lines.Add(useCodexCliOverride
            ? $"\"{EscapeCmd(command)}\" -c \"openai_base_url='{env.OpenAiBaseUrl}'\" %*"
            : $"\"{EscapeCmd(command)}\" %*");
        lines.Add("exit /b %ERRORLEVEL%");
        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    private static string NormalizeId(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "launcher" : value.Trim();
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        return builder.ToString().Trim('-') is { Length: > 0 } safe ? safe : "launcher";
    }

    private static string NormalizeCommand(string value)
        => string.IsNullOrWhiteSpace(value) ? "cmd" : value.Trim();

    private static bool IsCodexLauncher(string id, string command)
        => id.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Path.GetFileNameWithoutExtension(command), "codex", StringComparison.OrdinalIgnoreCase);

    private static string EscapePowerShell(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string EscapeCmd(string value)
        => value.Replace("\"", "\"\"", StringComparison.Ordinal);
}

internal sealed record TransparentProxyLaunchWrapperPreview(
    string Id,
    string DisplayName,
    string Command,
    string PowerShellPath,
    string CmdPath,
    string PowerShellScript,
    string CmdScript);

internal sealed record TransparentProxyLaunchWrapperWriteResult(
    bool Succeeded,
    string Summary,
    string PowerShellPath,
    string CmdPath);

internal sealed record TransparentProxyLaunchWrapperTarget(
    string Id,
    string DisplayName,
    string Command);

internal sealed record TransparentProxyLaunchWrapperArtifact(
    string Id,
    string DisplayName,
    string PowerShellPath,
    string CmdPath,
    bool PowerShellExists,
    bool CmdExists)
{
    public int ExistingCount => (PowerShellExists ? 1 : 0) + (CmdExists ? 1 : 0);

    public IReadOnlyList<string> ExistingPaths
        => (PowerShellExists, CmdExists) switch
        {
            (true, true) => [PowerShellPath, CmdPath],
            (true, false) => [PowerShellPath],
            (false, true) => [CmdPath],
            _ => []
        };
}

internal sealed record TransparentProxyLaunchWrapperCleanupResult(
    bool Succeeded,
    int DeletedCount,
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<string> FailedPaths,
    string Summary);
