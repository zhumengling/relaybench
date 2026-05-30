using System.Text.Json.Nodes;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class AnthropicClientConfigApplyAdapter
{
    private const string AnthropicOfficialBaseUrl = "https://api.anthropic.com";
    private const string ClaudeCodeEffortLevel = "max";

    private readonly IClientApiConfigMutationEnvironment _environment;

    public AnthropicClientConfigApplyAdapter(IClientApiConfigMutationEnvironment? environment = null)
    {
        _environment = environment ?? new ClientApiDiagnosticEnvironment();
    }

    public Task<ClientAppApplyResult> ApplyAsync(
        ClientApplyEndpoint endpoint,
        IReadOnlyList<ClientApplyTargetSelection> targetSelections,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selected = targetSelections
            .Where(target => target.Protocol == ClientApplyProtocolKind.Anthropic &&
                             string.Equals(target.TargetId, "claude-cli", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length == 0)
        {
            return Task.FromResult(new ClientAppApplyResult(
                true,
                "未选择 Anthropic 客户端，跳过写入。",
                [],
                [],
                [],
                null)
            {
                TargetResults = []
            });
        }

        var normalizedBaseUrl = NormalizeAnthropicBaseUrl(endpoint.BaseUrl);
        var normalizedApiKey = endpoint.ApiKey?.Trim() ?? string.Empty;
        var normalizedModel = endpoint.Model?.Trim() ?? string.Empty;
        var isRelayBenchLocalEndpoint = ClientApiConfigPatterns.IsLocalEndpoint(normalizedBaseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl) ||
            string.IsNullOrWhiteSpace(normalizedApiKey) ||
            string.IsNullOrWhiteSpace(normalizedModel))
        {
            var error = "Anthropic 写入失败：当前入口缺少地址、密钥或模型。";
            return Task.FromResult(new ClientAppApplyResult(
                false,
                error,
                [],
                [],
                [],
                "missing-context")
            {
                TargetResults =
                [
                    new ClientAppTargetApplyResult(
                        "claude-cli",
                        "Claude CLI",
                        ClientApplyProtocolKind.Anthropic,
                        false,
                        [],
                        [],
                        error)
                ]
            });
        }

        var claudeRoot = Path.Combine(_environment.UserProfilePath, ".claude");
        var settingsPath = Path.Combine(claudeRoot, "settings.json");
        var onboardingPath = Path.Combine(_environment.UserProfilePath, ".claude.json");
        _environment.EnsureDirectoryExists(claudeRoot);
        RelayBenchBackupRetention.PruneAllUnderDirectory(_environment, claudeRoot);

        List<string> changedFiles = [];
        List<string> backupFiles = [];
        var useAuthToken = isRelayBenchLocalEndpoint ||
                           !ClientApiConfigPatterns.IsOfficialEndpoint(normalizedBaseUrl, AnthropicOfficialBaseUrl);
        ApplyFile(
            settingsPath,
            existing => UpsertClaudeSettings(
                existing,
                normalizedBaseUrl,
                normalizedApiKey,
                normalizedModel,
                useAuthToken),
            changedFiles,
            backupFiles);
        ApplyFile(
            onboardingPath,
            UpsertClaudeOnboarding,
            changedFiles,
            backupFiles);

        var displayName = isRelayBenchLocalEndpoint
            ? "Claude CLI（本地转换）"
            : "Claude CLI";

        return Task.FromResult(new ClientAppApplyResult(
            true,
            BuildSummary(changedFiles.Count, isRelayBenchLocalEndpoint),
            changedFiles,
            backupFiles,
            [displayName],
            null)
        {
            TargetResults =
            [
                new ClientAppTargetApplyResult(
                    "claude-cli",
                    displayName,
                    ClientApplyProtocolKind.Anthropic,
                    true,
                    changedFiles,
                    backupFiles,
                    null)
            ]
        });
    }

    private void ApplyFile(
        string path,
        Func<string, string> transform,
        List<string> changedFiles,
        List<string> backupFiles)
    {
        var original = _environment.ReadFileText(path) ?? string.Empty;
        var updated = transform(original);
        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrEmpty(original))
        {
            var backupPath = $"{path}.relaybench-backup-{DateTime.Now:yyyyMMddHHmmss}";
            _environment.WriteFileText(backupPath, original);
            backupFiles.Add(backupPath);
            RelayBenchBackupRetention.PruneForOriginalFile(_environment, path);
        }

        _environment.WriteFileText(path, updated);
        changedFiles.Add(path);
    }

    private static string UpsertClaudeSettings(
        string existingContent,
        string baseUrl,
        string apiKey,
        string model,
        bool useAuthToken)
    {
        var root = ClientApiConfigPatterns.TryParseJsonObject(existingContent) ?? new JsonObject();
        var env = root["env"] as JsonObject;
        if (env is null)
        {
            env = [];
            root["env"] = env;
        }

        env["ANTHROPIC_BASE_URL"] = baseUrl;
        env["ANTHROPIC_MODEL"] = model;
        env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model;
        env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model;
        env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = ResolveClaudeFastModel(model);
        env["CLAUDE_CODE_SUBAGENT_MODEL"] = ResolveClaudeFastModel(model);
        if (ShouldUseMaxEffort(baseUrl, model))
        {
            env["CLAUDE_CODE_EFFORT_LEVEL"] = ClaudeCodeEffortLevel;
        }
        else
        {
            env.Remove("CLAUDE_CODE_EFFORT_LEVEL");
        }

        if (useAuthToken)
        {
            env.Remove("ANTHROPIC_API_KEY");
            env["ANTHROPIC_AUTH_TOKEN"] = apiKey;
        }
        else
        {
            env.Remove("ANTHROPIC_AUTH_TOKEN");
            env["ANTHROPIC_API_KEY"] = apiKey;
        }

        return ClientApiConfigPatterns.SerializeJson(root);
    }

    private static string UpsertClaudeOnboarding(string existingContent)
    {
        var root = ClientApiConfigPatterns.TryParseJsonObject(existingContent) ?? new JsonObject();
        root["hasCompletedOnboarding"] = true;
        return ClientApiConfigPatterns.SerializeJson(root);
    }

    private static string ResolveClaudeFastModel(string model)
        => IsDeepSeekModel(model)
            ? "deepseek-v4-flash"
            : model;

    private static bool ShouldUseMaxEffort(string baseUrl, string model)
        => IsDeepSeekModel(model) ||
           baseUrl.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeepSeekModel(string model)
        => model.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase);

    private static string BuildSummary(int changedFileCount, bool isRelayBenchLocalEndpoint)
    {
        if (changedFileCount == 0)
        {
            return isRelayBenchLocalEndpoint
                ? "Claude CLI 已经指向 RelayBench 本地统一出口，无需改动。"
                : "Claude CLI 配置已与当前入口一致，无需改动。";
        }

        return isRelayBenchLocalEndpoint
            ? $"已将 Claude CLI 指向 RelayBench 本地统一出口，OpenAI Chat 上游会在本地转换为 Anthropic Messages，共处理 {changedFileCount} 个文件。"
            : $"已将当前入口应用到 Claude CLI，共处理 {changedFileCount} 个文件。";
    }

    private static string NormalizeAnthropicBaseUrl(string? value)
    {
        var normalized = ClientApiConfigPatterns.NormalizeEndpoint(value);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return normalized;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/v1/messages".Length].TrimEnd('/');
        }
        else if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/v1".Length].TrimEnd('/');
        }

        builder.Path = string.IsNullOrWhiteSpace(path) ? "/" : path;
        return builder.Uri.ToString().TrimEnd('/');
    }
}
