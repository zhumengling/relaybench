using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class CodexFamilyConfigApplyService
{
    private const string CodexProviderKey = "custom";
    private const string CodexProviderName = "Custom OpenAI-Compatible";
    private const string CodexWireApi = "responses";

    private readonly IClientApiConfigMutationEnvironment _environment;

    public CodexFamilyConfigApplyService(IClientApiConfigMutationEnvironment? environment = null)
    {
        _environment = environment ?? new ClientApiDiagnosticEnvironment();
    }

    public Task<ClientAppApplyResult> ApplyAsync(
        string baseUrl,
        string apiKey,
        string model,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedBaseUrl = NormalizeCodexBaseUrl(baseUrl);
        var normalizedApiKey = apiKey?.Trim() ?? string.Empty;
        var normalizedModel = model?.Trim() ?? string.Empty;
        var normalizedDisplayName = NormalizeCodexProviderName(displayName);

        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
        {
            return Task.FromResult(new ClientAppApplyResult(
                false,
                "应用失败：当前入口缺少可写入 Codex 的 BaseUrl。",
                [],
                [],
                Array.Empty<string>(),
                "missing-base-url"));
        }

        if (string.IsNullOrWhiteSpace(normalizedApiKey))
        {
            return Task.FromResult(new ClientAppApplyResult(
                false,
                "应用失败：当前入口缺少可写入 Codex 的 API Key。",
                [],
                [],
                Array.Empty<string>(),
                "missing-api-key"));
        }

        if (string.IsNullOrWhiteSpace(normalizedModel))
        {
            return Task.FromResult(new ClientAppApplyResult(
                false,
                "应用失败：当前入口缺少模型，暂时无法写入 Codex 配置。",
                [],
                [],
                Array.Empty<string>(),
                "missing-model"));
        }

        var codexRoot = Path.Combine(_environment.UserProfilePath, ".codex");
        var configPath = Path.Combine(codexRoot, "config.toml");
        var authPath = Path.Combine(codexRoot, "auth.json");
        var settingsPath = Path.Combine(codexRoot, "settings.json");

        _environment.EnsureDirectoryExists(codexRoot);
        CodexRestoreStateStorage.EnsureOriginalStateCaptured(_environment, configPath, authPath, settingsPath);

        List<string> changedFiles = [];
        List<string> backupFiles = [];

        ApplyFile(
            configPath,
            existing => UpsertCodexConfig(existing, normalizedBaseUrl, normalizedModel, normalizedApiKey, normalizedDisplayName),
            changedFiles,
            backupFiles);
        ApplyFile(
            authPath,
            existing => UpsertCodexAuth(existing, normalizedApiKey),
            changedFiles,
            backupFiles);

        IReadOnlyList<string> appliedTargets = ["Codex CLI", "Codex Desktop", "VSCode Codex"];

        if (changedFiles.Count == 0)
        {
            return Task.FromResult(new ClientAppApplyResult(
                true,
                "Codex CLI / Codex Desktop / VSCode Codex 共用配置已与当前入口一致，无需改动。",
                [],
                [],
                appliedTargets,
                null));
        }

        return Task.FromResult(new ClientAppApplyResult(
            true,
            $"已将当前入口应用到 Codex CLI / Codex Desktop / VSCode Codex 共用配置，共处理 {changedFiles.Count} 个文件。",
            changedFiles,
            backupFiles,
            appliedTargets,
            null));
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
        }

        _environment.WriteFileText(path, updated);
        changedFiles.Add(path);
    }

    private static string UpsertCodexAuth(string existingContent, string apiKey)
    {
        var root = ClientApiConfigPatterns.TryParseJsonObject(existingContent) ?? new JsonObject();
        root["auth_mode"] = "apikey";
        root["OPENAI_API_KEY"] = apiKey;

        return ClientApiConfigPatterns.SerializeJson(root);
    }

    private static string UpsertCodexConfig(
        string existingContent,
        string baseUrl,
        string model,
        string apiKey,
        string displayName)
    {
        List<string> lines = SplitLines(existingContent);

        UpsertTopLevelString(lines, "model_provider", CodexProviderKey);
        UpsertTopLevelString(lines, "model", model);
        UpsertSectionString(lines, "model_providers.custom", "name", displayName);
        UpsertSectionString(lines, "model_providers.custom", "base_url", baseUrl);
        UpsertSectionString(lines, "model_providers.custom", "wire_api", CodexWireApi);
        UpsertSectionString(lines, "model_providers.custom", "experimental_bearer_token", apiKey);

        return JoinLines(lines);
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

    private static void UpsertSectionString(List<string> lines, string sectionName, string key, string value)
    {
        var header = $"[{sectionName}]";
        var lineContent = $"{key} = {SerializeTomlString(value)}";
        var sectionIndex = FindSectionHeader(lines, header);

        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add(header);
            lines.Add(lineContent);
            return;
        }

        var sectionEnd = FindNextSectionHeader(lines, sectionIndex + 1);
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        var existingIndex = FindKeyAssignment(lines, key, sectionIndex + 1, sectionEnd);
        if (existingIndex >= 0)
        {
            lines[existingIndex] = lineContent;
            return;
        }

        lines.Insert(sectionEnd, lineContent);
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

    private static string NormalizeCodexBaseUrl(string? value)
    {
        var normalized = ClientApiConfigPatterns.NormalizeEndpoint(value);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return string.Empty;
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

    private static string NormalizeCodexProviderName(string? value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(ch => !char.IsControl(ch))
            .ToArray()).Trim();

        return string.IsNullOrWhiteSpace(normalized)
            ? CodexProviderName
            : normalized;
    }
}
