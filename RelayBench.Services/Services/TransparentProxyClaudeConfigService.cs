using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RelayBench.Services;

internal sealed class TransparentProxyClaudeConfigService
{
    private const string BaseUrlKey = "ANTHROPIC_BASE_URL";
    private const string AuthTokenKey = "ANTHROPIC_AUTH_TOKEN";
    private const string ModelKey = "ANTHROPIC_MODEL";
    private const string DefaultOpusModelKey = "ANTHROPIC_DEFAULT_OPUS_MODEL";
    private const string DefaultSonnetModelKey = "ANTHROPIC_DEFAULT_SONNET_MODEL";
    private const string DefaultHaikuModelKey = "ANTHROPIC_DEFAULT_HAIKU_MODEL";
    private const string SubagentModelKey = "CLAUDE_CODE_SUBAGENT_MODEL";
    private const string DefaultClaudeModel = "claude-sonnet-4-5";
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

    private readonly string _userProfilePath;

    public TransparentProxyClaudeConfigService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    public TransparentProxyClaudeConfigService(string userProfilePath)
    {
        _userProfilePath = string.IsNullOrWhiteSpace(userProfilePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(userProfilePath);
    }

    public TransparentProxyClaudeConfigPreview Preview(string baseUrl, string? model = null)
    {
        var settingsPath = ResolveSettingsPath();
        var onboardingPath = ResolveOnboardingPath();
        var existing = File.Exists(settingsPath)
            ? File.ReadAllText(settingsPath)
            : string.Empty;
        var onboardingExisting = File.Exists(onboardingPath)
            ? File.ReadAllText(onboardingPath)
            : string.Empty;
        var updated = BuildUpdatedSettings(existing, baseUrl, model);
        var onboardingUpdated = BuildUpdatedOnboarding(onboardingExisting);
        var changed = !string.Equals(NormalizeJson(existing), updated, StringComparison.Ordinal) ||
                      !string.Equals(NormalizeJson(onboardingExisting), onboardingUpdated, StringComparison.Ordinal);
        return new TransparentProxyClaudeConfigPreview(
            settingsPath,
            changed,
            BuildPreviewText(settingsPath, onboardingPath, baseUrl, model, changed));
    }

    public TransparentProxyClaudeConfigMutationResult Apply(string baseUrl, string? model = null)
    {
        var settingsPath = ResolveSettingsPath();
        var onboardingPath = ResolveOnboardingPath();
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existing = File.Exists(settingsPath)
            ? File.ReadAllText(settingsPath)
            : string.Empty;
        var onboardingExisting = File.Exists(onboardingPath)
            ? File.ReadAllText(onboardingPath)
            : string.Empty;
        var normalizedExisting = NormalizeJson(existing);
        var updated = BuildUpdatedSettings(existing, baseUrl, model);
        var settingsChanged = !string.Equals(normalizedExisting, updated, StringComparison.Ordinal);
        var onboardingUpdated = BuildUpdatedOnboarding(onboardingExisting);
        var onboardingChanged = !string.Equals(NormalizeJson(onboardingExisting), onboardingUpdated, StringComparison.Ordinal);
        if (!settingsChanged && !onboardingChanged)
        {
            return new TransparentProxyClaudeConfigMutationResult(
                true,
                "Claude CLI 已经指向 RelayBench 本地统一出口，无需改动。",
                settingsPath,
                null);
        }

        string? backupPath = null;
        if (settingsChanged && !string.IsNullOrWhiteSpace(existing))
        {
            backupPath = $"{settingsPath}{BackupMarker}{DateTime.Now:yyyyMMddHHmmss}";
            File.WriteAllText(backupPath, existing);
            PruneBackups(settingsPath);
        }

        if (settingsChanged)
        {
            File.WriteAllText(settingsPath, updated);
        }

        if (onboardingChanged)
        {
            var onboardingDirectory = Path.GetDirectoryName(onboardingPath);
            if (!string.IsNullOrWhiteSpace(onboardingDirectory))
            {
                Directory.CreateDirectory(onboardingDirectory);
            }

            if (!string.IsNullOrWhiteSpace(onboardingExisting))
            {
                var onboardingBackupPath = $"{onboardingPath}{BackupMarker}{DateTime.Now:yyyyMMddHHmmss}";
                File.WriteAllText(onboardingBackupPath, onboardingExisting);
                PruneBackups(onboardingPath);
                backupPath = backupPath is null ? onboardingBackupPath : $"{backupPath};{onboardingBackupPath}";
            }

            File.WriteAllText(onboardingPath, onboardingUpdated);
        }

        return new TransparentProxyClaudeConfigMutationResult(
            true,
            "已写入 Claude CLI 接管配置，后续 Claude CLI 会通过 RelayBench 本地统一出口进入共享路由队列。",
            settingsPath,
            backupPath);
    }

    public TransparentProxyClaudeConfigMutationResult RestoreLatestBackup()
    {
        var settingsPath = ResolveSettingsPath();
        var backupPath = FindLatestBackup(settingsPath);
        if (backupPath is null)
        {
            return new TransparentProxyClaudeConfigMutationResult(
                false,
                "没有找到 Claude CLI 的 RelayBench 接管备份，无法自动恢复。",
                settingsPath,
                null);
        }

        var backup = File.ReadAllText(backupPath);
        var current = File.Exists(settingsPath)
            ? File.ReadAllText(settingsPath)
            : string.Empty;
        var restored = BuildRestoredSettings(current, backup);
        if (string.Equals(NormalizeJson(current), restored, StringComparison.Ordinal))
        {
            return new TransparentProxyClaudeConfigMutationResult(
                true,
                "Claude CLI 当前 settings.json 已经没有需要回滚的 RelayBench 接管变量，已保持用户现有配置不变。",
                settingsPath,
                backupPath);
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
        return new TransparentProxyClaudeConfigMutationResult(
            true,
            "已合并恢复 Claude CLI 配置：仅回滚 RelayBench env 变量，保留用户后续新增设置。",
            settingsPath,
            backupPath);
    }

    private string ResolveSettingsPath()
        => Path.Combine(_userProfilePath, ".claude", "settings.json");

    private string ResolveOnboardingPath()
        => Path.Combine(_userProfilePath, ".claude.json");

    private static string BuildUpdatedSettings(string existing, string baseUrl, string? model)
    {
        var root = LoadRootObject(existing);
        var env = root["env"] as JsonObject;
        if (env is null)
        {
            env = new JsonObject();
            root["env"] = env;
        }

        env[BaseUrlKey] = NormalizeAnthropicBaseUrl(baseUrl);
        env[AuthTokenKey] = LocalBearerToken;
        env[ModelKey] = ResolveModel(model);
        env[DefaultOpusModelKey] = ResolveModel(model);
        env[DefaultSonnetModelKey] = ResolveModel(model);
        env[DefaultHaikuModelKey] = ResolveModel(model);
        env[SubagentModelKey] = ResolveModel(model);
        env.Remove("CLAUDE_CODE_EFFORT_LEVEL");
        return root.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    private static string BuildUpdatedOnboarding(string existing)
    {
        var root = LoadRootObject(existing);
        root["hasCompletedOnboarding"] = true;
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
        var env = root["env"] as JsonObject;
        var backupEnv = backupRoot["env"] as JsonObject;
        if (env is null)
        {
            env = new JsonObject();
            root["env"] = env;
        }

        RestoreJsonValue(env, backupEnv, BaseUrlKey);
        RestoreJsonValue(env, backupEnv, AuthTokenKey);
        RestoreJsonValue(env, backupEnv, ModelKey);
        RestoreJsonValue(env, backupEnv, DefaultOpusModelKey);
        RestoreJsonValue(env, backupEnv, DefaultSonnetModelKey);
        RestoreJsonValue(env, backupEnv, DefaultHaikuModelKey);
        RestoreJsonValue(env, backupEnv, SubagentModelKey);
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
            ?? throw new InvalidOperationException("Claude settings.json 顶层必须是 JSON object。");
    }

    private static string NormalizeJson(string existing)
        => string.IsNullOrWhiteSpace(existing)
            ? string.Empty
            : LoadRootObject(existing).ToJsonString(WriteOptions) + Environment.NewLine;

    private static string BuildPreviewText(string settingsPath, string onboardingPath, string baseUrl, string? model, bool changed)
        => string.Join(
            Environment.NewLine,
            [
                $"目标文件：{settingsPath}",
                $"辅助文件：{onboardingPath}",
                changed ? "动作：写入或更新 env 中的 RelayBench 接管变量，并在改动前备份原文件。" : "动作：无需改动，当前配置已经一致。",
                "写入内容：",
                "\"env\": {",
                $"  \"{BaseUrlKey}\": \"{NormalizeAnthropicBaseUrl(baseUrl)}\",",
                $"  \"{AuthTokenKey}\": \"{LocalBearerToken}\",",
                $"  \"{ModelKey}\": \"{ResolveModel(model)}\",",
                $"  \"{DefaultOpusModelKey}\": \"{ResolveModel(model)}\",",
                $"  \"{DefaultSonnetModelKey}\": \"{ResolveModel(model)}\",",
                $"  \"{DefaultHaikuModelKey}\": \"{ResolveModel(model)}\",",
                $"  \"{SubagentModelKey}\": \"{ResolveModel(model)}\"",
                "}",
                "\"hasCompletedOnboarding\": true"
            ]);

    private static string ResolveModel(string? model)
        => string.IsNullOrWhiteSpace(model)
            ? DefaultClaudeModel
            : model.Trim();

    private static string NormalizeAnthropicBaseUrl(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:17880"
            : value.Trim();
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

internal sealed record TransparentProxyClaudeConfigPreview(
    string SettingsPath,
    bool Changed,
    string PreviewText);

internal sealed record TransparentProxyClaudeConfigMutationResult(
    bool Succeeded,
    string Summary,
    string SettingsPath,
    string? BackupPath);
