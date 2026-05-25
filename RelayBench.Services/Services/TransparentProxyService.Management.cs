using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.Core.Services;

namespace RelayBench.Services;

public sealed partial class TransparentProxyService
{
    private object BuildHealthPayload()
        => _managementApi.BuildHealthPayload(
            IsRunning,
            _config,
            CreateMetricsSnapshot(),
            _codexOAuthService.GetCredentials());

    private object BuildCachePayload()
    {
        var responseStats = _responseCache.Stats;
        var promptStats = _protocolTranslator.PromptSessionCacheStats;
        return _managementApi.BuildCachePayload(
            responseStats,
            promptStats,
            _config,
            _responseCachePolicy.BuildPolicyPayload());
    }

    private object BuildUsagePayload()
    {
        var snapshot = _tokenTelemetry.CreateSnapshot();
        var events = _tokenTelemetry.UsageEvents.Snapshot(96);
        return _managementApi.BuildUsagePayload(snapshot, events);
    }

    private object BuildIngressPayload()
        => _managementApi.BuildIngressPayload(CreateMetricsSnapshot());

    private object BuildCaptureAppsPayload()
        => _managementApi.BuildCaptureAppsPayload(_appDetector.Detect(), CreateMetricsSnapshot());

    private object BuildCaptureDiagnosticsPayload()
    {
        var config = _config;
        var basePort = config?.Port ?? 17880;
        return _managementApi.BuildCaptureDiagnosticsPayload(
            IsRunning,
            config,
            _appDetector.Detect(),
            _portInspector.Inspect(basePort),
            _cliEnvironmentService.Build(basePort),
            _launchWrapperService.ScanKnownLaunchers());
    }

    private object BuildCaptureRecoveryPayload(bool execute)
        => _managementApi.BuildCaptureRecoveryPayload(
            execute,
            execute ? ExecuteCaptureRecovery() : PreviewCaptureRecovery());

    private object BuildLogsPayload(string pathAndQuery)
    {
        var limit = 96;
        var requestedLimit = TryReadQueryParameter(pathAndQuery, "limit");
        if (int.TryParse(requestedLimit, out var parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, 500);
        }

        return _managementApi.BuildLogsPayload(SnapshotRecentLogs(limit));
    }

    private object BuildProtocolsPayload()
        => _managementApi.BuildProtocolsPayload(_config?.Routes ?? Array.Empty<TransparentProxyRoute>());

    private static bool ShouldExecuteCaptureRecovery(string method)
        => string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase);

    internal static bool IsManagementRequestAllowed(
        TransparentProxyServerConfig? config,
        IPAddress? remoteAddress,
        string? providedManagementKey)
    {
        var isLoopback = IsLoopbackAddress(remoteAddress);
        var secret = config?.ManagementSecret?.Trim() ?? string.Empty;
        if (!isLoopback)
        {
            if (config?.AllowRemoteManagement != true || string.IsNullOrWhiteSpace(secret))
            {
                return false;
            }
        }

        return string.IsNullOrWhiteSpace(secret) ||
               FixedTimeEquals(secret, providedManagementKey?.Trim() ?? string.Empty);
    }

    private static bool IsLoopbackAddress(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private IReadOnlyList<TransparentProxyCaptureRecoveryItem> PreviewCaptureRecovery()
    {
        var items = _captureArtifactStore
            .ScanDefaultArtifacts()
            .Select(static artifact => new TransparentProxyCaptureRecoveryItem(
                artifact.TargetId,
                artifact.DisplayName,
                artifact.BackupCount > 0 ? "ready" : "missing",
                true,
                artifact.BackupCount > 0
                    ? $"已找到 {artifact.BackupCount} 个恢复点；POST 后将使用最近一次备份恢复。"
                    : "未找到 RelayBench 接管备份；POST 时会跳过该目标。",
                artifact.Path,
                artifact.LatestBackupPath))
            .ToList();

        items.AddRange(_launchWrapperService
            .ScanKnownLaunchers()
            .Select(static artifact => new TransparentProxyCaptureRecoveryItem(
                $"launcher-{artifact.Id}",
                $"{artifact.DisplayName} 临时启动器",
                artifact.ExistingCount > 0 ? "ready" : "missing",
                true,
                artifact.ExistingCount > 0
                    ? $"已找到 {artifact.ExistingCount} 个临时启动器；POST 后将删除这些启动器。"
                    : "未发现 RelayBench 临时启动器；POST 时会跳过。",
                string.Join(";", artifact.ExistingPaths),
                null)));

        return items;
    }

    private IReadOnlyList<TransparentProxyCaptureRecoveryItem> ExecuteCaptureRecovery()
    {
        List<TransparentProxyCaptureRecoveryItem> items = [];
        try
        {
            var result = _codexConfigService.RestoreLatestBackup();
            items.Add(new(
                "codex-cli",
                "Codex CLI",
                result.Succeeded ? "restored" : "skipped",
                result.Succeeded,
                result.Summary,
                result.ConfigPath,
                result.BackupPath));
        }
        catch (Exception ex)
        {
            items.Add(new(
                "codex-cli",
                "Codex CLI",
                "failed",
                false,
                ex.Message,
                ResolveCodexConfigPath(),
                null));
        }

        try
        {
            var result = _claudeConfigService.RestoreLatestBackup();
            items.Add(new(
                "claude-cli",
                "Claude CLI",
                result.Succeeded ? "restored" : "skipped",
                result.Succeeded,
                result.Summary,
                result.SettingsPath,
                result.BackupPath));
        }
        catch (Exception ex)
        {
            items.Add(new(
                "claude-cli",
                "Claude CLI",
                "failed",
                false,
                ex.Message,
                ResolveClaudeSettingsPath(),
                null));
        }

        try
        {
            var result = _vsCodeSettingsService.RestoreLatestBackups();
            items.Add(new(
                "vs-codex",
                "VS Codex / VS Code",
                result.Succeeded ? "restored" : "skipped",
                result.Succeeded,
                result.Summary,
                string.Join(";", result.ChangedFiles),
                string.Join(";", result.BackupFiles)));
        }
        catch (Exception ex)
        {
            items.Add(new(
                "vs-codex",
                "VS Codex / VS Code",
                "failed",
                false,
                ex.Message,
                string.Join(";", ResolveVsCodeSettingsPaths()),
                null));
        }

        try
        {
            var result = _launchWrapperService.DeleteKnownLaunchers();
            items.Add(new(
                "launch-wrappers",
                "CLI 临时启动器",
                result.DeletedCount > 0 ? "restored" : "skipped",
                result.Succeeded,
                result.Summary,
                string.Join(";", result.DeletedPaths.Concat(result.FailedPaths)),
                null));
        }
        catch (Exception ex)
        {
            items.Add(new(
                "launch-wrappers",
                "CLI 临时启动器",
                "failed",
                false,
                ex.Message,
                null,
                null));
        }

        return items;
    }

    private static string ResolveCodexConfigPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    private static string ResolveClaudeSettingsPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static IReadOnlyList<string> ResolveVsCodeSettingsPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var stable = Path.Combine(appData, "Code", "User", "settings.json");
        var insiders = Path.Combine(appData, "Code - Insiders", "User", "settings.json");
        var insidersDirectory = Path.GetDirectoryName(insiders);
        return File.Exists(insiders) ||
               (!string.IsNullOrWhiteSpace(insidersDirectory) && Directory.Exists(insidersDirectory))
            ? [stable, insiders]
            : [stable];
    }

    private static double CalculateRate(long numerator, long denominator)
        => denominator <= 0
            ? 0d
            : Math.Round(numerator * 100d / denominator, 2);

    private object BuildSchedulerPayload(string pathAndQuery)
    {
        var config = _config;
        var requestedModel = TryReadQueryParameter(pathAndQuery, "model");
        var sessionKey = TryReadQueryParameter(pathAndQuery, "session");
        if (config is null)
        {
            return new
            {
                ready = false,
                model = requestedModel,
                routeCount = 0,
                candidates = Array.Empty<object>(),
                routes = Array.Empty<object>()
            };
        }

        var now = DateTimeOffset.UtcNow;
        TransparentProxyRoute[] candidates;
        Dictionary<string, TransparentProxyRouteRuntimeState> states;
        string? boundRouteId;
        var explicitPoolExists = TransparentProxySchedulerService.HasExplicitRouteModelPool(config.Routes, requestedModel);
        lock (_syncRoot)
        {
            PruneSessionRouteBindings(now);
            boundRouteId = string.Equals(TransparentProxyRouteStrategies.Normalize(config.RouteStrategy), TransparentProxyRouteStrategies.SessionAffinity, StringComparison.Ordinal) &&
                           !string.IsNullOrWhiteSpace(sessionKey) &&
                           TryGetSessionRouteBinding(config, sessionKey, now, out var sessionRouteId)
                ? sessionRouteId
                : null;
            states = new Dictionary<string, TransparentProxyRouteRuntimeState>(_routeStates, StringComparer.OrdinalIgnoreCase);
            var previewCursor = _roundRobinCursor;
            candidates = _scheduler.BuildCandidateRoutes(
                    config,
                    requestedModel,
                    boundRouteId,
                    states,
                    _circuitBreaker,
                    now,
                    ref previewCursor)
                .ToArray();
        }

        var orderByRouteId = candidates
            .Select((route, index) => (route.Id, Order: index + 1))
            .ToDictionary(static item => item.Id, static item => item.Order, StringComparer.OrdinalIgnoreCase);
        var routeRows = config.Routes
            .Select(route =>
            {
                states.TryGetValue(route.Id, out var state);
                var canServeModel = TransparentProxySchedulerService.CanRouteServeModel(route, requestedModel);
                var explicitMatch = TransparentProxySchedulerService.HasExplicitRouteModelMatch(route, requestedModel);
                var routeAvailable = state is null || _circuitBreaker.IsRouteAvailable(state, now);
                orderByRouteId.TryGetValue(route.Id, out var order);
                return new
                {
                    id = route.Id,
                    name = route.Name,
                    order = order == 0 ? (int?)null : order,
                    selected = order > 0,
                    canServeModel,
                    explicitMatch,
                    passThroughFallback = explicitPoolExists && canServeModel && !explicitMatch,
                    available = routeAvailable,
                    skipReason = ResolveSchedulerSkipReason(order, canServeModel, routeAvailable, explicitPoolExists, explicitMatch),
                    priority = route.Priority,
                    models = route.Models.Count,
                    protocol = route.PreferredWireApi,
                    circuit = state?.CircuitState.ToString() ?? "Closed",
                    circuitOpenUntil = state?.CircuitOpenUntil,
                    latencyMs = state?.LastLatencyMs ?? 0,
                    success = state?.Success ?? 0,
                    failed = state?.Failed ?? 0
                };
            })
            .ToArray();

        return new
        {
            ready = true,
            generatedAt = now,
            model = requestedModel,
            session = string.IsNullOrWhiteSpace(sessionKey) ? null : "provided",
            strategy = TransparentProxyRouteStrategies.Normalize(config.RouteStrategy),
            boundRouteId,
            explicitPoolExists,
            candidateCount = candidates.Length,
            candidates = candidates.Select((route, index) => new
            {
                order = index + 1,
                id = route.Id,
                name = route.Name,
                priority = route.Priority,
                models = route.Models.Count,
                protocol = route.PreferredWireApi
            }).ToArray(),
            routes = routeRows
        };
    }

    private static string ResolveSchedulerSkipReason(
        int order,
        bool canServeModel,
        bool routeAvailable,
        bool explicitPoolExists,
        bool explicitMatch)
    {
        if (order > 0)
        {
            return string.Empty;
        }

        if (!canServeModel)
        {
            return "model-not-served";
        }

        if (!routeAvailable)
        {
            return "circuit-open";
        }

        if (explicitPoolExists && !explicitMatch)
        {
            return "pass-through-fallback-later";
        }

        return "not-selected";
    }

    private static string? TryReadQueryParameter(string pathAndQuery, string name)
    {
        var queryStart = pathAndQuery.IndexOf('?');
        if (queryStart < 0 || queryStart == pathAndQuery.Length - 1)
        {
            return null;
        }

        foreach (var part in pathAndQuery[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            var rawName = equals >= 0 ? part[..equals] : part;
            if (!string.Equals(DecodeQueryValue(rawName), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = equals >= 0 ? part[(equals + 1)..] : string.Empty;
            var value = DecodeQueryValue(rawValue);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string DecodeQueryValue(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal)).Trim();
        }
        catch
        {
            return value.Trim();
        }
    }

    private object BuildRoutesPayload()
        => _managementApi.BuildRoutesPayload(IsRunning, _config, CreateMetricsSnapshot());

    private static string BuildApiKeyPreview(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "pass-through";
        }

        return apiKey.Length <= 8
            ? "***"
            : $"{apiKey[..Math.Min(3, apiKey.Length)]}...{apiKey[^Math.Min(4, apiKey.Length)..]}";
    }

}
