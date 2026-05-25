using System.IO;
using System.Text.Json;

namespace RelayBench.Services;

internal sealed class TransparentProxyCodexConfigService
{
    private const string ProviderKey = "relaybench";
    private const string ProviderSection = "model_providers.relaybench";
    private const string LocalBearerToken = "relaybench-local";
    private const string BackupMarker = ".relaybench-app-capture-backup-";

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
        return new TransparentProxyCodexConfigPreview(
            configPath,
            changed,
            BuildPreviewText(configPath, baseUrl, model, preferredWireApi, changed));
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
                "Codex CLI 已经指向 RelayBench 本地统一出口，无需改动。",
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
            "已写入 Codex CLI 接管配置，后续 Codex CLI 会通过 RelayBench 本地统一出口进入共享路由队列。",
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
                "没有找到 Codex CLI 的 RelayBench 接管备份，无法自动恢复。",
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
                "Codex CLI 当前配置已经没有需要回滚的 RelayBench 接管字段，已保持用户现有配置不变。",
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
            "已合并恢复 Codex CLI 配置：仅回滚 RelayBench provider 和接管入口，保留用户后续新增配置。",
            configPath,
            backupPath);
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
        var wireApi = NormalizeWireApi(preferredWireApi);
        List<string> lines = SplitLines(existing);

        UpsertTopLevelString(lines, "model_provider", ProviderKey);
        UpsertTopLevelString(lines, "model", normalizedModel);
        RemoveSection(lines, $"[{ProviderSection}]");

        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.Add(string.Empty);
        }

        lines.Add($"[{ProviderSection}]");
        lines.Add("name = \"RelayBench\"");
        lines.Add($"base_url = {SerializeTomlString(normalizedBaseUrl)}");
        lines.Add($"wire_api = {SerializeTomlString(wireApi)}");
        lines.Add("request_max_retries = 0");
        lines.Add("stream_max_retries = 0");
        lines.Add("stream_idle_timeout_ms = 600000");
        lines.Add($"experimental_bearer_token = {SerializeTomlString(LocalBearerToken)}");

        return JoinLines(lines);
    }

    private static string BuildPreviewText(
        string configPath,
        string baseUrl,
        string model,
        string? preferredWireApi,
        bool changed)
        => string.Join(
            Environment.NewLine,
            [
                $"目标文件：{configPath}",
                changed ? "动作：写入或更新 RelayBench provider，并在改动前备份原文件。" : "动作：无需改动，当前配置已经一致。",
                "写入内容：",
                "model_provider = \"relaybench\"",
                $"model = \"{(string.IsNullOrWhiteSpace(model) ? "gpt-5.4" : model.Trim())}\"",
                "[model_providers.relaybench]",
                "name = \"RelayBench\"",
                $"base_url = \"{NormalizeBaseUrl(baseUrl)}\"",
                $"wire_api = \"{NormalizeWireApi(preferredWireApi)}\"",
                "experimental_bearer_token = \"relaybench-local\""
            ]);

    private static string NormalizeBaseUrl(string? value)
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

    private static string NormalizeWireApi(string? value)
        => string.Equals(value, "chat", StringComparison.OrdinalIgnoreCase)
            ? "chat"
            : "responses";

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

    private static string BuildRestoredConfig(string current, string backup)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return backup;
        }

        var lines = SplitLines(current);
        var backupLines = SplitLines(backup);
        var hasRelayBenchSection = FindSectionHeader(lines, $"[{ProviderSection}]") >= 0;
        var usesRelayBenchProvider = IsTopLevelStringValue(lines, "model_provider", ProviderKey);
        if (!hasRelayBenchSection && !usesRelayBenchProvider)
        {
            return current;
        }

        RemoveSection(lines, $"[{ProviderSection}]");
        RestoreTopLevelAssignment(lines, backupLines, "model_provider");
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
        var firstSection = FindNextSectionHeader(lines, 0);
        var searchEnd = firstSection < 0 ? lines.Count : firstSection;
        var index = FindKeyAssignment(lines, key, 0, searchEnd);
        if (index < 0)
        {
            return false;
        }

        var equalsIndex = lines[index].IndexOf('=');
        if (equalsIndex < 0)
        {
            return false;
        }

        var value = lines[index][(equalsIndex + 1)..].Trim().Trim('"', '\'');
        return string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase);
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
    string PreviewText);

internal sealed record TransparentProxyCodexConfigMutationResult(
    bool Succeeded,
    string Summary,
    string ConfigPath,
    string? BackupPath);
