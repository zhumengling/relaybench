using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public TransparentProxyConfigStore()
        : this(RelayBenchPaths.RootDirectory)
    {
    }

    public TransparentProxyConfigStore(string rootDirectory)
    {
        var configDirectory = Path.Combine(Path.GetFullPath(rootDirectory), "config");
        Directory.CreateDirectory(configDirectory);
        _filePath = Path.Combine(configDirectory, "transparent-proxy.json");
    }

    public TransparentProxyConfigSnapshot Load(AppStateSnapshot? legacySnapshot = null)
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                var snapshot = DeserializeWithUnprotectedSecrets(json);
                if (snapshot is not null)
                {
                    return Normalize(snapshot, legacySnapshot);
                }
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxyConfigStore.Load", ex);
        }

        return FromAppState(legacySnapshot);
    }

    public void Save(TransparentProxyConfigSnapshot snapshot)
    {
        try
        {
            var normalized = Normalize(snapshot, null);
            var json = SerializeWithProtectedSecrets(normalized);
            WriteAllTextAtomically(_filePath, json);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("TransparentProxyConfigStore.Save", ex);
        }
    }

    public static TransparentProxyConfigSnapshot FromAppState(AppStateSnapshot? snapshot)
        => Normalize(new TransparentProxyConfigSnapshot
        {
            PortText = snapshot?.TransparentProxyPortText ?? "17880",
            RoutesText = snapshot?.TransparentProxyRoutesText ?? string.Empty,
            RateLimitPerMinuteText = snapshot?.TransparentProxyRateLimitPerMinuteText ?? "60",
            MaxConcurrencyText = snapshot?.TransparentProxyMaxConcurrencyText ?? "8",
            RouteStrategyKey = snapshot?.TransparentProxyRouteStrategyKey ?? TransparentProxyRouteStrategies.Smart,
            EnableFallback = snapshot?.TransparentProxyEnableFallback ?? true,
            EnableCache = snapshot?.TransparentProxyEnableCache ?? true,
            CacheTtlSecondsText = snapshot?.TransparentProxyCacheTtlSecondsText ?? "60",
            RequestRetryText = snapshot?.TransparentProxyRequestRetryText ?? "1",
            MaxRetryIntervalSecondsText = snapshot?.TransparentProxyMaxRetryIntervalSecondsText ?? "8",
            SessionAffinityTtlSecondsText = snapshot?.TransparentProxySessionAffinityTtlSecondsText ?? "1800",
            ModelCooldownSecondsText = snapshot?.TransparentProxyModelCooldownSecondsText ?? "120",
            RewriteModel = snapshot?.TransparentProxyRewriteModel ?? false
        }, null);

    public static void ApplyToAppState(TransparentProxyConfigSnapshot config, AppStateSnapshot snapshot)
    {
        var normalized = Normalize(config, null);
        snapshot.TransparentProxyPortText = normalized.PortText;
        snapshot.TransparentProxyRoutesText = normalized.RoutesText;
        snapshot.TransparentProxyRateLimitPerMinuteText = normalized.RateLimitPerMinuteText;
        snapshot.TransparentProxyMaxConcurrencyText = normalized.MaxConcurrencyText;
        snapshot.TransparentProxyRouteStrategyKey = normalized.RouteStrategyKey;
        snapshot.TransparentProxyEnableFallback = normalized.EnableFallback;
        snapshot.TransparentProxyEnableCache = normalized.EnableCache;
        snapshot.TransparentProxyCacheTtlSecondsText = normalized.CacheTtlSecondsText;
        snapshot.TransparentProxyRequestRetryText = normalized.RequestRetryText;
        snapshot.TransparentProxyMaxRetryIntervalSecondsText = normalized.MaxRetryIntervalSecondsText;
        snapshot.TransparentProxySessionAffinityTtlSecondsText = normalized.SessionAffinityTtlSecondsText;
        snapshot.TransparentProxyModelCooldownSecondsText = normalized.ModelCooldownSecondsText;
        snapshot.TransparentProxyRewriteModel = normalized.RewriteModel;
    }

    private static TransparentProxyConfigSnapshot Normalize(
        TransparentProxyConfigSnapshot snapshot,
        AppStateSnapshot? legacySnapshot)
    {
        var legacy = legacySnapshot is null ? null : FromAppState(legacySnapshot);
        return new TransparentProxyConfigSnapshot
        {
            PortText = Coalesce(snapshot.PortText, legacy?.PortText, "17880"),
            RoutesText = string.IsNullOrWhiteSpace(snapshot.RoutesText)
                ? legacy?.RoutesText ?? string.Empty
                : snapshot.RoutesText,
            RateLimitPerMinuteText = Coalesce(snapshot.RateLimitPerMinuteText, legacy?.RateLimitPerMinuteText, "60"),
            MaxConcurrencyText = Coalesce(snapshot.MaxConcurrencyText, legacy?.MaxConcurrencyText, "8"),
            RouteStrategyKey = TransparentProxyRouteStrategies.Normalize(
                Coalesce(snapshot.RouteStrategyKey, legacy?.RouteStrategyKey, TransparentProxyRouteStrategies.Smart)),
            EnableFallback = snapshot.EnableFallback,
            EnableCache = snapshot.EnableCache,
            CacheTtlSecondsText = Coalesce(snapshot.CacheTtlSecondsText, legacy?.CacheTtlSecondsText, "60"),
            RequestRetryText = Coalesce(snapshot.RequestRetryText, legacy?.RequestRetryText, "1"),
            MaxRetryIntervalSecondsText = Coalesce(snapshot.MaxRetryIntervalSecondsText, legacy?.MaxRetryIntervalSecondsText, "8"),
            SessionAffinityTtlSecondsText = Coalesce(snapshot.SessionAffinityTtlSecondsText, legacy?.SessionAffinityTtlSecondsText, "1800"),
            ModelCooldownSecondsText = Coalesce(snapshot.ModelCooldownSecondsText, legacy?.ModelCooldownSecondsText, "120"),
            RewriteModel = snapshot.RewriteModel
        };
    }

    private static string Coalesce(string? value, string? fallback, string defaultValue)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : !string.IsNullOrWhiteSpace(fallback)
                ? fallback
                : defaultValue;

    private static string SerializeWithProtectedSecrets(TransparentProxyConfigSnapshot value)
    {
        var node = JsonSerializer.SerializeToNode(value, SerializerOptions);
        ProtectSecretNodes(node);
        return node?.ToJsonString(SerializerOptions) ?? "{}";
    }

    private static TransparentProxyConfigSnapshot? DeserializeWithUnprotectedSecrets(string json)
    {
        var node = JsonNode.Parse(json);
        UnprotectSecretNodes(node);
        return node is null ? null : node.Deserialize<TransparentProxyConfigSnapshot>(SerializerOptions);
    }

    private static void ProtectSecretNodes(JsonNode? node)
        => TransformSecretNodes(node, SecretProtector.Protect);

    private static void UnprotectSecretNodes(JsonNode? node)
        => TransformSecretNodes(node, SecretProtector.Unprotect);

    private static void TransformSecretNodes(JsonNode? node, Func<string?, string> transform)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToArray())
                {
                    if (string.Equals(property.Key, nameof(TransparentProxyConfigSnapshot.RoutesText), StringComparison.Ordinal))
                    {
                        jsonObject[property.Key] = property.Value?.GetValue<string>() is { } secret
                            ? transform(secret)
                            : property.Value;
                        continue;
                    }

                    TransformSecretNodes(property.Value, transform);
                }

                break;
            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    TransformSecretNodes(item, transform);
                }

                break;
        }
    }

    private static void WriteAllTextAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, content, Encoding.UTF8);

        if (File.Exists(path))
        {
            File.Replace(temporaryPath, path, null);
            return;
        }

        File.Move(temporaryPath, path);
    }
}

internal sealed class TransparentProxyConfigSnapshot
{
    public string PortText { get; set; } = "17880";

    public string RoutesText { get; set; } = string.Empty;

    public string RateLimitPerMinuteText { get; set; } = "60";

    public string MaxConcurrencyText { get; set; } = "8";

    public string RouteStrategyKey { get; set; } = TransparentProxyRouteStrategies.Smart;

    public bool EnableFallback { get; set; } = true;

    public bool EnableCache { get; set; } = true;

    public string CacheTtlSecondsText { get; set; } = "60";

    public string RequestRetryText { get; set; } = "1";

    public string MaxRetryIntervalSecondsText { get; set; } = "8";

    public string SessionAffinityTtlSecondsText { get; set; } = "1800";

    public string ModelCooldownSecondsText { get; set; } = "120";

    public bool RewriteModel { get; set; }
}
