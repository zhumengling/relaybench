using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

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

        var isCodexClient = clientName is "Codex CLI" or "Codex Desktop";
        var codexState = isCodexClient
            ? CodexRestoreStateStorage.TryLoad(_environment)
            : null;
        List<string> changedFiles = [];
        List<string> backupFiles = [];

        foreach (var filePath in targetFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileExists = _environment.FileExists(filePath);
            var original = fileExists
                ? _environment.ReadFileText(filePath) ?? string.Empty
                : string.Empty;
            var action = ResolveRestoreAction(clientName, filePath, fileExists, original, codexState);
            if (!action.Changed)
            {
                continue;
            }

            if (fileExists && !string.IsNullOrEmpty(original))
            {
                var backupPath = $"{filePath}.relaybench-backup-{DateTime.Now:yyyyMMddHHmmss}";
                _environment.WriteFileText(backupPath, original);
                backupFiles.Add(backupPath);
            }

            if (action.DeleteFile)
            {
                _environment.DeleteFile(filePath);
            }
            else if (action.UpdatedContent is not null)
            {
                _environment.WriteFileText(filePath, action.UpdatedContent);
            }

            changedFiles.Add(filePath);
        }

        if (isCodexClient && codexState is not null)
        {
            CodexRestoreStateStorage.Delete(_environment);
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
                Path.Combine(userProfile, ".codex", "auth.json"),
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

    private RestoreAction ResolveRestoreAction(
        string clientName,
        string filePath,
        bool fileExists,
        string currentContent,
        CodexRestoreStateStorage.CodexRestoreState? codexState)
    {
        if (clientName is "Codex CLI" or "Codex Desktop")
        {
            if (TryResolveCodexStateRestore(filePath, fileExists, currentContent, codexState, out var stateAction))
            {
                return stateAction;
            }

            if (TryResolveLegacyCodexBackupRestore(filePath, fileExists, currentContent, out var legacyAction))
            {
                return legacyAction;
            }

            return CleanCodexContent(filePath, fileExists, currentContent);
        }

        if (!fileExists || string.IsNullOrWhiteSpace(currentContent))
        {
            return RestoreAction.NoChange;
        }

        var updated = CleanContent(filePath, currentContent, out var changed);
        return changed
            ? RestoreAction.Write(updated)
            : RestoreAction.NoChange;
    }

    private bool TryResolveCodexStateRestore(
        string filePath,
        bool fileExists,
        string currentContent,
        CodexRestoreStateStorage.CodexRestoreState? state,
        out RestoreAction action)
    {
        action = RestoreAction.NoChange;
        if (state?.Files is null)
        {
            return false;
        }

        var entry = state.Files.FirstOrDefault(file =>
            string.Equals(file.Path, filePath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        if (!entry.Existed)
        {
            action = fileExists
                ? RestoreAction.Delete()
                : RestoreAction.NoChange;
            return true;
        }

        var restoredContent = entry.Content ?? string.Empty;
        action = fileExists && string.Equals(currentContent, restoredContent, StringComparison.Ordinal)
            ? RestoreAction.NoChange
            : RestoreAction.Write(restoredContent);
        return true;
    }

    private bool TryResolveLegacyCodexBackupRestore(
        string filePath,
        bool fileExists,
        string currentContent,
        out RestoreAction action)
    {
        action = RestoreAction.NoChange;
        if (!CodexOfficialConfigTools.TryLoadOfficialLikeBackup(_environment, filePath, out var backupContent) ||
            backupContent is null)
        {
            return false;
        }

        action = fileExists && string.Equals(currentContent, backupContent, StringComparison.Ordinal)
            ? RestoreAction.NoChange
            : RestoreAction.Write(backupContent);
        return true;
    }

    private static RestoreAction CleanCodexContent(string filePath, bool fileExists, string currentContent)
    {
        if (!fileExists)
        {
            return RestoreAction.NoChange;
        }

        var rewritten = CodexOfficialConfigTools.RewriteToOfficialLike(filePath, currentContent);
        if (!rewritten.Changed)
        {
            return RestoreAction.NoChange;
        }

        return rewritten.DeleteFile
            ? RestoreAction.Delete()
            : RestoreAction.Write(rewritten.UpdatedContent ?? string.Empty);
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

    private readonly record struct RestoreAction(bool Changed, bool DeleteFile, string? UpdatedContent)
    {
        public static RestoreAction NoChange => new(false, false, null);

        public static RestoreAction Delete()
            => new(true, true, null);

        public static RestoreAction Write(string content)
            => new(true, false, content);
    }
}
