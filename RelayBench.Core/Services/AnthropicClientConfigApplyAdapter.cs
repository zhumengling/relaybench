using System.Text.Json.Nodes;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class AnthropicClientConfigApplyAdapter
{
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

        var normalizedBaseUrl = ClientApiConfigPatterns.NormalizeEndpoint(endpoint.BaseUrl).TrimEnd('/');
        var normalizedApiKey = endpoint.ApiKey?.Trim() ?? string.Empty;
        var normalizedModel = endpoint.Model?.Trim() ?? string.Empty;
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
        _environment.EnsureDirectoryExists(claudeRoot);
        RelayBenchBackupRetention.PruneAllUnderDirectory(_environment, claudeRoot);

        List<string> changedFiles = [];
        List<string> backupFiles = [];
        ApplyFile(
            settingsPath,
            existing => UpsertClaudeSettings(existing, normalizedBaseUrl, normalizedApiKey, normalizedModel),
            changedFiles,
            backupFiles);

        return Task.FromResult(new ClientAppApplyResult(
            true,
            changedFiles.Count == 0
                ? "Claude CLI 配置已与当前入口一致，无需改动。"
                : $"已将当前入口应用到 Claude CLI，共处理 {changedFiles.Count} 个文件。",
            changedFiles,
            backupFiles,
            ["Claude CLI"],
            null)
        {
            TargetResults =
            [
                new ClientAppTargetApplyResult(
                    "claude-cli",
                    "Claude CLI",
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
        string model)
    {
        var root = ClientApiConfigPatterns.TryParseJsonObject(existingContent) ?? new JsonObject();
        var env = root["env"] as JsonObject;
        if (env is null)
        {
            env = [];
            root["env"] = env;
        }

        env["ANTHROPIC_API_KEY"] = apiKey;
        env["ANTHROPIC_BASE_URL"] = baseUrl;
        env["ANTHROPIC_MODEL"] = model;

        return ClientApiConfigPatterns.SerializeJson(root);
    }
}
