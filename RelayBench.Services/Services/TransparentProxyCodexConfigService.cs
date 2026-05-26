using System.IO;
using System.Text.Json;

namespace RelayBench.Services;

internal sealed class TransparentProxyCodexConfigService
{
    private const string ProviderKey = "relaybench";
    private const string ProviderSection = "model_providers.relaybench";
    private const string ProfileSection = "profiles.relaybench";
    private const string BackupMarker = ".relaybench-app-capture-backup-";
    private const string OpenAiBaseUrlKey = "openai_base_url";

    private readonly string _userProfilePath;

    public TransparentProxyCodexConfigService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    public TransparentProxyCodexConfigService(string userProfilePath)
    {
        _userProfilePath = string.IsNullOrWhiteSpace(userProfilePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(userProfilePath);
    }

    public TransparentProxyCodexConfigPreview Preview(
        string baseUrl,
        string model,
        string? preferredWireApi = null)
    {
        var configPath = ResolveConfigPath();
        var existing = File.Exists(configPath)
            ? File.ReadAllText(configPath)
            : string.Empty;
        var updated = BuildUpdatedConfig(existing, baseUrl, model, preferredWireApi);
        var changed = !string.Equals(existing, updated, StringComparison.Ordinal);
        var diagnostics = BuildDiagnostics(existing, configPath, baseUrl, preferredWireApi);
        return new TransparentProxyCodexConfigPreview(
            configPath,
            changed,
            BuildPreviewText(configPath, baseUrl, model, preferredWireApi, changed, diagnostics),
            diagnostics);
    }

    public TransparentProxyCodexConfigMutationResult Apply(
        string baseUrl,
        string model,
        string? preferredWireApi = null)
    {
        var configPath = ResolveConfigPath();
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existing = File.Exists(configPath)
            ? File.ReadAllText(configPath)
            : string.Empty;
        var updated = BuildUpdatedConfig(existing, baseUrl, model, preferredWireApi);
        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return new TransparentProxyCodexConfigMutationResult(
                true,
                "Codex config.toml 已经通过 openai_base_url 指向 RelayBench，本次无需改动。",
                configPath,
                null);
        }

        string? backupPath = null;
        if (!string.IsNullOrEmpty(existing))
        {
            backupPath = $"{configPath}{BackupMarker}{DateTime.Now:yyyyMMddHHmmss}";
            File.WriteAllText(backupPath, existing);
            PruneBackups(configPath);
        }

        File.WriteAllText(configPath, updated);
        return new TransparentProxyCodexConfigMutationResult(
            true,
            "已按 Codex 文档推荐写入 openai_base_url；Codex 后续会通过 RelayBench 本地统一出口进入共享路由队列。",
            configPath,
            backupPath);
    }

    public TransparentProxyCodexConfigMutationResult RestoreLatestBackup()
    {
        var configPath = ResolveConfigPath();
        var backupPath = FindLatestBackup(configPath);
        if (backupPath is null)
        {
            return new TransparentProxyCodexConfigMutationResult(
                false,
                "没有找到 Codex config.toml 的 RelayBench 接管备份，无法自动恢复。",
                configPath,
                null);
        }

        var backup = File.ReadAllText(backupPath);
        var current = File.Exists(configPath)
            ? File.ReadAllText(configPath)
            : string.Empty;
        var restored = BuildRestoredConfig(current, backup);
        if (string.Equals(current, restored, StringComparison.Ordinal))
        {
            return new TransparentProxyCodexConfigMutationResult(
                true,
                "Codex config.toml 当前没有需要回滚的 RelayBench 接管字段，已保持现有配置不变。",
                configPath,
                backupPath);
        }

        if (!string.Equals(current, backup, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(current))
        {
            var rollbackBackupPath = $"{configPath}{BackupMarker}before-restore-{DateTime.Now:yyyyMMddHHmmss}";
            File.WriteAllText(rollbackBackupPath, current);
        }

        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(configPath, restored);
        return new TransparentProxyCodexConfigMutationResult(
            true,
            "已合并恢复 Codex config.toml：仅回滚 RelayBench 接管入口，保留用户后续新增配置。",
            configPath,
            backupPath);
    }

    public TransparentProxyCodexConfigDiagnostics Diagnose(
        string baseUrl,
        string? preferredWireApi = null)
    {
        var configPath = ResolveConfigPath();
        var existing = File.Exists(configPath)
            ? File.ReadAllText(configPath)
            : string.Empty;
        return BuildDiagnostics(existing, configPath, baseUrl, preferredWireApi);
    }

    private string ResolveConfigPath()
        => Path.Combine(_userProfilePath, ".codex", "config.toml");

    private static string BuildUpdatedConfig(
        string existing,
        string baseUrl,
        string model,
        string? preferredWireApi)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? "gpt-5.4" : model.Trim();
        List<string> lines = SplitLines(existing);

        RemoveLegacyRelayBenchTakeover(lines);
        UpsertTopLevelString(lines, OpenAiBaseUrlKey, normalizedBaseUrl);
        UpsertTopLevelString(lines, "model", normalizedModel);

        return JoinLines(lines);
    }

    private static string BuildPreviewText(
        string configPath,
        string baseUrl,
        string model,
        string? preferredWireApi,
        bool changed,
        TransparentProxyCodexConfigDiagnostics diagnostics)
    {
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? "gpt-5.4" : model.Trim();
        var diagnosticsLines = diagnostics.Items.Count == 0
            ? ["- 正常：未发现需要特别处理的 Codex 配置风险。"]
            : diagnostics.Items.Select(FormatDiagnosticItem);

        return string.Join(
            Environment.NewLine,
            [
                $"目标文件：{configPath}",
                "模式：Codex 文档推荐的 openai_base_url 简洁接管",
                changed ? "动作：写入或更新 RelayBench 本地入口，并在改动前备份原文件。" : "动作：无需改动，当前配置已经一致。",
                "写入内容：",
                $"{OpenAiBaseUrlKey} = \"{NormalizeBaseUrl(baseUrl)}\"",
                $"model = \"{normalizedModel}\"",
                "清理策略：如果发现旧版 RelayBench provider/profile 接管字段，会在写入时移除，避免覆盖用户的全局 provider。",
                "不会改动：auth.json、MCP servers、hooks、权限策略、项目级 .codex/config.toml。",
                "写入前检查：",
                .. diagnosticsLines,
                "高级提示：临时接管优先使用 codex -c openai_base_url=\"...\"；长期接管再写入用户级 config.toml。"
            ]);
    }

    private static TransparentProxyCodexConfigDiagnostics BuildDiagnostics(
        string existing,
        string configPath,
        string baseUrl,
        string? preferredWireApi)
    {
        List<TransparentProxyCodexConfigDiagnosticItem> items = [];
        var desiredBaseUrl = NormalizeBaseUrl(baseUrl);
        var desiredWireApi = NormalizeWireApi(preferredWireApi);

        if (string.IsNullOrWhiteSpace(existing))
        {
            items.Add(new("info", "未找到现有 config.toml；接管时会创建用户级配置文件。"));
            return new TransparentProxyCodexConfigDiagnostics(configPath, items);
        }

        var lines = SplitLines(existing);
        var currentBaseUrl = ReadTopLevelStringValue(lines, OpenAiBaseUrlKey);
        if (string.IsNullOrWhiteSpace(currentBaseUrl))
        {
            items.Add(new("warning", "当前未设置 openai_base_url；Codex 可能仍走默认入口。"));
        }
        else if (AreSameNormalizedBaseUrl(currentBaseUrl, desiredBaseUrl))
        {
            items.Add(new("ok", "openai_base_url 已指向 RelayBench 本地入口。"));
        }
        else
        {
            items.Add(new("warning", $"openai_base_url 当前指向 {currentBaseUrl}，接管时会改为 {desiredBaseUrl}。"));
        }

        var modelProvider = ReadTopLevelStringValue(lines, "model_provider");
        if (string.Equals(modelProvider, ProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new("warning", "发现旧版 model_provider = \"relaybench\"；接管时会清理，改用 openai_base_url。"));
        }
        else if (!string.IsNullOrWhiteSpace(modelProvider) && !IsOpenAiProviderName(modelProvider))
        {
            items.Add(new("warning", $"当前 model_provider = \"{modelProvider}\" 可能优先于 openai_base_url；如 Codex 未经过 RelayBench，请切回默认 OpenAI provider 或使用 -c 临时覆盖。"));
        }

        if (IsTopLevelStringValue(lines, "profile", ProviderKey))
        {
            items.Add(new("warning", "发现旧版 profile = \"relaybench\"；接管时会清理，避免 RelayBench 固化为全局默认 profile。"));
        }

        if (FindSectionHeader(lines, $"[{ProviderSection}]") >= 0)
        {
            items.Add(new("warning", "发现旧版 [model_providers.relaybench]；接管时会清理，避免与 openai_base_url 产生双入口。"));
            var currentWireApi = ReadSectionStringValue(lines, ProviderSection, "wire_api");
            if (!string.IsNullOrWhiteSpace(currentWireApi) &&
                !string.Equals(NormalizeWireApi(currentWireApi), desiredWireApi, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new("warning", $"旧版 relaybench provider 使用 wire_api = \"{currentWireApi}\"，当前探测偏好为 \"{desiredWireApi}\"。"));
            }
        }

        if (FindSectionHeader(lines, $"[{ProfileSection}]") >= 0)
        {
            items.Add(new("warning", "发现旧版 [profiles.relaybench]；接管时会清理，避免残留 profile 覆盖用户选择。"));
        }

        if (HasSectionWithNameOrPrefix(lines, "mcp_servers"))
        {
            items.Add(new("info", "检测到 MCP servers 配置；RelayBench 只调整模型入口，不会改动现有 MCP 服务器。"));
        }

        if (HasSectionWithNameOrPrefix(lines, "hooks"))
        {
            items.Add(new("info", "检测到 hooks 配置；RelayBench 不会修改现有通知、脚本或钩子行为。"));
        }

        var approvalPolicy = ReadTopLevelStringValue(lines, "approval_policy");
        if (string.Equals(approvalPolicy, "never", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new("warning", "approval_policy = \"never\" 会让 Codex 自动执行更多操作；RelayBench 不会修改它，但建议用户确认这是有意设置。"));
        }

        var sandboxMode = ReadTopLevelStringValue(lines, "sandbox_mode");
        if (string.Equals(sandboxMode, "danger-full-access", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new("warning", "sandbox_mode = \"danger-full-access\" 权限很高；RelayBench 不会修改它，但预览中会提醒用户复核。"));
        }

        if (HasTopLevelAssignment(lines, "default_permissions") &&
            HasTopLevelAssignment(lines, "sandbox_mode"))
        {
            items.Add(new("warning", "同时存在 default_permissions 与旧版 sandbox_mode；Codex 新权限配置建议避免混用。"));
        }

        if (items.Count == 0)
        {
            items.Add(new("ok", "未发现需要特别处理的 Codex 配置风险。"));
        }

        return new TransparentProxyCodexConfigDiagnostics(configPath, items);
    }

    private static string FormatDiagnosticItem(TransparentProxyCodexConfigDiagnosticItem item)
        => item.Severity.ToLowerInvariant() switch
        {
            "ok" => $"- 正常：{item.Message}",
            "warning" => $"- 注意：{item.Message}",
            "error" => $"- 错误：{item.Message}",
            _ => $"- 提示：{item.Message}"
        };

    private static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        var candidate = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
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
        normalized = builder.Uri.ToString().TrimEnd('/');
        return true;
    }

    private static string NormalizeBaseUrl(string? value)
        => TryNormalizeBaseUrl(value, out var normalized)
            ? normalized
            : "http://127.0.0.1:17880/v1";

    private static bool AreSameNormalizedBaseUrl(string current, string desired)
        => TryNormalizeBaseUrl(current, out var normalizedCurrent) &&
           string.Equals(normalizedCurrent, desired, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWireApi(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
        return normalized is "chat" or "chat-completions" or "chat/completions" or "openai-chat"
            ? "chat"
            : "responses";
    }

    private static void UpsertTopLevelString(List<string> lines, string key, string value)
    {
        var lineContent = $"{key} = {SerializeTomlString(value)}";
        var firstSectionIndex = FindNextSectionHeader(lines, 0);
        var searchEnd = firstSectionIndex < 0 ? lines.Count : firstSectionIndex;
        var existingIndex = FindKeyAssignment(lines, key, 0, searchEnd);
        if (existingIndex >= 0)
        {
            lines[existingIndex] = lineContent;
            return;
        }

        lines.Insert(searchEnd, lineContent);
    }

    private static void RemoveLegacyRelayBenchTakeover(List<string> lines)
    {
        RemoveSection(lines, $"[{ProviderSection}]");
        RemoveSection(lines, $"[{ProfileSection}]");
        RemoveTopLevelAssignmentIfStringValue(lines, "model_provider", ProviderKey);
        RemoveTopLevelAssignmentIfStringValue(lines, "profile", ProviderKey);
    }

    private static void RemoveSection(List<string> lines, string header)
    {
        var sectionIndex = FindSectionHeader(lines, header);
        if (sectionIndex < 0)
        {
            return;
        }

        var sectionEnd = FindNextSectionHeader(lines, sectionIndex + 1);
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        lines.RemoveRange(sectionIndex, sectionEnd - sectionIndex);
    }

    private static void RemoveTopLevelAssignmentIfStringValue(List<string> lines, string key, string expectedValue)
    {
        var firstSection = FindNextSectionHeader(lines, 0);
        var searchEnd = firstSection < 0 ? lines.Count : firstSection;
        var index = FindKeyAssignment(lines, key, 0, searchEnd);
        if (index < 0)
        {
            return;
        }

        var value = ReadAssignmentValue(lines[index]);
        if (string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase))
        {
            lines.RemoveAt(index);
        }
    }

    private static string BuildRestoredConfig(string current, string backup)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return backup;
        }

        var lines = SplitLines(current);
        var backupLines = SplitLines(backup);
        var hasRelayBenchSection = FindSectionHeader(lines, $"[{ProviderSection}]") >= 0;
        var hasRelayBenchProfileSection = FindSectionHeader(lines, $"[{ProfileSection}]") >= 0;
        var usesRelayBenchProvider = IsTopLevelStringValue(lines, "model_provider", ProviderKey);
        var usesRelayBenchProfile = IsTopLevelStringValue(lines, "profile", ProviderKey);
        var hasRelayBenchBaseUrl = ReadTopLevelStringValue(lines, OpenAiBaseUrlKey) is { } currentBaseUrl &&
                                   IsLocalRelayBenchBaseUrl(currentBaseUrl);
        if (!hasRelayBenchSection &&
            !hasRelayBenchProfileSection &&
            !usesRelayBenchProvider &&
            !usesRelayBenchProfile &&
            !hasRelayBenchBaseUrl)
        {
            return current;
        }

        RemoveLegacyRelayBenchTakeover(lines);
        RestoreTopLevelAssignment(lines, backupLines, OpenAiBaseUrlKey);
        RestoreTopLevelAssignment(lines, backupLines, "model_provider");
        RestoreTopLevelAssignment(lines, backupLines, "profile");
        RestoreTopLevelAssignment(lines, backupLines, "model");
        return JoinLines(lines);
    }

    private static void RestoreTopLevelAssignment(List<string> lines, List<string> backupLines, string key)
    {
        var currentFirstSection = FindNextSectionHeader(lines, 0);
        var currentSearchEnd = currentFirstSection < 0 ? lines.Count : currentFirstSection;
        var currentIndex = FindKeyAssignment(lines, key, 0, currentSearchEnd);

        var backupFirstSection = FindNextSectionHeader(backupLines, 0);
        var backupSearchEnd = backupFirstSection < 0 ? backupLines.Count : backupFirstSection;
        var backupIndex = FindKeyAssignment(backupLines, key, 0, backupSearchEnd);
        if (backupIndex >= 0)
        {
            if (currentIndex >= 0)
            {
                lines[currentIndex] = backupLines[backupIndex];
                return;
            }

            var insertIndex = FindNextSectionHeader(lines, 0);
            lines.Insert(insertIndex < 0 ? lines.Count : insertIndex, backupLines[backupIndex]);
            return;
        }

        if (currentIndex >= 0)
        {
            lines.RemoveAt(currentIndex);
        }
    }

    private static bool IsTopLevelStringValue(List<string> lines, string key, string expectedValue)
    {
        var value = ReadTopLevelStringValue(lines, key);
        return string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalRelayBenchBaseUrl(string value)
    {
        if (!TryNormalizeBaseUrl(value, out var normalized) ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Port is >= 1 and <= 65535 &&
               (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOpenAiProviderName(string value)
    {
        var normalized = value
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized is "openai" or "openaicompatible";
    }

    private static bool HasTopLevelAssignment(List<string> lines, string key)
    {
        var firstSection = FindNextSectionHeader(lines, 0);
        var searchEnd = firstSection < 0 ? lines.Count : firstSection;
        return FindKeyAssignment(lines, key, 0, searchEnd) >= 0;
    }

    private static bool HasSectionWithNameOrPrefix(List<string> lines, string sectionName)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            {
                continue;
            }

            var normalized = trimmed.Trim('[', ']').Trim();
            if (string.Equals(normalized, sectionName, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith($"{sectionName}.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadTopLevelStringValue(List<string> lines, string key)
    {
        var firstSection = FindNextSectionHeader(lines, 0);
        var searchEnd = firstSection < 0 ? lines.Count : firstSection;
        var index = FindKeyAssignment(lines, key, 0, searchEnd);
        return index < 0 ? null : ReadAssignmentValue(lines[index]);
    }

    private static string? ReadSectionStringValue(List<string> lines, string sectionName, string key)
    {
        var sectionIndex = FindSectionHeader(lines, $"[{sectionName}]");
        if (sectionIndex < 0)
        {
            return null;
        }

        var sectionEnd = FindNextSectionHeader(lines, sectionIndex + 1);
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        var keyIndex = FindKeyAssignment(lines, key, sectionIndex + 1, sectionEnd);
        return keyIndex < 0 ? null : ReadAssignmentValue(lines[keyIndex]);
    }

    private static string? ReadAssignmentValue(string line)
    {
        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
        {
            return null;
        }

        var raw = StripInlineComment(line[(equalsIndex + 1)..]).Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(raw);
            }
            catch
            {
                return raw.Trim('"');
            }
        }

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1];
        }

        return raw;
    }

    private static string StripInlineComment(string value)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && inDoubleQuote)
            {
                escaped = true;
                continue;
            }

            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (ch == '#' && !inSingleQuote && !inDoubleQuote)
            {
                return value[..index].TrimEnd();
            }
        }

        return value;
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
        while (lines.Count > 1 &&
               string.IsNullOrWhiteSpace(lines[^1]) &&
               string.IsNullOrWhiteSpace(lines[^2]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var text = string.Join(Environment.NewLine, lines).TrimEnd();
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text + Environment.NewLine;
    }

    private static string SerializeTomlString(string value)
        => JsonSerializer.Serialize(value);

    private static string? FindLatestBackup(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath);
        var fileName = Path.GetFileName(configPath);
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

    private static void PruneBackups(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath);
        var fileName = Path.GetFileName(configPath);
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

internal sealed record TransparentProxyCodexConfigPreview(
    string ConfigPath,
    bool Changed,
    string PreviewText,
    TransparentProxyCodexConfigDiagnostics Diagnostics);

internal sealed record TransparentProxyCodexConfigDiagnostics(
    string ConfigPath,
    IReadOnlyList<TransparentProxyCodexConfigDiagnosticItem> Items);

internal sealed record TransparentProxyCodexConfigDiagnosticItem(
    string Severity,
    string Message);

internal sealed record TransparentProxyCodexConfigMutationResult(
    bool Succeeded,
    string Summary,
    string ConfigPath,
    string? BackupPath);
