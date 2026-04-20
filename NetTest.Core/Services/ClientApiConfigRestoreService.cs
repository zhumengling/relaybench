using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed class ClientApiConfigRestoreService
{
    private readonly IClientApiConfigMutationEnvironment _environment;

    public ClientApiConfigRestoreService(IClientApiConfigMutationEnvironment? environment = null)
    {
        _environment = environment ?? new ClientApiDiagnosticEnvironment();
    }

    public Task<ClientApiConfigRestoreResult> RestoreAsync(string clientName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var targetFiles = ResolveTargetFiles(clientName);
        if (targetFiles.Count == 0)
        {
            return Task.FromResult(new ClientApiConfigRestoreResult(
                false,
                $"当前暂不支持自动还原 {clientName} 的默认配置。",
                [],
                [],
                "unsupported-client"));
        }

        List<string> changedFiles = [];
        List<string> backupFiles = [];

        foreach (var filePath in targetFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_environment.FileExists(filePath))
            {
                continue;
            }

            var original = _environment.ReadFileText(filePath);
            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var updated = CleanContent(filePath, original!, out var changed);
            if (!changed)
            {
                continue;
            }

            var backupPath = $"{filePath}.nettest-backup-{DateTime.Now:yyyyMMddHHmmss}";
            _environment.WriteFileText(backupPath, original!);
            _environment.WriteFileText(filePath, updated);

            changedFiles.Add(filePath);
            backupFiles.Add(backupPath);
        }

        if (changedFiles.Count == 0)
        {
            return Task.FromResult(new ClientApiConfigRestoreResult(
                true,
                $"未在 {clientName} 的已知配置文件中发现需要清理的代理接管或自定义入口项。",
                [],
                [],
                null));
        }

        return Task.FromResult(new ClientApiConfigRestoreResult(
            true,
            $"已恢复 {clientName} 的默认入口配置，共处理 {changedFiles.Count} 个文件。",
            changedFiles,
            backupFiles,
            null));
    }

    private List<string> ResolveTargetFiles(string clientName)
    {
        var userProfile = _environment.UserProfilePath;
        var roamingAppData = _environment.RoamingAppDataPath;

        return clientName switch
        {
            "Codex CLI" or "Codex Desktop" => [
                Path.Combine(userProfile, ".codex", "config.toml"),
                Path.Combine(userProfile, ".codex", "settings.json")
            ],
            "VSCode Codex" => [
                Path.Combine(roamingAppData, "Code", "User", "settings.json"),
                Path.Combine(roamingAppData, "Code - Insiders", "User", "settings.json")
            ],
            "Claude CLI" => [
                Path.Combine(userProfile, ".claude", "settings.json"),
                Path.Combine(userProfile, ".claude", "claude.json"),
                Path.Combine(userProfile, ".claude.json")
            ],
            "Antigravity" => [
                Path.Combine(roamingAppData, "Antigravity", "User", "settings.json"),
                Path.Combine(userProfile, ".gemini", "antigravity", "mcp_config.json"),
                Path.Combine(userProfile, ".gemini", ".env")
            ],
            _ => []
        };
    }

    private static string CleanContent(string path, string content, out bool changed)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var root = ClientApiConfigPatterns.TryParseJsonObject(content);
            if (root is null)
            {
                return ClientApiConfigPatterns.RemoveLineBasedOverrides(content, out changed);
            }

            changed = ClientApiConfigPatterns.RemoveJsonOverrides(root);
            return changed
                ? ClientApiConfigPatterns.SerializeJson(root)
                : content;
        }

        return ClientApiConfigPatterns.RemoveLineBasedOverrides(content, out changed);
    }
}
