using System.IO;
using System.Security.Principal;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyNetworkGuardService
{
    private static readonly string[] RequiredDirectFragments =
    [
        "127.0.0.0/8",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "github.com,DIRECT",
        "githubusercontent.com,DIRECT",
        "npmjs.org,DIRECT",
        "npmjs.com,DIRECT",
        "marketplace.visualstudio.com,DIRECT",
        "DOMAIN,chat.openai.com,DIRECT",
        "DOMAIN,platform.openai.com,DIRECT",
        "DOMAIN-SUFFIX,chatgpt.com,DIRECT",
        "DOMAIN-SUFFIX,oaistatic.com,DIRECT",
        "DOMAIN-SUFFIX,oaiusercontent.com,DIRECT",
        "MATCH,DIRECT"
    ];

    private static readonly string[] RequiredAiFragments =
    [
        "api.openai.com,RelayBench",
        "api.anthropic.com,RelayBench"
    ];

    private static readonly string[] ForbiddenProcessCaptures =
    [
        "PROCESS-NAME,Code.exe,RelayBench",
        "PROCESS-NAME,node.exe,RelayBench",
        "PROCESS-NAME,codex.exe,RelayBench",
        "PROCESS-NAME,claude.exe,RelayBench"
    ];

    private static readonly string[] SidecarExecutableNames =
    [
        "mihomo.exe",
        "clash-meta.exe",
        "verge-mihomo.exe",
        "mihomo-windows-amd64.exe"
    ];

    public TransparentProxyNetworkGuardSnapshot Inspect()
    {
        var diagnostics = new List<string>();
        var isAdministrator = IsRunningAsAdministrator();
        diagnostics.Add(isAdministrator
            ? "管理员权限：已满足，可启动 TUN sidecar。"
            : "管理员权限：不足，TUN 只能预览，启动前需要以管理员身份运行 RelayBench。");

        var sidecarPath = FindMihomoSidecarPath();
        diagnostics.Add(string.IsNullOrWhiteSpace(sidecarPath)
            ? "mihomo sidecar：未找到。已自动查找 mihomo.exe / clash-meta.exe / verge-mihomo.exe、程序目录、tools\\mihomo、PATH 和 Clash Verge Rev 常见目录。"
            : $"mihomo sidecar：{sidecarPath}");

        diagnostics.Add("安全策略：默认只接管 OpenAI / Anthropic API 域名，ChatGPT/OpenAI 官网、GitHub、npm、VS Code marketplace、局域网和本机地址直连。TUN 是 tunnel-only，不替换 API Key 或 Base URL。");
        return new TransparentProxyNetworkGuardSnapshot(isAdministrator, sidecarPath, diagnostics);
    }

    public TransparentProxyNetworkGuardValidationResult ValidateMihomoConfig(string? configText)
    {
        var issues = new List<string>();
        var normalized = (configText ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            issues.Add("TUN 配置为空。");
        }

        foreach (var fragment in RequiredDirectFragments)
        {
            if (!normalized.Contains(fragment.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"缺少 DIRECT 防回环/防误接管规则：{fragment}");
            }
        }

        foreach (var fragment in RequiredAiFragments)
        {
            if (!normalized.Contains(fragment.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"缺少 AI API 接管规则：{fragment}");
            }
        }

        foreach (var fragment in ForbiddenProcessCaptures)
        {
            if (normalized.Contains(fragment.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"默认配置不能按进程全量接管：{fragment}");
            }
        }

        if (normalized.Contains("MATCH,RelayBench", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("默认 TUN 配置不能把 MATCH 指向 RelayBench，必须保留 MATCH,DIRECT。");
        }

        return new TransparentProxyNetworkGuardValidationResult(issues.Count == 0, issues);
    }

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string? FindMihomoSidecarPath()
    {
        foreach (var candidate in EnumerateMihomoCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(candidate))
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

    private static IEnumerable<string> EnumerateMihomoCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var rootDirectory = RelayBenchPaths.RootDirectory;
        var currentDirectory = Environment.CurrentDirectory;

        foreach (var root in new[] { baseDirectory, rootDirectory, currentDirectory })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var candidate in EnumerateSidecarPathsUnder(root))
            {
                yield return candidate;
            }
        }

        foreach (var root in EnumerateKnownSidecarRoots())
        {
            foreach (var candidate in EnumerateSidecarPathsUnder(root))
            {
                yield return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var item in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in SidecarExecutableNames)
            {
                yield return Path.Combine(item, name);
            }
        }
    }

    private static IEnumerable<string> EnumerateSidecarPathsUnder(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            yield break;
        }

        var subDirectories = new[]
        {
            string.Empty,
            "tools",
            Path.Combine("tools", "mihomo"),
            Path.Combine("tools", "clash"),
            "sidecar",
            "resources",
            Path.Combine("resources", "sidecar"),
            Path.Combine("resources", "mihomo"),
            Path.Combine("resources", "resources", "sidecar")
        };

        foreach (var directory in subDirectories)
        {
            foreach (var name in SidecarExecutableNames)
            {
                yield return string.IsNullOrWhiteSpace(directory)
                    ? Path.Combine(root, name)
                    : Path.Combine(root, directory, name);
            }
        }
    }

    private static IEnumerable<string> EnumerateKnownSidecarRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            yield return Path.Combine(root, "Clash Verge Rev");
            yield return Path.Combine(root, "Clash Verge");
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Clash Verge Rev");
            yield return Path.Combine(localAppData, "Programs", "Clash Verge");
            yield return Path.Combine(localAppData, "clash-verge-rev");
            yield return Path.Combine(localAppData, "clash-verge");
        }
    }
}

internal sealed record TransparentProxyNetworkGuardSnapshot(
    bool IsAdministrator,
    string? MihomoPath,
    IReadOnlyList<string> Diagnostics);

internal sealed record TransparentProxyNetworkGuardValidationResult(
    bool IsSafe,
    IReadOnlyList<string> Issues);
