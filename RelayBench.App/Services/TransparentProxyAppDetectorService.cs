using System.Diagnostics;
using System.IO;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyAppDetectorService
{
    public IReadOnlyList<TransparentProxyDetectedApp> Detect()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return
        [
            DetectPathBackedApp(
                "codex-cli",
                "Codex CLI",
                "Codex config",
                Path.Combine(userProfile, ".codex", "config.toml"),
                ["codex.exe", "codex.cmd", "codex.ps1"],
                ["codex"],
                [
                    Path.Combine(appData, "npm", "codex.cmd"),
                    Path.Combine(appData, "npm", "codex.ps1"),
                    Path.Combine(userProfile, ".local", "bin", "codex.exe")
                ]),
            DetectPathBackedApp(
                "claude-cli",
                "Claude CLI",
                "settings env",
                Path.Combine(userProfile, ".claude", "settings.json"),
                ["claude.exe", "claude.cmd", "claude.ps1"],
                ["claude"],
                [
                    Path.Combine(appData, "npm", "claude.cmd"),
                    Path.Combine(appData, "npm", "claude.ps1"),
                    Path.Combine(userProfile, ".local", "bin", "claude.exe")
                ]),
            DetectPathBackedApp(
                "vs-codex",
                "VS Codex / VS Code",
                "workspace settings",
                Path.Combine(appData, "Code", "User", "settings.json"),
                ["code.exe", "code.cmd"],
                ["Code"],
                [
                    Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
                    Path.Combine(localAppData, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe")
                ]),
            DetectCodexDesktop(userProfile, localAppData)
        ];
    }

    private static TransparentProxyDetectedApp DetectPathBackedApp(
        string id,
        string displayName,
        string recommendedMode,
        string configPath,
        IReadOnlyList<string> executableNames,
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> fallbackExecutablePaths)
    {
        var executablePath =
            FindExecutableOnPath(executableNames) ??
            FindExistingPath(fallbackExecutablePaths) ??
            FindRunningProcessExecutable(processNames);
        var configExists = File.Exists(configPath);
        var running = IsAnyProcessRunning(processNames);
        var status = BuildStatus(executablePath, configExists, running);

        return new TransparentProxyDetectedApp(
            id,
            displayName,
            recommendedMode,
            status,
            executablePath,
            configPath);
    }

    private static TransparentProxyDetectedApp DetectCodexDesktop(string userProfile, string localAppData)
    {
        var configPath = Path.Combine(userProfile, ".codex", "config.toml");
        var executablePath =
            FindExistingPath(
            [
                Path.Combine(localAppData, "Programs", "Codex", "Codex.exe"),
                Path.Combine(localAppData, "Codex", "Codex.exe")
            ]) ??
            FindRunningProcessExecutable(["Codex"]);
        var running = IsAnyProcessRunning(["Codex"]);
        var configExists = File.Exists(configPath);
        var status = executablePath is not null || running
            ? configExists ? "运行中，可复用 Codex config" : "运行中，未确认稳定配置入口"
            : configExists ? "可复用 Codex config" : "未检测到稳定配置入口";

        return new TransparentProxyDetectedApp(
            "codex-desktop",
            "Codex 桌面端",
            "待确认",
            status,
            executablePath,
            configPath);
    }

    private static string BuildStatus(string? executablePath, bool configExists, bool running)
    {
        if (running && configExists)
        {
            return "运行中，配置已就绪";
        }

        if (running)
        {
            return "运行中，配置待创建";
        }

        if (executablePath is not null && configExists)
        {
            return "已检测到";
        }

        if (executablePath is not null)
        {
            return "已检测到，配置待创建";
        }

        return configExists ? "配置存在，命令未入 PATH" : "未检测到";
    }

    private static string? FindExecutableOnPath(IReadOnlyList<string> executableNames)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? FindExistingPath(IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? FindRunningProcessExecutable(IReadOnlyList<string> processNames)
    {
        foreach (var processName in processNames)
        {
            foreach (var process in SafeGetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            return path;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        return null;
    }

    private static bool IsAnyProcessRunning(IReadOnlyList<string> processNames)
    {
        foreach (var processName in processNames)
        {
            var processes = SafeGetProcessesByName(processName);
            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    private static Process[] SafeGetProcessesByName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return [];
        }

        try
        {
            return Process.GetProcessesByName(processName);
        }
        catch
        {
            return [];
        }
    }
}

public sealed record TransparentProxyDetectedApp(
    string Id,
    string DisplayName,
    string RecommendedMode,
    string Status,
    string? ExecutablePath,
    string? ConfigPath);
