using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class CodexFamilyConfigApplyService
{
    private const string CodexProviderKey = "custom";
    private const string CodexProviderName = "OpenAI";
    private const string CodexResponsesWireApi = "responses";
    private const string CodexChatWireApi = "chat";

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
        int? modelContextWindow = null,
        string? preferredWireApi = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedBaseUrl = NormalizeCodexBaseUrl(baseUrl);
        var normalizedApiKey = apiKey?.Trim() ?? string.Empty;
        var normalizedModel = model?.Trim() ?? string.Empty;
        var normalizedDisplayName = NormalizeCodexProviderName(displayName, normalizedModel);
        var resolvedContextWindow = ModelContextWindowCatalog.ResolveContextWindow(normalizedModel, modelContextWindow);
        var autoCompactTokenLimit = ModelContextWindowCatalog.CalculateAutoCompactTokenLimit(resolvedContextWindow);
        var wireApi = ResolveCodexWireApiPreference(normalizedBaseUrl, normalizedModel, preferredWireApi);

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
        RelayBenchBackupRetention.PruneAllUnderDirectory(_environment, codexRoot);

        List<string> changedFiles = [];
        List<string> backupFiles = [];

        ApplyFile(
            configPath,
            existing => UpsertCodexConfig(
                existing,
                normalizedBaseUrl,
                normalizedModel,
                normalizedApiKey,
                normalizedDisplayName,
                resolvedContextWindow,
                autoCompactTokenLimit,
                wireApi),
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
            RelayBenchBackupRetention.PruneForOriginalFile(_environment, path);
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
        string displayName,
        int? contextWindow,
        int? autoCompactTokenLimit,
        string wireApi)
    {
        List<string> lines = SplitLines(existingContent);

        UpsertTopLevelString(lines, "model_provider", CodexProviderKey);
        UpsertTopLevelString(lines, "model", model);
        UpsertOptionalTopLevelInteger(lines, "model_context_window", contextWindow);
        UpsertOptionalTopLevelInteger(lines, "model_auto_compact_token_limit", autoCompactTokenLimit);
        if (string.Equals(wireApi, CodexChatWireApi, StringComparison.Ordinal))
        {
            RemoveTopLevelAssignment(lines, "model_reasoning_effort");
            RemoveTopLevelAssignment(lines, "model_reasoning_summary");
        }

        UpsertSectionString(lines, "model_providers.custom", "name", displayName);
        UpsertSectionString(lines, "model_providers.custom", "base_url", baseUrl);
        UpsertSectionString(lines, "model_providers.custom", "wire_api", wireApi);
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

    private static void UpsertOptionalTopLevelInteger(List<string> lines, string key, int? value)
    {
        if (value is null)
        {
            RemoveTopLevelAssignment(lines, key);
            return;
        }

        var lineContent = $"{key} = {value.Value}";
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

    private static void RemoveTopLevelAssignment(List<string> lines, string key)
    {
        var firstSectionIndex = FindNextSectionHeader(lines, 0);
        var searchEnd = firstSectionIndex < 0 ? lines.Count : firstSectionIndex;
        var existingIndex = FindKeyAssignment(lines, key, 0, searchEnd);
        if (existingIndex >= 0)
        {
            lines.RemoveAt(existingIndex);
        }
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

    private static string NormalizeCodexProviderName(string? value, string model)
    {
        if (IsDeepSeekModel(model))
        {
            return "DeepSeek";
        }

        return string.IsNullOrWhiteSpace(value)
            ? CodexProviderName
            : value.Trim();
    }

    public static string ResolveCodexWireApiPreference(
        string? baseUrl,
        string? model,
        string? preferredWireApi = null)
    {
        if (TryNormalizeCodexWireApi(preferredWireApi, out var normalizedPreferredWireApi))
        {
            return normalizedPreferredWireApi;
        }

        return IsChatPreferredModel(model) || IsChatPreferredEndpoint(baseUrl)
            ? CodexChatWireApi
            : CodexResponsesWireApi;
    }

    private static bool IsDeepSeekModel(string model)
        => model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeCodexWireApi(string? value, out string normalized)
    {
        normalized = string.Empty;
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (string.Equals(text, CodexChatWireApi, StringComparison.OrdinalIgnoreCase))
        {
            normalized = CodexChatWireApi;
            return true;
        }

        if (string.Equals(text, CodexResponsesWireApi, StringComparison.OrdinalIgnoreCase))
        {
            normalized = CodexResponsesWireApi;
            return true;
        }

        return false;
    }

    private static bool IsChatPreferredModel(string? model)
    {
        var normalized = model?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        string[] markers =
        [
            "deepseek",
            "qwen",
            "qwq",
            "qvq",
            "kimi",
            "moonshot",
            "glm",
            "chatglm",
            "zhipu",
            "yi-",
            "yi_",
            "baichuan",
            "minimax",
            "abab",
            "doubao",
            "hunyuan",
            "ernie",
            "wenxin",
            "spark",
            "xunfei",
            "step-",
            "step_",
            "internlm",
            "sensechat",
            "telechat"
        ];

        return markers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsChatPreferredEndpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(ClientApiConfigPatterns.NormalizeEndpoint(baseUrl), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var signature = $"{uri.Host}{uri.AbsolutePath}".ToLowerInvariant();
        string[] markers =
        [
            "dashscope.aliyuncs.com",
            "bigmodel.cn",
            "moonshot.cn",
            "deepseek.com",
            "volces.com",
            "volcengineapi.com",
            "siliconflow.cn",
            "minimax.chat",
            "baichuan-ai.com",
            "lingyiwanwu.com",
            "hunyuan.cloud.tencent.com",
            "xf-yun.com",
            "sensenova.cn",
            "stepfun.com",
            "compatible-mode"
        ];

        return markers.Any(marker => signature.Contains(marker, StringComparison.Ordinal));
    }
}
