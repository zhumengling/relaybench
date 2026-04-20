using System.Net.Http;
using System.Text;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed class ClientApiDiagnosticsService
{
    private static readonly HttpMethod GetMethod = HttpMethod.Get;
    private static readonly HttpMethod PostMethod = HttpMethod.Post;

    private readonly IClientApiDiagnosticEnvironment _environment;
    private readonly IClientApiProbeTransport _transport;

    public ClientApiDiagnosticsService(
        IClientApiDiagnosticEnvironment? environment = null,
        IClientApiProbeTransport? transport = null)
    {
        _environment = environment ?? new ClientApiDiagnosticEnvironment();
        _transport = transport ?? new ClientApiHttpProbeTransport();
    }

    public async Task<ClientApiDiagnosticsResult> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var definitions = BuildDefinitions();
        List<ClientApiCheck> checks = new(definitions.Count);

        foreach (var definition in definitions.Select((value, index) => new { value, index }))
        {
            progress?.Report($"正在检查客户端 API {definition.index + 1}/{definitions.Count}：{definition.value.Name}");
            checks.Add(await ProbeAsync(definition.value, cancellationToken));
        }

        var installedCount = checks.Count(check => check.Installed);
        var configuredCount = checks.Count(check => check.ConfigDetected);
        var reachableCount = checks.Count(check => check.Reachable);
        var readyCount = checks.Count(check => check.Installed && check.Reachable);

        var summary =
            $"共检查 {checks.Count} 个客户端入口；" +
            $"已发现安装 {installedCount}/{checks.Count}；" +
            $"发现配置 {configuredCount}/{checks.Count}；" +
            $"底层 API 可达 {reachableCount}/{checks.Count}；" +
            $"本机已安装且链路可达 {readyCount}/{checks.Count}。";
        var error = reachableCount == 0
            ? "所有客户端对应的 API 入口都不可达。"
            : null;

        return new ClientApiDiagnosticsResult(
            DateTimeOffset.Now,
            checks,
            installedCount,
            configuredCount,
            reachableCount,
            summary,
            error);
    }

    private async Task<ClientApiCheck> ProbeAsync(ClientDefinition definition, CancellationToken cancellationToken)
    {
        var localState = definition.LocalStateFactory(_environment);
        var probe = await _transport.ProbeAsync(definition.ProbeUrl, definition.ProbeMethod, definition.Provider, cancellationToken);
        var reachable = probe.StatusCode is not null;
        var verdict = BuildCheckVerdict(localState, probe);
        var summary = BuildSummary(definition, localState, probe, verdict);

        return new ClientApiCheck(
            definition.Name,
            definition.Provider,
            definition.Kind,
            definition.ProbeUrl.ToString(),
            definition.ProbeMethod.Method,
            localState.Installed,
            localState.ConfigDetected,
            localState.InstallEvidence,
            localState.ConfigSource,
            localState.ProxySource,
            localState.AccessPathLabel,
            localState.ConfigOriginLabel,
            localState.EndpointLabel,
            localState.RoutingNote,
            localState.RestoreSupported,
            localState.RestoreHint,
            reachable,
            probe.StatusCode,
            probe.Latency,
            verdict,
            summary,
            probe.Evidence,
            probe.Error);
    }

    private static string BuildCheckVerdict(LocalClientState localState, ClientApiProbeResponse probe)
    {
        if (probe.StatusCode is null)
        {
            return $"{localState.AccessPathLabel}，但底层 API 不可达";
        }

        return $"{localState.AccessPathLabel}，底层 API 可达";
    }

    private static string BuildSummary(
        ClientDefinition definition,
        LocalClientState localState,
        ClientApiProbeResponse probe,
        string verdict)
    {
        StringBuilder builder = new();
        builder.Append($"{definition.Name}：{verdict}。");
        builder.Append($" 当前入口：{localState.EndpointLabel}。");
        builder.Append($" 鉴定结果：{localState.AccessPathLabel} / {localState.ConfigOriginLabel}。");
        builder.Append($" 备注：{localState.RoutingNote}。");
        builder.Append(localState.Installed
            ? $" 安装线索：{localState.InstallEvidence}。"
            : " 未发现本机安装线索。");
        builder.Append(localState.ConfigDetected
            ? $" 配置线索：{localState.ConfigSource}。"
            : " 未发现可识别的客户端配置。");
        builder.Append($" 代理来源：{localState.ProxySource}。");

        if (probe.StatusCode is not null)
        {
            builder.Append($" API 探测：HTTP {probe.StatusCode}，{probe.Verdict}");
            if (probe.Latency is not null)
            {
                builder.Append($"，耗时 {probe.Latency.Value.TotalMilliseconds:F0} ms");
            }

            builder.Append('。');
        }
        else
        {
            builder.Append($" API 探测失败：{probe.Error ?? "未知错误"}。");
        }

        return builder.ToString();
    }

    private IReadOnlyList<ClientDefinition> BuildDefinitions()
    {
        var userProfile = _environment.UserProfilePath;
        var roamingAppData = _environment.RoamingAppDataPath;
        var localAppData = _environment.LocalAppDataPath;

        return
        [
            new(
                "Codex CLI",
                "OpenAI",
                "CLI",
                new Uri("https://api.openai.com/v1/models"),
                GetMethod,
                environment => DetectCodexCli(environment, userProfile)),
            new(
                "Codex Desktop",
                "OpenAI",
                "桌面端",
                new Uri("https://api.openai.com/v1/models"),
                GetMethod,
                environment => DetectCodexDesktop(environment, userProfile, localAppData)),
            new(
                "VSCode Codex",
                "OpenAI",
                "编辑器扩展",
                new Uri("https://api.openai.com/v1/models"),
                GetMethod,
                environment => DetectVsCodeCodex(environment, roamingAppData, localAppData, userProfile)),
            new(
                "Antigravity",
                "Google",
                "桌面端",
                new Uri("https://generativelanguage.googleapis.com/v1beta/models"),
                GetMethod,
                environment => DetectAntigravity(environment, userProfile, roamingAppData, localAppData)),
            new(
                "Claude CLI",
                "Anthropic",
                "CLI",
                new Uri("https://api.anthropic.com/v1/messages"),
                PostMethod,
                environment => DetectClaudeCli(environment, userProfile))
        ];
    }

    private static LocalClientState DetectCodexCli(IClientApiDiagnosticEnvironment environment, string userProfile)
    {
        var commandPaths = environment.ResolveCommandPaths("codex");
        var codexRoot = Path.Combine(userProfile, ".codex");
        var sessionsDir = Path.Combine(codexRoot, "sessions");
        var configFiles = ReadExistingFiles(
            environment,
            Path.Combine(codexRoot, "config.toml"),
            Path.Combine(codexRoot, "auth.json"),
            Path.Combine(codexRoot, "settings.json"));
        var envVars = ReadExistingEnvironmentVariables(environment, "OPENAI_API_KEY", "OPENAI_BASE_URL");
        var installEvidenceParts = new List<string>();
        if (commandPaths.Count > 0)
        {
            installEvidenceParts.Add($"命中命令：{string.Join("；", commandPaths.Take(2))}");
        }

        if (environment.DirectoryExists(codexRoot))
        {
            installEvidenceParts.Add($"命中目录：{codexRoot}");
        }

        if (environment.DirectoryExists(sessionsDir))
        {
            installEvidenceParts.Add($"发现会话目录：{sessionsDir}");
        }

        var profile = ResolveAccessProfile(
            "https://api.openai.com/v1",
            configFiles,
            envVars,
            hasOfficialOAuth: configFiles.Any(file => file.Path.EndsWith("auth.json", StringComparison.OrdinalIgnoreCase) &&
                                                     !file.Content.Contains("PROXY_MANAGED", StringComparison.OrdinalIgnoreCase)),
            restoreHint: "支持还原 .codex/config.toml 与 .codex/settings.json 中的代理接管或自定义入口。");

        return BuildState(
            installed: commandPaths.Count > 0 || environment.DirectoryExists(codexRoot),
            configDetected: configFiles.Count > 0 || envVars.Count > 0,
            installEvidence: installEvidenceParts.Count > 0
                ? string.Join("；", installEvidenceParts)
                : "未在 PATH 或用户目录中发现 Codex CLI 线索",
            configSource: BuildConfigSource(configFiles, envVars, ".codex"),
            proxySource: BuildProxySource(environment),
            profile: profile);
    }

    private static LocalClientState DetectCodexDesktop(IClientApiDiagnosticEnvironment environment, string userProfile, string localAppData)
    {
        List<string> appCandidates = [];
        foreach (var candidate in new[]
                 {
                     Path.Combine(localAppData, "Programs", "Codex", "Codex.exe"),
                     Path.Combine(localAppData, "Programs", "OpenAI Codex", "Codex.exe"),
                     Path.Combine(localAppData, "Programs", "OpenAI Codex", "OpenAI Codex.exe")
                 })
        {
            if (environment.FileExists(candidate))
            {
                appCandidates.Add(candidate);
            }
        }

        var processHits = environment.GetRunningProcessNames()
            .Where(name => name.Contains("codex", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var configFiles = ReadExistingFiles(
            environment,
            Path.Combine(userProfile, ".codex", "config.toml"),
            Path.Combine(userProfile, ".codex", "auth.json"),
            Path.Combine(userProfile, ".codex", "settings.json"));
        var envVars = ReadExistingEnvironmentVariables(environment, "OPENAI_API_KEY", "OPENAI_BASE_URL");

        var installEvidence = appCandidates.Count > 0
            ? $"命中程序：{string.Join("；", appCandidates.Take(2))}"
            : processHits.Length > 0
                ? $"发现运行进程：{string.Join("、", processHits)}"
                : "未发现 Codex 桌面端程序或进程";

        var profile = ResolveAccessProfile(
            "https://api.openai.com/v1",
            configFiles,
            envVars,
            hasOfficialOAuth: configFiles.Any(file => file.Path.EndsWith("auth.json", StringComparison.OrdinalIgnoreCase) &&
                                                     !file.Content.Contains("PROXY_MANAGED", StringComparison.OrdinalIgnoreCase)),
            restoreHint: "支持还原 .codex/config.toml 与 .codex/settings.json 中的代理接管或自定义入口。");

        return BuildState(
            installed: appCandidates.Count > 0 || processHits.Length > 0,
            configDetected: configFiles.Count > 0 || envVars.Count > 0,
            installEvidence: installEvidence,
            configSource: BuildConfigSource(configFiles, envVars, ".codex"),
            proxySource: BuildProxySource(environment),
            profile: profile);
    }

    private static LocalClientState DetectVsCodeCodex(
        IClientApiDiagnosticEnvironment environment,
        string roamingAppData,
        string localAppData,
        string userProfile)
    {
        var vscodeCommands = environment.ResolveCommandPaths("code");
        List<string> vscodeApps = [];
        foreach (var candidate in new[]
                 {
                     Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
                     Path.Combine(localAppData, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe")
                 })
        {
            if (environment.FileExists(candidate))
            {
                vscodeApps.Add(candidate);
            }
        }

        List<string> extensionHits = [];
        foreach (var extensionRoot in new[]
                 {
                     Path.Combine(userProfile, ".vscode", "extensions"),
                     Path.Combine(userProfile, ".vscode-insiders", "extensions")
                 })
        {
            if (!environment.DirectoryExists(extensionRoot))
            {
                continue;
            }

            extensionHits.AddRange(environment.EnumerateDirectories(extensionRoot)
                .Where(path => Path.GetFileName(path).Contains("codex", StringComparison.OrdinalIgnoreCase))
                .Take(4));
        }

        List<ConfigFileSnapshot> configFiles = [];
        foreach (var settingsPath in new[]
                 {
                     Path.Combine(roamingAppData, "Code", "User", "settings.json"),
                     Path.Combine(roamingAppData, "Code - Insiders", "User", "settings.json")
                 })
        {
            var content = environment.ReadFileText(settingsPath);
            if (!string.IsNullOrWhiteSpace(content) &&
                (content.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
                 content.Contains("openai", StringComparison.OrdinalIgnoreCase)))
            {
                configFiles.Add(new ConfigFileSnapshot(settingsPath, content));
            }
        }

        var envVars = ReadExistingEnvironmentVariables(environment, "OPENAI_API_KEY", "OPENAI_BASE_URL");
        var installEvidence = new List<string>();
        if (vscodeCommands.Count > 0)
        {
            installEvidence.Add($"命中 code 命令：{string.Join("；", vscodeCommands.Take(2))}");
        }

        if (vscodeApps.Count > 0)
        {
            installEvidence.Add($"命中编辑器程序：{string.Join("；", vscodeApps.Take(2))}");
        }

        if (extensionHits.Count > 0)
        {
            installEvidence.Add($"命中扩展目录：{string.Join("；", extensionHits.Select(Path.GetFileName).Take(2))}");
        }

        var profile = ResolveAccessProfile(
            "https://api.openai.com/v1",
            configFiles,
            envVars,
            hasOfficialOAuth: false,
            restoreHint: "支持还原 VS Code User/settings.json 中的代理接管或自定义入口键。");

        return BuildState(
            installed: extensionHits.Count > 0 || vscodeCommands.Count > 0 || vscodeApps.Count > 0,
            configDetected: configFiles.Count > 0 || envVars.Count > 0,
            installEvidence: installEvidence.Count > 0
                ? string.Join("；", installEvidence)
                : "未发现 VSCode Codex 扩展或编辑器入口",
            configSource: BuildConfigSource(configFiles, envVars, "Code/User/settings.json"),
            proxySource: BuildProxySource(environment),
            profile: profile);
    }

    private static LocalClientState DetectClaudeCli(IClientApiDiagnosticEnvironment environment, string userProfile)
    {
        var commandPaths = environment.ResolveCommandPaths("claude");
        var claudeRoot = Path.Combine(userProfile, ".claude");
        var projectsDir = Path.Combine(claudeRoot, "projects");
        var configFiles = ReadExistingFiles(
            environment,
            Path.Combine(claudeRoot, "config.json"),
            Path.Combine(claudeRoot, "settings.json"),
            Path.Combine(claudeRoot, "claude.json"),
            Path.Combine(claudeRoot, "credentials.json"),
            Path.Combine(claudeRoot, ".credentials.json"),
            Path.Combine(userProfile, ".claude.json"));
        var envVars = ReadExistingEnvironmentVariables(
            environment,
            "ANTHROPIC_API_KEY",
            "ANTHROPIC_AUTH_TOKEN",
            "ANTHROPIC_BASE_URL");
        var installEvidenceParts = new List<string>();
        if (commandPaths.Count > 0)
        {
            installEvidenceParts.Add($"命中命令：{string.Join("；", commandPaths.Take(2))}");
        }

        if (environment.DirectoryExists(claudeRoot))
        {
            installEvidenceParts.Add($"命中目录：{claudeRoot}");
        }

        if (environment.DirectoryExists(projectsDir))
        {
            installEvidenceParts.Add($"发现项目会话目录：{projectsDir}");
        }

        var hasOfficialOAuth = envVars.Any(entry => entry.Name.Equals("ANTHROPIC_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase)) ||
                               configFiles.Any(file => file.Content.Contains("ANTHROPIC_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase));
        var profile = ResolveAccessProfile(
            "https://api.anthropic.com",
            configFiles,
            envVars,
            hasOfficialOAuth: hasOfficialOAuth,
            restoreHint: "支持还原 .claude/settings.json、.claude/claude.json 中的代理接管或自定义入口。");

        return BuildState(
            installed: commandPaths.Count > 0 || environment.DirectoryExists(claudeRoot),
            configDetected: configFiles.Count > 0 || envVars.Count > 0,
            installEvidence: installEvidenceParts.Count > 0
                ? string.Join("；", installEvidenceParts)
                : "未在 PATH 或用户目录中发现 Claude CLI 线索",
            configSource: BuildConfigSource(configFiles, envVars, ".claude"),
            proxySource: BuildProxySource(environment),
            profile: profile);
    }

    private static LocalClientState DetectAntigravity(
        IClientApiDiagnosticEnvironment environment,
        string userProfile,
        string roamingAppData,
        string localAppData)
    {
        var antigravityRoamingRoot = Path.Combine(roamingAppData, "Antigravity");
        var antigravityUserDir = Path.Combine(antigravityRoamingRoot, "User");
        var antigravitySettingsPath = Path.Combine(antigravityUserDir, "settings.json");
        var antigravityLogsDir = Path.Combine(antigravityRoamingRoot, "logs");
        var geminiRoot = Path.Combine(userProfile, ".gemini");
        var antigravityGeminiDir = Path.Combine(geminiRoot, "antigravity");
        var installationIdPath = Path.Combine(antigravityGeminiDir, "installation_id");
        var mcpConfigPath = Path.Combine(antigravityGeminiDir, "mcp_config.json");
        var conversationsDir = Path.Combine(antigravityGeminiDir, "conversations");
        var tempProfileDir = Path.Combine(localAppData, "Temp", "antigravity-stable-user-x64");
        var processHits = environment.GetRunningProcessNames()
            .Where(name => name.Contains("antigravity", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var configFiles = ReadExistingFiles(
            environment,
            antigravitySettingsPath,
            installationIdPath,
            mcpConfigPath,
            Path.Combine(geminiRoot, ".env"));
        var envVars = ReadExistingEnvironmentVariables(
            environment,
            "GEMINI_API_KEY",
            "GOOGLE_API_KEY",
            "GOOGLE_GEMINI_BASE_URL",
            "GOOGLE_API_BASE_URL");
        List<string> installEvidenceParts = [];

        if (environment.DirectoryExists(antigravityRoamingRoot))
        {
            installEvidenceParts.Add($"命中数据目录：{antigravityRoamingRoot}");
        }

        if (environment.DirectoryExists(antigravityGeminiDir))
        {
            installEvidenceParts.Add($"命中工作目录：{antigravityGeminiDir}");
        }

        if (environment.DirectoryExists(tempProfileDir))
        {
            installEvidenceParts.Add($"命中临时配置目录：{tempProfileDir}");
        }

        if (processHits.Length > 0)
        {
            installEvidenceParts.Add($"发现运行进程：{string.Join("、", processHits)}");
        }

        List<string> configSources = [];
        if (configFiles.Count > 0)
        {
            configSources.Add($"命中文件：{string.Join("；", configFiles.Select(file => file.Path).Take(3))}");
        }

        if (environment.DirectoryExists(conversationsDir))
        {
            configSources.Add($"发现会话目录：{conversationsDir}");
        }

        if (environment.DirectoryExists(antigravityLogsDir))
        {
            configSources.Add($"发现日志目录：{antigravityLogsDir}");
        }

        if (envVars.Count > 0)
        {
            configSources.Add($"命中环境变量：{string.Join("、", envVars.Select(entry => entry.Name))}");
        }

        var profile = ResolveAccessProfile(
            "https://generativelanguage.googleapis.com",
            configFiles,
            envVars,
            hasOfficialOAuth: false,
            restoreHint: "支持还原 Antigravity / .gemini 配置中的代理接管或自定义 Google API 入口。");

        return BuildState(
            installed: environment.DirectoryExists(antigravityRoamingRoot) ||
                       environment.DirectoryExists(antigravityGeminiDir) ||
                       environment.DirectoryExists(tempProfileDir) ||
                       processHits.Length > 0,
            configDetected: configSources.Count > 0,
            installEvidence: installEvidenceParts.Count > 0
                ? string.Join("；", installEvidenceParts)
                : "未发现 Antigravity 本机安装线索",
            configSource: configSources.Count > 0
                ? string.Join("；", configSources)
                : "未在 .gemini/antigravity、Roaming/Antigravity 或环境变量中发现配置",
            proxySource: BuildProxySource(environment),
            profile: profile);
    }

    private static LocalClientState BuildState(
        bool installed,
        bool configDetected,
        string installEvidence,
        string configSource,
        string proxySource,
        AccessProfile profile)
        => new(
            installed,
            configDetected,
            installEvidence,
            configSource,
            proxySource,
            profile.AccessPathLabel,
            profile.ConfigOriginLabel,
            profile.EndpointLabel,
            profile.RoutingNote,
            profile.RestoreSupported,
            profile.RestoreHint);

    private static List<ConfigFileSnapshot> ReadExistingFiles(IClientApiDiagnosticEnvironment environment, params string[] paths)
        => paths
            .Where(environment.FileExists)
            .Select(path => new ConfigFileSnapshot(path, environment.ReadFileText(path) ?? string.Empty))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<EnvironmentVariableValue> ReadExistingEnvironmentVariables(IClientApiDiagnosticEnvironment environment, params string[] variableNames)
        => variableNames
            .Select(name => new EnvironmentVariableValue(name, environment.GetEnvironmentVariable(name)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .DistinctBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildConfigSource(IReadOnlyList<ConfigFileSnapshot> configFiles, IReadOnlyList<EnvironmentVariableValue> envVars, string fallback)
    {
        List<string> parts = [];
        if (configFiles.Count > 0)
        {
            parts.Add($"命中文件：{string.Join("；", configFiles.Select(file => file.Path).Take(3))}");
        }

        if (envVars.Count > 0)
        {
            parts.Add($"命中环境变量：{string.Join("、", envVars.Select(entry => entry.Name))}");
        }

        return parts.Count > 0
            ? string.Join("；", parts)
            : $"未在 {fallback} 或环境变量中发现配置";
    }

    private static string BuildProxySource(IClientApiDiagnosticEnvironment environment)
    {
        var proxyVars = ReadExistingEnvironmentVariables(
            environment,
            "HTTPS_PROXY",
            "HTTP_PROXY",
            "ALL_PROXY",
            "NO_PROXY",
            "https_proxy",
            "http_proxy",
            "all_proxy",
            "no_proxy");

        return proxyVars.Count > 0
            ? $"环境变量：{string.Join("、", proxyVars.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase))}"
            : "未发现显式代理环境变量";
    }

    private static AccessProfile ResolveAccessProfile(
        string officialEndpoint,
        IReadOnlyList<ConfigFileSnapshot> configFiles,
        IReadOnlyList<EnvironmentVariableValue> envVars,
        bool hasOfficialOAuth,
        string restoreHint)
    {
        var endpointCandidates = configFiles
            .SelectMany(file => ClientApiConfigPatterns.ExtractUrlCandidates(file.Content)
                .Select(url => new EndpointCandidate(url, $"file:{file.Path}")))
            .Concat(envVars
                .SelectMany(entry => ClientApiConfigPatterns.ExtractUrlCandidates(entry.Value)
                    .Select(url => new EndpointCandidate(url, $"env:{entry.Name}"))))
            .DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var localEndpoint = endpointCandidates.FirstOrDefault(candidate => ClientApiConfigPatterns.IsLocalEndpoint(candidate.Url));
        var officialEndpointCandidate = endpointCandidates.FirstOrDefault(candidate => ClientApiConfigPatterns.IsOfficialEndpoint(candidate.Url, officialEndpoint));
        var thirdPartyEndpoint = endpointCandidates.FirstOrDefault(candidate =>
            !ClientApiConfigPatterns.IsLocalEndpoint(candidate.Url) &&
            !ClientApiConfigPatterns.IsOfficialEndpoint(candidate.Url, officialEndpoint));

        var accessPathLabel = localEndpoint is not null
            ? "本地代理接管"
            : thirdPartyEndpoint is not null
                ? "直连第三方"
                : "直连官方";
        var configOriginLabel = hasOfficialOAuth
            ? "官方 OAuth"
            : envVars.Count > 0
                ? "环境变量注入"
                : configFiles.Count > 0
                    ? "配置文件注入"
                    : "无显式鉴权";
        var endpointLabel = localEndpoint?.Url
                            ?? thirdPartyEndpoint?.Url
                            ?? officialEndpointCandidate?.Url
                            ?? officialEndpoint;
        var hasCcSwitchMarker = configFiles.Any(file => file.Content.Contains("PROXY_MANAGED", StringComparison.OrdinalIgnoreCase) ||
                                                        file.Content.Contains("cc-switch", StringComparison.OrdinalIgnoreCase)) ||
                               envVars.Any(entry => entry.Value?.Contains("PROXY_MANAGED", StringComparison.OrdinalIgnoreCase) == true);
        var localPort = ClientApiConfigPatterns.TryGetPort(localEndpoint?.Url);
        var routingNote = localEndpoint is not null
            ? localPort == 15721 || hasCcSwitchMarker
                ? "疑似 cc-switch 接管（本地 15721）"
                : localPort is not null
                    ? $"本地代理接管（自定义端口 {localPort.Value}）"
                    : "本地代理接管（未识别端口）"
            : thirdPartyEndpoint is not null
                ? "入口已改写到第三方端点"
                : "使用官方默认端点";

        return new AccessProfile(
            accessPathLabel,
            configOriginLabel,
            endpointLabel,
            routingNote,
            RestoreSupported: true,
            restoreHint);
    }

    private sealed record ClientDefinition(
        string Name,
        string Provider,
        string Kind,
        Uri ProbeUrl,
        HttpMethod ProbeMethod,
        Func<IClientApiDiagnosticEnvironment, LocalClientState> LocalStateFactory);

    private sealed record LocalClientState(
        bool Installed,
        bool ConfigDetected,
        string InstallEvidence,
        string ConfigSource,
        string ProxySource,
        string AccessPathLabel,
        string ConfigOriginLabel,
        string EndpointLabel,
        string RoutingNote,
        bool RestoreSupported,
        string RestoreHint);

    private sealed record ConfigFileSnapshot(string Path, string Content);

    private sealed record EnvironmentVariableValue(string Name, string? Value);

    private sealed record EndpointCandidate(string Url, string Source);

    private sealed record AccessProfile(
        string AccessPathLabel,
        string ConfigOriginLabel,
        string EndpointLabel,
        string RoutingNote,
        bool RestoreSupported,
        string RestoreHint);
}
