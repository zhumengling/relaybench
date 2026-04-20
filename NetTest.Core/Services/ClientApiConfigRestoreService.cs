using System.Text.Json.Nodes;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed class ClientApiConfigRestoreService
{
    private const string CodexProviderKey = "custom";
    private const string CodexProviderName = "Custom OpenAI-Compatible";
    private const string CodexWireApi = "responses";

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
                var backupPath = $"{filePath}.nettest-backup-{DateTime.Now:yyyyMMddHHmmss}";
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
        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var candidates = _environment.EnumerateFiles(directory, $"{fileName}.nettest-backup-*")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        var backupContent = _environment.ReadFileText(candidates[0]);
        if (backupContent is null)
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

        if (filePath.EndsWith("config.toml", StringComparison.OrdinalIgnoreCase))
        {
            return CleanCodexToml(currentContent);
        }

        if (filePath.EndsWith("auth.json", StringComparison.OrdinalIgnoreCase))
        {
            return CleanCodexAuthJson(currentContent);
        }

        if (string.IsNullOrWhiteSpace(currentContent))
        {
            return RestoreAction.NoChange;
        }

        var updated = CleanContent(filePath, currentContent, out var changed);
        return changed
            ? RestoreAction.Write(updated)
            : RestoreAction.NoChange;
    }

    private static RestoreAction CleanCodexAuthJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return RestoreAction.NoChange;
        }

        var root = ClientApiConfigPatterns.TryParseJsonObject(content);
        if (root is null)
        {
            var updated = content
                .Replace("\"OPENAI_API_KEY\"", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("\"auth_mode\"", string.Empty, StringComparison.OrdinalIgnoreCase);
            return string.Equals(updated, content, StringComparison.Ordinal)
                ? RestoreAction.NoChange
                : RestoreAction.Write(updated);
        }

        var changed = false;
        if (root.TryGetPropertyValue("auth_mode", out var authModeNode) &&
            authModeNode is JsonValue authModeValue &&
            authModeValue.TryGetValue<string>(out var authMode) &&
            string.Equals(authMode, "apikey", StringComparison.OrdinalIgnoreCase))
        {
            root.Remove("auth_mode");
            changed = true;
        }

        if (root.Remove("OPENAI_API_KEY"))
        {
            changed = true;
        }

        if (root.Remove("OPENAI_BASE_URL"))
        {
            changed = true;
        }

        if (!changed)
        {
            return RestoreAction.NoChange;
        }

        return root.Count == 0
            ? RestoreAction.Delete()
            : RestoreAction.Write(ClientApiConfigPatterns.SerializeJson(root));
    }

    private static RestoreAction CleanCodexToml(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return RestoreAction.NoChange;
        }

        List<string> lines = SplitLines(content);
        var changed = false;

        var removedManagedSection = RemoveManagedCodexCustomSection(lines);
        changed |= removedManagedSection;
        if (removedManagedSection)
        {
            changed |= RemoveTopLevelStringAssignmentIfValue(lines, "model_provider", CodexProviderKey);
        }

        if (!changed)
        {
            return RestoreAction.NoChange;
        }

        var updated = JoinLines(lines);
        return string.IsNullOrWhiteSpace(updated)
            ? RestoreAction.Delete()
            : RestoreAction.Write(updated);
    }

    private static bool RemoveTopLevelStringAssignmentIfValue(List<string> lines, string key, string expectedValue)
    {
        var firstSectionIndex = FindNextSectionHeader(lines, 0);
        var searchEnd = firstSectionIndex < 0 ? lines.Count : firstSectionIndex;
        var existingIndex = FindKeyAssignment(lines, key, 0, searchEnd);
        if (existingIndex < 0)
        {
            return false;
        }

        var currentValue = ParseAssignmentValue(lines[existingIndex]);
        if (!string.Equals(currentValue, expectedValue, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lines.RemoveAt(existingIndex);
        return true;
    }

    private static bool RemoveManagedCodexCustomSection(List<string> lines)
    {
        const string header = "[model_providers.custom]";
        var sectionIndex = FindSectionHeader(lines, header);
        if (sectionIndex < 0)
        {
            return false;
        }

        var sectionEnd = FindNextSectionHeader(lines, sectionIndex + 1);
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        string? nameValue = null;
        var hasExperimentalBearerToken = false;
        for (var index = sectionIndex + 1; index < sectionEnd; index++)
        {
            var trimmed = lines[index].Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                trimmed.StartsWith('#') ||
                trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var assignmentIndex = trimmed.IndexOf('=');
            if (assignmentIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..assignmentIndex].Trim();
            var value = trimmed[(assignmentIndex + 1)..].Trim().Trim('"', '\'');
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                nameValue = value;
            }
            else if (string.Equals(key, "experimental_bearer_token", StringComparison.OrdinalIgnoreCase))
            {
                hasExperimentalBearerToken = true;
            }
        }

        var managed = string.Equals(nameValue, CodexProviderName, StringComparison.Ordinal) ||
                      hasExperimentalBearerToken;
        if (!managed)
        {
            return false;
        }

        lines.RemoveRange(sectionIndex, sectionEnd - sectionIndex);
        return true;
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

    private static int FindSectionHeader(List<string> lines, string header)
        => lines.FindIndex(line => string.Equals(line.Trim(), header, StringComparison.OrdinalIgnoreCase));

    private static int FindNextSectionHeader(List<string> lines, int startIndex)
        => lines.FindIndex(startIndex, line => IsSectionHeader(line));

    private static int FindKeyAssignment(List<string> lines, string key, int startIndex, int endExclusive)
    {
        for (var index = startIndex; index < endExclusive; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith($"{key} ", StringComparison.Ordinal) ||
                trimmed.StartsWith($"{key}=", StringComparison.Ordinal))
            {
                var equalsIndex = trimmed.IndexOf('=');
                if (equalsIndex > 0 && string.Equals(trimmed[..equalsIndex].Trim(), key, StringComparison.Ordinal))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static bool IsSectionHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('[') && trimmed.EndsWith(']');
    }

    private static string ParseAssignmentValue(string line)
    {
        var trimmed = line.Trim();
        var assignmentIndex = trimmed.IndexOf('=');
        return assignmentIndex < 0
            ? string.Empty
            : trimmed[(assignmentIndex + 1)..].Trim().Trim('"', '\'');
    }

    private static List<string> SplitLines(string content)
        => string.IsNullOrWhiteSpace(content)
            ? []
            : content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .ToList();

    private static string JoinLines(List<string> lines)
    {
        List<string> normalized = [];
        var previousBlank = false;

        foreach (var line in lines)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank && previousBlank)
            {
                continue;
            }

            normalized.Add(line);
            previousBlank = isBlank;
        }

        while (normalized.Count > 0 && string.IsNullOrWhiteSpace(normalized[^1]))
        {
            normalized.RemoveAt(normalized.Count - 1);
        }

        return normalized.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, normalized) + Environment.NewLine;
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
