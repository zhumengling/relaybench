using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class CodexFamilyConfigApplyService
{
    private const string CodexProviderKey = "relaybench";
    private const string CodexProviderName = "RelayBench";
    private const string CodexResponsesWireApi = "responses";
    private const string CodexDefaultHttpHeaders = "{ \"Content-Type\" = \"application/json\" }";
    private const string CodexOpenAiBaseUrlKey = "openai_base_url";
    private const string CodexProfileSection = "profiles.relaybench";

    private readonly IClientApiConfigMutationEnvironment _environment;

    public CodexFamilyConfigApplyService(IClientApiConfigMutationEnvironment? environment = null)
    {
        _environment = environment ?? new ClientApiDiagnosticEnvironment();
    }

    public static CodexConfigTemplate CreateDefaultTemplate(
        string? baseUrl,
        string? apiKey,
        string? model,
        string? displayName = null,
        int? modelContextWindow = null,
        string? preferredWireApi = null)
    {
        var normalizedModel = model?.Trim() ?? string.Empty;
        var normalizedBaseUrl = NormalizeCodexBaseUrl(baseUrl);
        var resolvedContextWindow = ModelContextWindowCatalog.ResolveContextWindow(normalizedModel, modelContextWindow);
        var autoCompactTokenLimit = ModelContextWindowCatalog.CalculateAutoCompactTokenLimit(resolvedContextWindow);
        var wireApi = ResolveCodexWireApiPreference(normalizedBaseUrl, normalizedModel, preferredWireApi);

        return new CodexConfigTemplate(
            normalizedModel,
            CodexProviderKey,
            resolvedContextWindow,
            autoCompactTokenLimit,
            NormalizeCodexProviderName(displayName, normalizedModel),
            normalizedBaseUrl,
            wireApi,
            apiKey?.Trim() ?? string.Empty,
            CodexDefaultHttpHeaders,
            RequestMaxRetries: null,
            StreamMaxRetries: null,
            StreamIdleTimeoutMs: null,
            AdditionalRawSettings: null);
    }

    public Task<ClientAppApplyResult> ApplyAsync(
        string baseUrl,
        string apiKey,
        string model,
        string? displayName = null,
        int? modelContextWindow = null,
        string? preferredWireApi = null,
        IReadOnlyList<ClientApplyTargetSelection>? targetSelections = null,
        CodexConfigTemplate? configTemplate = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var template = NormalizeTemplate(
            configTemplate,
            CreateDefaultTemplate(baseUrl, apiKey, model, displayName, modelContextWindow, preferredWireApi));
        var normalizedBaseUrl = NormalizeCodexBaseUrl(template.BaseUrl);
        var normalizedApiKey = template.ExperimentalBearerToken.Trim();
        var normalizedModel = template.Model.Trim();
        var normalizedDisplayName = NormalizeCodexProviderName(template.ProviderName, normalizedModel);
        var resolvedContextWindow = template.ModelContextWindow;
        var autoCompactTokenLimit = template.ModelAutoCompactTokenLimit;
        var wireApi = ResolveCodexWireApiPreference(normalizedBaseUrl, normalizedModel, template.WireApi);
        var useOpenAiBaseUrlMode = ShouldUseOpenAiBaseUrlMode(normalizedBaseUrl);

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

        if (!useOpenAiBaseUrlMode && string.IsNullOrWhiteSpace(normalizedApiKey))
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
                wireApi,
                template,
                useOpenAiBaseUrlMode),
            changedFiles,
            backupFiles);
        ApplyFile(
            authPath,
            RemoveCodexApiKeyAuthOverrides,
            changedFiles,
            backupFiles);

        IReadOnlyList<ClientAppTargetApplyResult> targetResults = BuildCodexTargetResults(
            targetSelections,
            true,
            changedFiles,
            backupFiles,
            null);
        IReadOnlyList<string> appliedTargets = targetResults.Select(target => target.DisplayName).ToArray();

        if (changedFiles.Count == 0)
        {
            return Task.FromResult(new ClientAppApplyResult(
                true,
                $"{FormatAppliedTargets(appliedTargets)} 共用配置已与当前入口一致，无需改动。",
                [],
                [],
                appliedTargets,
                null)
            {
                TargetResults = targetResults
            });
        }

        return Task.FromResult(new ClientAppApplyResult(
            true,
            $"已将当前入口应用到 {FormatAppliedTargets(appliedTargets)} 共用配置，共处理 {changedFiles.Count} 个文件。",
            changedFiles,
            backupFiles,
            appliedTargets,
            null)
        {
            TargetResults = targetResults
        });
    }

    private static IReadOnlyList<ClientAppTargetApplyResult> BuildCodexTargetResults(
        IReadOnlyList<ClientApplyTargetSelection>? targetSelections,
        bool succeeded,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> backupFiles,
        string? error)
    {
        var selectedProtocols = targetSelections?
            .Where(target => target.Protocol == ClientApplyProtocolKind.Responses)
            .GroupBy(target => target.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Protocol,
                StringComparer.OrdinalIgnoreCase);

        if (selectedProtocols is { Count: > 0 } &&
            !selectedProtocols.ContainsKey("codex") &&
            !selectedProtocols.Keys.Any(static id =>
                string.Equals(id, "codex-cli", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "codex-desktop", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "vscode-codex", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        return
        [
            new ClientAppTargetApplyResult(
                "codex",
                "Codex",
                ClientApplyProtocolKind.Responses,
                succeeded,
                changedFiles,
                backupFiles,
                error)
        ];
    }

    private static string FormatAppliedTargets(IReadOnlyList<string> appliedTargets)
        => appliedTargets.Count == 0
            ? "Codex"
            : string.Join(" / ", appliedTargets);

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

    private static string RemoveCodexApiKeyAuthOverrides(string existingContent)
    {
        if (string.IsNullOrWhiteSpace(existingContent))
        {
            return existingContent;
        }

        var root = ClientApiConfigPatterns.TryParseJsonObject(existingContent) ?? new JsonObject();
        if (root.TryGetPropertyValue("auth_mode", out var authModeNode) &&
            authModeNode is JsonValue authModeValue &&
            authModeValue.TryGetValue<string>(out var authMode) &&
            string.Equals(authMode, "apikey", StringComparison.OrdinalIgnoreCase))
        {
            root.Remove("auth_mode");
        }

        root.Remove("OPENAI_API_KEY");

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
        string wireApi,
        CodexConfigTemplate template,
        bool useOpenAiBaseUrlMode)
    {
        List<string> lines = SplitLines(existingContent);

        RemoveLegacyRelayBenchCustomProvider(lines);
        UpsertTopLevelString(lines, "model", model);
        UpsertOptionalTopLevelInteger(lines, "model_context_window", contextWindow);
        UpsertOptionalTopLevelInteger(lines, "model_auto_compact_token_limit", autoCompactTokenLimit);
        if (useOpenAiBaseUrlMode)
        {
            RemoveRelayBenchProviderProfile(lines);
            UpsertTopLevelString(lines, CodexOpenAiBaseUrlKey, baseUrl);
            ApplyAdditionalTemplateSettings(lines, template.AdditionalRawSettings);
            return JoinLines(lines);
        }

        var providerSection = $"model_providers.{CodexProviderKey}";
        UpsertTopLevelString(lines, "model_provider", CodexProviderKey);
        UpsertSectionString(lines, providerSection, "name", displayName);
        UpsertSectionString(lines, providerSection, "base_url", baseUrl);
        UpsertSectionString(lines, providerSection, "wire_api", wireApi);
        UpsertSectionRawValue(
            lines,
            providerSection,
            "http_headers",
            NormalizeHttpHeaders(template.HttpHeaders));
        UpsertSectionString(lines, providerSection, "experimental_bearer_token", apiKey);
        UpsertOptionalSectionInteger(lines, providerSection, "request_max_retries", template.RequestMaxRetries);
        UpsertOptionalSectionInteger(lines, providerSection, "stream_max_retries", template.StreamMaxRetries);
        UpsertOptionalSectionInteger(lines, providerSection, "stream_idle_timeout_ms", template.StreamIdleTimeoutMs);
        ApplyAdditionalTemplateSettings(lines, template.AdditionalRawSettings);

        return JoinLines(lines);
    }

    private static CodexConfigTemplate NormalizeTemplate(CodexConfigTemplate? template, CodexConfigTemplate fallback)
    {
        if (template is null)
        {
            return fallback;
        }

        var model = string.IsNullOrWhiteSpace(template.Model) ? fallback.Model : template.Model.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(template.BaseUrl) ? fallback.BaseUrl : template.BaseUrl.Trim();
        var apiKey = string.IsNullOrWhiteSpace(template.ExperimentalBearerToken)
            ? fallback.ExperimentalBearerToken
            : template.ExperimentalBearerToken.Trim();
        var providerName = string.IsNullOrWhiteSpace(template.ProviderName)
            ? fallback.ProviderName
            : template.ProviderName.Trim();

        return template with
        {
            Model = model,
            ModelProvider = CodexProviderKey,
            ProviderName = providerName,
            BaseUrl = baseUrl,
            WireApi = ResolveCodexWireApiPreference(baseUrl, model, template.WireApi),
            ExperimentalBearerToken = apiKey,
            HttpHeaders = NormalizeHttpHeaders(template.HttpHeaders),
            AdditionalRawSettings = template.AdditionalRawSettings
        };
    }

    private static string NormalizeHttpHeaders(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? CodexDefaultHttpHeaders
            : value.Trim();

    private static void RemoveLegacyRelayBenchCustomProvider(List<string> lines)
    {
        var sectionIndex = FindSectionHeader(lines, "[model_providers.custom]");
        if (sectionIndex < 0)
        {
            return;
        }

        var sectionEnd = FindNextSectionHeader(lines, sectionIndex + 1);
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        if (!SectionContainsRelayBenchProviderName(lines, sectionIndex + 1, sectionEnd))
        {
            return;
        }

        lines.RemoveRange(sectionIndex, sectionEnd - sectionIndex);
        while (sectionIndex < lines.Count &&
               sectionIndex > 0 &&
               string.IsNullOrWhiteSpace(lines[sectionIndex]) &&
               string.IsNullOrWhiteSpace(lines[sectionIndex - 1]))
        {
            lines.RemoveAt(sectionIndex);
        }
    }

    private static bool SectionContainsRelayBenchProviderName(List<string> lines, int startIndex, int endExclusive)
    {
        var nameIndex = FindKeyAssignment(lines, "name", startIndex, endExclusive);
        if (nameIndex < 0)
        {
            return false;
        }

        return lines[nameIndex].Contains("RelayBench", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveRelayBenchProviderProfile(List<string> lines)
    {
        RemoveSection(lines, $"[model_providers.{CodexProviderKey}]");
        RemoveSection(lines, $"[{CodexProfileSection}]");
        RemoveTopLevelStringAssignmentIfValue(lines, "model_provider", CodexProviderKey);
        RemoveTopLevelStringAssignmentIfValue(lines, "profile", CodexProviderKey);
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

    private static void RemoveTopLevelStringAssignmentIfValue(List<string> lines, string key, string expectedValue)
    {
        var firstSectionIndex = FindNextSectionHeader(lines, 0);
        var searchEnd = firstSectionIndex < 0 ? lines.Count : firstSectionIndex;
        var existingIndex = FindKeyAssignment(lines, key, 0, searchEnd);
        if (existingIndex < 0)
        {
            return;
        }

        var currentValue = ParseAssignmentValue(lines[existingIndex]);
        if (string.Equals(currentValue, expectedValue, StringComparison.OrdinalIgnoreCase))
        {
            lines.RemoveAt(existingIndex);
        }
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

    private static void UpsertTopLevelRawValue(List<string> lines, string key, string rawValue)
    {
        var lineContent = $"{key} = {rawValue}";
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

    private static void UpsertOptionalSectionInteger(List<string> lines, string sectionName, string key, int? value)
    {
        if (value is null)
        {
            RemoveSectionAssignment(lines, sectionName, key);
            return;
        }

        UpsertSectionRawValue(lines, sectionName, key, value.Value.ToString());
    }

    private static void UpsertSectionString(List<string> lines, string sectionName, string key, string value)
        => UpsertSectionRawValue(lines, sectionName, key, SerializeTomlString(value));

    private static void UpsertSectionRawValue(List<string> lines, string sectionName, string key, string rawValue)
    {
        var header = $"[{sectionName}]";
        var lineContent = $"{key} = {rawValue}";
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

    private static void RemoveSectionAssignment(List<string> lines, string sectionName, string key)
    {
        var header = $"[{sectionName}]";
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

        var existingIndex = FindKeyAssignment(lines, key, sectionIndex + 1, sectionEnd);
        if (existingIndex >= 0)
        {
            lines.RemoveAt(existingIndex);
        }
    }

    private static void ApplyAdditionalTemplateSettings(
        List<string> lines,
        IReadOnlyDictionary<string, string>? additionalRawSettings)
    {
        if (additionalRawSettings is null || additionalRawSettings.Count == 0)
        {
            return;
        }

        foreach (var (path, rawValue) in additionalRawSettings)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rawValue) ||
                IsBuiltInTemplatePath(path))
            {
                continue;
            }

            var splitIndex = FindLastUnquotedDot(path);
            if (splitIndex <= 0 || splitIndex >= path.Length - 1)
            {
                UpsertTopLevelRawValue(lines, path.Trim(), rawValue.Trim());
                continue;
            }

            var sectionName = path[..splitIndex].Trim();
            var key = path[(splitIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(sectionName) || string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            UpsertSectionRawValue(lines, sectionName, key, rawValue.Trim());
        }
    }

    private static bool IsBuiltInTemplatePath(string path)
        => path is
            "openai_base_url" or
            "model" or
            "model_provider" or
            "profile" or
            "model_context_window" or
            "model_auto_compact_token_limit" or
            "profiles.relaybench.model" or
            "profiles.relaybench.model_provider" or
            "model_providers.relaybench.name" or
            "model_providers.relaybench.base_url" or
            "model_providers.relaybench.wire_api" or
            "model_providers.relaybench.experimental_bearer_token" or
            "model_providers.relaybench.http_headers" or
            "model_providers.relaybench.request_max_retries" or
            "model_providers.relaybench.stream_max_retries" or
            "model_providers.relaybench.stream_idle_timeout_ms";

    private static int FindLastUnquotedDot(string value)
    {
        var inQuote = false;
        var escaped = false;
        var lastDot = -1;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (ch == '.' && !inQuote)
            {
                lastDot = index;
            }
        }

        return lastDot;
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

        return string.IsNullOrWhiteSpace(value) || IsOpenAiProviderName(value)
            ? CodexProviderName
            : value.Trim();
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

    public static string ResolveCodexWireApiPreference(
        string? baseUrl,
        string? model,
        string? preferredWireApi = null)
    {
        return CodexResponsesWireApi;
    }

    private static bool IsDeepSeekModel(string model)
        => model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldUseOpenAiBaseUrlMode(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
    }

}
