using System.Text.Json.Nodes;

namespace NetTest.Core.Services;

internal static class CodexOfficialConfigTools
{
    private const string CodexProviderKey = "custom";

    public static CodexRewriteResult RewriteToOfficialLike(string filePath, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return CodexRewriteResult.NoChange;
        }

        if (filePath.EndsWith("config.toml", StringComparison.OrdinalIgnoreCase))
        {
            return RewriteConfigToml(content);
        }

        if (filePath.EndsWith("auth.json", StringComparison.OrdinalIgnoreCase))
        {
            return RewriteAuthJson(content);
        }

        return RewriteGeneric(filePath, content);
    }

    public static bool IsOfficialLike(string filePath, string content)
        => !RewriteToOfficialLike(filePath, content).Changed;

    public static bool TryLoadOfficialLikeBackup(
        IClientApiConfigMutationEnvironment environment,
        string filePath,
        out string? backupContent)
    {
        backupContent = null;

        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var candidates = environment.EnumerateFiles(directory, $"{fileName}.nettest-backup-*")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var content = environment.ReadFileText(candidate);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (!IsOfficialLike(filePath, content))
            {
                continue;
            }

            backupContent = content;
            return true;
        }

        return false;
    }

    public static CodexBaselineSnapshot CreateBaselineSnapshot(
        IClientApiConfigMutationEnvironment environment,
        string filePath)
    {
        var existed = environment.FileExists(filePath);
        if (!existed)
        {
            return CodexBaselineSnapshot.Missing;
        }

        var currentContent = environment.ReadFileText(filePath) ?? string.Empty;
        if (IsOfficialLike(filePath, currentContent))
        {
            return new CodexBaselineSnapshot(true, currentContent);
        }

        if (TryLoadOfficialLikeBackup(environment, filePath, out var backupContent) &&
            backupContent is not null)
        {
            return new CodexBaselineSnapshot(true, backupContent);
        }

        var rewritten = RewriteToOfficialLike(filePath, currentContent);
        if (!rewritten.Changed)
        {
            return new CodexBaselineSnapshot(true, currentContent);
        }

        return rewritten.DeleteFile
            ? CodexBaselineSnapshot.Missing
            : new CodexBaselineSnapshot(true, rewritten.UpdatedContent ?? string.Empty);
    }

    private static CodexRewriteResult RewriteConfigToml(string content)
    {
        List<string> lines = SplitLines(content);
        var changed = false;

        changed |= RemoveSection(lines, "[model_providers.custom]");
        changed |= RemoveTopLevelStringAssignmentIfValue(lines, "model_provider", CodexProviderKey);

        if (!changed)
        {
            return CodexRewriteResult.NoChange;
        }

        var updated = JoinLines(lines);
        return string.IsNullOrWhiteSpace(updated)
            ? CodexRewriteResult.Delete()
            : CodexRewriteResult.Write(updated);
    }

    private static CodexRewriteResult RewriteAuthJson(string content)
    {
        var root = ClientApiConfigPatterns.TryParseJsonObject(content);
        if (root is null)
        {
            var updated = content
                .Replace("\"OPENAI_API_KEY\"", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("\"OPENAI_BASE_URL\"", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("\"auth_mode\"", string.Empty, StringComparison.OrdinalIgnoreCase);
            return string.Equals(updated, content, StringComparison.Ordinal)
                ? CodexRewriteResult.NoChange
                : CodexRewriteResult.Write(updated);
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
            return CodexRewriteResult.NoChange;
        }

        return root.Count == 0
            ? CodexRewriteResult.Delete()
            : CodexRewriteResult.Write(ClientApiConfigPatterns.SerializeJson(root));
    }

    private static CodexRewriteResult RewriteGeneric(string filePath, string content)
    {
        if (filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var root = ClientApiConfigPatterns.TryParseJsonObject(content);
            if (root is null)
            {
                var updated = ClientApiConfigPatterns.RemoveLineBasedOverrides(content, out var changedFromMalformedJson);
                return changedFromMalformedJson
                    ? CodexRewriteResult.Write(updated)
                    : CodexRewriteResult.NoChange;
            }

            var changedFromJson = ClientApiConfigPatterns.RemoveJsonOverrides(root);
            return changedFromJson
                ? CodexRewriteResult.Write(ClientApiConfigPatterns.SerializeJson(root))
                : CodexRewriteResult.NoChange;
        }

        var cleaned = ClientApiConfigPatterns.RemoveLineBasedOverrides(content, out var changedFromLineText);
        return changedFromLineText
            ? CodexRewriteResult.Write(cleaned)
            : CodexRewriteResult.NoChange;
    }

    private static bool RemoveSection(List<string> lines, string header)
    {
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

        lines.RemoveRange(sectionIndex, sectionEnd - sectionIndex);
        return true;
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

    private static int FindSectionHeader(List<string> lines, string header)
        => lines.FindIndex(line => string.Equals(line.Trim(), header, StringComparison.OrdinalIgnoreCase));

    private static int FindNextSectionHeader(List<string> lines, int startIndex)
        => lines.FindIndex(startIndex, IsSectionHeader);

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

    internal readonly record struct CodexRewriteResult(bool Changed, bool DeleteFile, string? UpdatedContent)
    {
        public static CodexRewriteResult NoChange => new(false, false, null);

        public static CodexRewriteResult Delete()
            => new(true, true, null);

        public static CodexRewriteResult Write(string content)
            => new(true, false, content);
    }

    internal readonly record struct CodexBaselineSnapshot(bool Existed, string? Content)
    {
        public static CodexBaselineSnapshot Missing => new(false, null);
    }
}
