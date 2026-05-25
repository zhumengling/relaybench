using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RelayBench.Services;

internal sealed class TransparentProxyVsCodeSettingsService
{
    private const string TerminalEnvWindowsKey = "terminal.integrated.env.windows";
    private const string OpenAiBaseUrlKey = "OPENAI_BASE_URL";
    private const string OpenAiApiKeyKey = "OPENAI_API_KEY";
    private const string AnthropicBaseUrlKey = "ANTHROPIC_BASE_URL";
    private const string AnthropicAuthTokenKey = "ANTHROPIC_AUTH_TOKEN";
    private const string RelayBenchBaseUrlKey = "RELAYBENCH_BASE_URL";
    private const string LocalBearerToken = "relaybench-local";
    private const string BackupMarker = ".relaybench-app-capture-backup-";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _roamingAppDataPath;

    public TransparentProxyVsCodeSettingsService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
    {
    }

    public TransparentProxyVsCodeSettingsService(string roamingAppDataPath)
    {
        _roamingAppDataPath = string.IsNullOrWhiteSpace(roamingAppDataPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.GetFullPath(roamingAppDataPath);
    }

    public TransparentProxyVsCodeSettingsPreview Preview(string baseUrl)
        => Preview(baseUrl, TransparentProxyVsCodeSettingsScope.User, null);

    public TransparentProxyVsCodeSettingsPreview Preview(
        string baseUrl,
        TransparentProxyVsCodeSettingsScope scope,
        string? workspaceDirectory)
    {
        var paths = ResolveSettingsPaths(scope, workspaceDirectory);
        var changed = paths.Any(path => HasChanged(path, baseUrl));
        return new TransparentProxyVsCodeSettingsPreview(
            paths,
            changed,
            BuildPreviewText(paths, baseUrl, changed, scope, workspaceDirectory));
    }

    public TransparentProxyVsCodeSettingsMutationResult Apply(string baseUrl)
        => Apply(baseUrl, TransparentProxyVsCodeSettingsScope.User, null);

    public TransparentProxyVsCodeSettingsMutationResult Apply(
        string baseUrl,
        TransparentProxyVsCodeSettingsScope scope,
        string? workspaceDirectory)
    {
        var paths = ResolveSettingsPaths(scope, workspaceDirectory);
        List<string> changedFiles = [];
        List<string> backupFiles = [];
        foreach (var settingsPath in paths)
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var existing = File.Exists(settingsPath)
                ? File.ReadAllText(settingsPath)
                : string.Empty;
            var normalizedExisting = NormalizeJson(existing);
            var updated = BuildUpdatedSettings(existing, baseUrl);
            if (string.Equals(normalizedExisting, updated, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(existing))
            {
                var backupPath = $"{settingsPath}{BackupMarker}{DateTime.Now:yyyyMMddHHmmss}";
                File.WriteAllText(backupPath, existing);
                backupFiles.Add(backupPath);
                PruneBackups(settingsPath);
            }

            File.WriteAllText(settingsPath, updated);
            changedFiles.Add(settingsPath);
        }

        return changedFiles.Count == 0
            ? new TransparentProxyVsCodeSettingsMutationResult(
                true,
                $"VS Code {TransparentProxyVsCodeSettingsScopes.GetDisplayName(scope)}终端环境已经指向 RelayBench 本地统一出口，无需改动。",
                [],
                [])
            : new TransparentProxyVsCodeSettingsMutationResult(
                true,
                $"已写入 VS Code {TransparentProxyVsCodeSettingsScopes.GetDisplayName(scope)}终端环境接管配置：{changedFiles.Count} 个 settings.json。新开的 VS Code 终端会通过 RelayBench 本地统一出口进入共享路由队列。",
                changedFiles,
                backupFiles);
    }

    public TransparentProxyVsCodeSettingsMutationResult RestoreLatestBackups()
        => RestoreLatestBackups(TransparentProxyVsCodeSettingsScope.User, null);

    public TransparentProxyVsCodeSettingsMutationResult RestoreLatestBackups(
        TransparentProxyVsCodeSettingsScope scope,
        string? workspaceDirectory)
    {
        var paths = ResolveSettingsPaths(scope, workspaceDirectory);
        List<string> changedFiles = [];
        List<string> backupFiles = [];
        foreach (var settingsPath in paths)
        {
            var backupPath = FindLatestBackup(settingsPath);
            if (backupPath is null)
            {
                continue;
            }

            var backup = File.ReadAllText(backupPath);
            var current = File.Exists(settingsPath)
                ? File.ReadAllText(settingsPath)
                : string.Empty;
            var restored = BuildRestoredSettings(current, backup);
            if (string.Equals(NormalizeJson(current), restored, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(current, backup, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(current))
            {
                var rollbackBackupPath = $"{settingsPath}{BackupMarker}before-restore-{DateTime.Now:yyyyMMddHHmmss}";
                File.WriteAllText(rollbackBackupPath, current);
            }

            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(settingsPath, restored);
            changedFiles.Add(settingsPath);
            backupFiles.Add(backupPath);
        }

        return changedFiles.Count == 0
            ? new TransparentProxyVsCodeSettingsMutationResult(
                false,
                $"没有找到 VS Code {TransparentProxyVsCodeSettingsScopes.GetDisplayName(scope)}终端环境的 RelayBench 接管备份，无法自动恢复。",
                [],
                [])
            : new TransparentProxyVsCodeSettingsMutationResult(
                true,
                $"已合并恢复 VS Code {TransparentProxyVsCodeSettingsScopes.GetDisplayName(scope)}终端环境配置：{changedFiles.Count} 个 settings.json，仅回滚 RelayBench 管理的终端环境变量。",
                changedFiles,
                backupFiles);
    }

    private IReadOnlyList<string> ResolveSettingsPaths(
        TransparentProxyVsCodeSettingsScope scope,
        string? workspaceDirectory)
    {
        List<string> paths = [];
        if (scope is TransparentProxyVsCodeSettingsScope.User or TransparentProxyVsCodeSettingsScope.UserAndWorkspace)
        {
            paths.AddRange(ResolveUserSettingsPaths());
        }

        if (scope is TransparentProxyVsCodeSettingsScope.Workspace or TransparentProxyVsCodeSettingsScope.UserAndWorkspace)
        {
            paths.Add(ResolveWorkspaceSettingsPath(workspaceDirectory));
        }

        if (paths.Count == 0)
        {
            paths.AddRange(ResolveUserSettingsPaths());
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IReadOnlyList<string> ResolveUserSettingsPaths()
    {
        var stable = Path.Combine(_roamingAppDataPath, "Code", "User", "settings.json");
        var insiders = Path.Combine(_roamingAppDataPath, "Code - Insiders", "User", "settings.json");
        List<string> paths = [stable];
        var insidersDirectory = Path.GetDirectoryName(insiders);
        if (File.Exists(insiders) ||
            (!string.IsNullOrWhiteSpace(insidersDirectory) && Directory.Exists(insidersDirectory)))
        {
            paths.Add(insiders);
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ResolveWorkspaceSettingsPath(string? workspaceDirectory)
    {
        var root = string.IsNullOrWhiteSpace(workspaceDirectory)
            ? Directory.GetCurrentDirectory()
            : workspaceDirectory.Trim();
        return Path.Combine(Path.GetFullPath(root), ".vscode", "settings.json");
    }

    private static bool HasChanged(string settingsPath, string baseUrl)
    {
        var existing = File.Exists(settingsPath)
            ? File.ReadAllText(settingsPath)
            : string.Empty;
        return !string.Equals(NormalizeJson(existing), BuildUpdatedSettings(existing, baseUrl), StringComparison.Ordinal);
    }

    private static string BuildUpdatedSettings(string existing, string baseUrl)
    {
        var root = LoadRootObject(existing);
        var env = root[TerminalEnvWindowsKey] as JsonObject;
        if (env is null)
        {
            env = new JsonObject();
            root[TerminalEnvWindowsKey] = env;
        }

        var openAiBaseUrl = NormalizeOpenAiBaseUrl(baseUrl);
        env[OpenAiBaseUrlKey] = openAiBaseUrl;
        env[OpenAiApiKeyKey] = LocalBearerToken;
        env[AnthropicBaseUrlKey] = NormalizeAnthropicBaseUrl(baseUrl);
        env[AnthropicAuthTokenKey] = LocalBearerToken;
        env[RelayBenchBaseUrlKey] = openAiBaseUrl;
        return root.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    private static string BuildRestoredSettings(string current, string backup)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return NormalizeJson(backup);
        }

        var root = LoadRootObject(current);
        var backupRoot = LoadRootObject(backup);
        var env = root[TerminalEnvWindowsKey] as JsonObject;
        var backupEnv = backupRoot[TerminalEnvWindowsKey] as JsonObject;
        if (env is null)
        {
            env = new JsonObject();
            root[TerminalEnvWindowsKey] = env;
        }

        RestoreJsonValue(env, backupEnv, OpenAiBaseUrlKey);
        RestoreJsonValue(env, backupEnv, OpenAiApiKeyKey);
        RestoreJsonValue(env, backupEnv, AnthropicBaseUrlKey);
        RestoreJsonValue(env, backupEnv, AnthropicAuthTokenKey);
        RestoreJsonValue(env, backupEnv, RelayBenchBaseUrlKey);
        return root.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    private static void RestoreJsonValue(JsonObject target, JsonObject? backup, string key)
    {
        if (backup is not null &&
            backup.TryGetPropertyValue(key, out var backupValue) &&
            backupValue is not null)
        {
            target[key] = JsonNode.Parse(backupValue.ToJsonString());
            return;
        }

        target.Remove(key);
    }

    private static JsonObject LoadRootObject(string existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return [];
        }

        var node = JsonNode.Parse(existing, nodeOptions: null, documentOptions: ReadOptions);
        return node as JsonObject
            ?? throw new InvalidOperationException("VS Code settings.json 顶层必须是 JSON object。");
    }

    private static string NormalizeJson(string existing)
        => string.IsNullOrWhiteSpace(existing)
            ? string.Empty
            : LoadRootObject(existing).ToJsonString(WriteOptions) + Environment.NewLine;

    private static string BuildPreviewText(
        IReadOnlyList<string> settingsPaths,
        string baseUrl,
        bool changed,
        TransparentProxyVsCodeSettingsScope scope,
        string? workspaceDirectory)
        => string.Join(
            Environment.NewLine,
            BuildPreviewLines(settingsPaths, baseUrl, changed, scope, workspaceDirectory));

    private static IEnumerable<string> BuildPreviewLines(
        IReadOnlyList<string> settingsPaths,
        string baseUrl,
        bool changed,
        TransparentProxyVsCodeSettingsScope scope,
        string? workspaceDirectory)
    {
        yield return $"接管范围：{TransparentProxyVsCodeSettingsScopes.GetDisplayName(scope)}";
        if (scope is TransparentProxyVsCodeSettingsScope.Workspace or TransparentProxyVsCodeSettingsScope.UserAndWorkspace)
        {
            yield return $"工作区目录：{Path.GetDirectoryName(Path.GetDirectoryName(ResolveWorkspaceSettingsPath(workspaceDirectory))) ?? "-"}";
        }

        foreach (var line in new[]
                 {
                $"目标文件：{string.Join("；", settingsPaths)}",
                changed ? "动作：写入或更新 VS Code 终端环境变量，并在改动前备份原文件。" : "动作：无需改动，当前配置已经一致。",
                "写入位置：terminal.integrated.env.windows",
                $"OPENAI_BASE_URL={NormalizeOpenAiBaseUrl(baseUrl)}",
                $"ANTHROPIC_BASE_URL={NormalizeAnthropicBaseUrl(baseUrl)}",
                "OPENAI_API_KEY=relaybench-local",
                "ANTHROPIC_AUTH_TOKEN=relaybench-local",
                "说明：VS Codex 若读取 Codex 共享配置，会继续使用 ~/.codex/config.toml；本设置只影响新开的 VS Code 集成终端。"
                 })
        {
            yield return line;
        }
    }

    private static string NormalizeOpenAiBaseUrl(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:17880/v1"
            : value.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return "http://127.0.0.1:17880/v1";
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            path = "/v1";
        }
        else if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path += "/v1";
        }

        builder.Path = path;
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string NormalizeAnthropicBaseUrl(string? value)
    {
        var normalized = NormalizeOpenAiBaseUrl(value);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return "http://127.0.0.1:17880";
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3].TrimEnd('/');
        }

        builder.Path = string.IsNullOrWhiteSpace(path) ? "/" : path;
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string? FindLatestBackup(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        var fileName = Path.GetFileName(settingsPath);
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(fileName) ||
            !Directory.Exists(directory))
        {
            return null;
        }

        return Directory
            .GetFiles(directory, $"{fileName}{BackupMarker}*")
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static void PruneBackups(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        var fileName = Path.GetFileName(settingsPath);
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(fileName) ||
            !Directory.Exists(directory))
        {
            return;
        }

        var backups = Directory
            .GetFiles(directory, $"{fileName}{BackupMarker}*")
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .Skip(8);
        foreach (var backup in backups)
        {
            try
            {
                File.Delete(backup);
            }
            catch
            {
                // best effort
            }
        }
    }
}

internal sealed record TransparentProxyVsCodeSettingsPreview(
    IReadOnlyList<string> SettingsPaths,
    bool Changed,
    string PreviewText);

internal sealed record TransparentProxyVsCodeSettingsMutationResult(
    bool Succeeded,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> BackupFiles);

internal enum TransparentProxyVsCodeSettingsScope
{
    User,
    Workspace,
    UserAndWorkspace
}

internal static class TransparentProxyVsCodeSettingsScopes
{
    public const string UserKey = "user";
    public const string WorkspaceKey = "workspace";
    public const string UserAndWorkspaceKey = "user-workspace";

    public static string NormalizeKey(string? key)
        => Parse(key) switch
        {
            TransparentProxyVsCodeSettingsScope.Workspace => WorkspaceKey,
            TransparentProxyVsCodeSettingsScope.UserAndWorkspace => UserAndWorkspaceKey,
            _ => UserKey
        };

    public static TransparentProxyVsCodeSettingsScope Parse(string? key)
    {
        if (string.Equals(key, WorkspaceKey, StringComparison.OrdinalIgnoreCase))
        {
            return TransparentProxyVsCodeSettingsScope.Workspace;
        }

        if (string.Equals(key, UserAndWorkspaceKey, StringComparison.OrdinalIgnoreCase))
        {
            return TransparentProxyVsCodeSettingsScope.UserAndWorkspace;
        }

        return TransparentProxyVsCodeSettingsScope.User;
    }

    public static string GetDisplayName(TransparentProxyVsCodeSettingsScope scope)
        => scope switch
        {
            TransparentProxyVsCodeSettingsScope.Workspace => "当前工作区级",
            TransparentProxyVsCodeSettingsScope.UserAndWorkspace => "用户级 + 当前工作区级",
            _ => "用户级"
        };
}
