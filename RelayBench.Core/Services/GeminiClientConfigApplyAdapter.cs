using System.Text;
using System.Text.Json.Nodes;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class GeminiClientConfigApplyAdapter
{
    private readonly IClientApiConfigMutationEnvironment _environment;

    public GeminiClientConfigApplyAdapter(IClientApiConfigMutationEnvironment? environment = null)
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
            .Where(target => target.Protocol == ClientApplyProtocolKind.Gemini &&
                             string.Equals(target.TargetId, "antigravity", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length == 0)
        {
            return Task.FromResult(new ClientAppApplyResult(
                true,
                "未选择 Gemini / Antigravity 客户端，跳过写入。",
                [],
                [],
                [],
                null)
            {
                TargetResults = []
            });
        }

        var normalizedBaseUrl = NormalizeGeminiBaseUrl(endpoint.BaseUrl);
        var normalizedApiKey = endpoint.ApiKey?.Trim() ?? string.Empty;
        var normalizedModel = endpoint.Model?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl) ||
            string.IsNullOrWhiteSpace(normalizedApiKey) ||
            string.IsNullOrWhiteSpace(normalizedModel))
        {
            var error = "Gemini / Antigravity 写入失败：当前入口缺少地址、密钥或模型。";
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
                        "antigravity",
                        "Antigravity",
                        ClientApplyProtocolKind.Gemini,
                        false,
                        [],
                        [],
                        error)
                ]
            });
        }

        var geminiRoot = Path.Combine(_environment.UserProfilePath, ".gemini");
        var envPath = Path.Combine(geminiRoot, ".env");
        var settingsPath = Path.Combine(geminiRoot, "settings.json");
        _environment.EnsureDirectoryExists(geminiRoot);
        RelayBenchBackupRetention.PruneAllUnderDirectory(_environment, geminiRoot);

        List<string> changedFiles = [];
        List<string> backupFiles = [];
        ApplyFile(
            envPath,
            existing => UpsertGeminiEnv(existing, normalizedBaseUrl, normalizedApiKey, normalizedModel),
            changedFiles,
            backupFiles);
        ApplyFile(
            settingsPath,
            UpsertGeminiSettings,
            changedFiles,
            backupFiles);

        return Task.FromResult(new ClientAppApplyResult(
            true,
            BuildSummary(changedFiles.Count),
            changedFiles,
            backupFiles,
            ["Antigravity"],
            null)
        {
            TargetResults =
            [
                new ClientAppTargetApplyResult(
                    "antigravity",
                    "Antigravity",
                    ClientApplyProtocolKind.Gemini,
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

    private static string UpsertGeminiEnv(
        string existingContent,
        string baseUrl,
        string apiKey,
        string model)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GOOGLE_GEMINI_BASE_URL"] = baseUrl,
            ["GEMINI_API_KEY"] = apiKey,
            ["GEMINI_MODEL"] = model
        };
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        var normalized = existingContent.Replace("\r\n", "\n", StringComparison.Ordinal);

        foreach (var line in normalized.Split('\n'))
        {
            if (string.IsNullOrEmpty(line) && builder.Length == normalized.Length)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            var assignmentIndex = trimmed.IndexOf('=');
            if (assignmentIndex > 0)
            {
                var key = trimmed[..assignmentIndex].Trim();
                if (values.TryGetValue(key, out var value))
                {
                    builder.Append(key).Append('=').Append(value).AppendLine();
                    written.Add(key);
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(line))
            {
                builder.Append(line).AppendLine();
            }
        }

        foreach (var (key, value) in values)
        {
            if (!written.Contains(key))
            {
                builder.Append(key).Append('=').Append(value).AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string UpsertGeminiSettings(string existingContent)
    {
        var root = ClientApiConfigPatterns.TryParseJsonObject(existingContent) ?? new JsonObject();
        var security = root["security"] as JsonObject;
        if (security is null)
        {
            security = [];
            root["security"] = security;
        }

        var auth = security["auth"] as JsonObject;
        if (auth is null)
        {
            auth = [];
            security["auth"] = auth;
        }

        auth["selectedType"] = "gemini-api-key";
        return ClientApiConfigPatterns.SerializeJson(root);
    }

    private static string BuildSummary(int changedFileCount)
        => changedFileCount == 0
            ? "Antigravity / Gemini 配置已与当前本地代理入口一致，无需改动。"
            : $"已将 Antigravity / Gemini 环境指向 RelayBench 本地代理入口，共处理 {changedFileCount} 个文件。";

    private static string NormalizeGeminiBaseUrl(string? value)
    {
        var normalized = ClientApiConfigPatterns.NormalizeEndpoint(value);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return normalized;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/v1beta/models", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/v1beta/models".Length].TrimEnd('/');
        }
        else if (path.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/v1beta".Length].TrimEnd('/');
        }
        else if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/v1".Length].TrimEnd('/');
        }

        builder.Path = string.IsNullOrWhiteSpace(path) ? "/" : path;
        return builder.Uri.ToString().TrimEnd('/');
    }
}
