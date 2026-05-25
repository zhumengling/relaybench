using RelayBench.Core.Services;

namespace RelayBench.Services;

internal sealed class TransparentProxyManagementApiService
{
    public object BuildHealthPayload(
        bool isRunning,
        TransparentProxyServerConfig? config,
        TransparentProxyMetricsSnapshot metrics,
        IReadOnlyList<CodexOAuthCredential> codexOAuthCredentials)
    {
        var codexOAuthRoutes = config?.Routes.Count(static route => route.IsCodexOAuth) ?? 0;
        return new
        {
            status = isRunning ? "ok" : "stopped",
            port = config?.Port ?? 0,
            routes = config?.Routes.Count ?? 0,
            codexOAuth = new
            {
                routes = codexOAuthRoutes,
                ready = codexOAuthCredentials.Count(static item => item.State == CodexOAuthCredentialState.Ready),
                refreshing = codexOAuthCredentials.Count(static item => item.State == CodexOAuthCredentialState.Refreshing),
                refreshBackoff = codexOAuthCredentials.Count(static item => item.State == CodexOAuthCredentialState.RefreshBackoff),
                needsRelogin = codexOAuthCredentials.Count(static item => item.State == CodexOAuthCredentialState.NeedsRelogin),
                disabled = codexOAuthCredentials.Count(static item => item.State == CodexOAuthCredentialState.Disabled),
                accounts = codexOAuthCredentials.Select(static item => new
                {
                    id = MaskCredentialReference(item.Id),
                    name = item.DisplayName,
                    state = item.State.ToString(),
                    plan = item.PlanType,
                    account = item.AccountIdHash,
                    item.AccessTokenExpiresAt,
                    item.LastRefreshAt,
                    item.RefreshBackoffUntil,
                    item.RefreshFailureCount,
                    lastError = ProbeTraceRedactor.RedactText(item.LastError)
                }).ToArray()
            },
            metrics
        };
    }

    public object BuildCachePayload(
        TransparentProxyCacheStats responseStats,
        TransparentProxyPromptSessionCacheStats promptStats,
        TransparentProxyServerConfig? config,
        object policy)
        => new
        {
            enabled = config?.EnableCache ?? false,
            ttlSeconds = config?.CacheTtlSeconds ?? 0,
            maxResponseBytes = config?.CacheMaxBytes ?? 0,
            response = new
            {
                entries = responseStats.ResponseEntries,
                hits = responseStats.Hits,
                misses = responseStats.Misses,
                hitRate = CalculateRate(responseStats.Hits, responseStats.Hits + responseStats.Misses),
                stores = responseStats.Stores,
                evictions = responseStats.Evictions,
                inFlightKeys = responseStats.InFlightKeys,
                leaseWaits = responseStats.LeaseWaits
            },
            modelList = new
            {
                entries = responseStats.ModelListEntries,
                hits = responseStats.ModelListHits,
                misses = responseStats.ModelListMisses,
                hitRate = CalculateRate(responseStats.ModelListHits, responseStats.ModelListHits + responseStats.ModelListMisses)
            },
            promptSessions = new
            {
                entries = promptStats.Entries,
                hits = promptStats.Hits,
                misses = promptStats.Misses,
                hitRate = CalculateRate(promptStats.Hits, promptStats.Hits + promptStats.Misses)
            },
            policy
        };

    public object BuildUsagePayload(
        TransparentProxyTokenTelemetrySnapshot snapshot,
        IReadOnlyList<TransparentProxyUsageEvent> events)
        => new
        {
            generatedAt = DateTimeOffset.UtcNow,
            totals = new
            {
                outputTokens = snapshot.TotalOutputTokens,
                inputTokens = snapshot.TotalInputTokens,
                totalTokens = snapshot.TotalInputTokens + snapshot.TotalOutputTokens,
                tokensPerSecond = snapshot.TokensPerSecond,
                promptCacheTokens = snapshot.PromptCacheTokens,
                lastTokenActivityAt = snapshot.LastTokenActivityAt
            },
            eventCount = events.Count,
            events = events.Select(static item => new
            {
                item.Sequence,
                item.Timestamp,
                kind = item.Kind.ToString(),
                item.OutputTokenDelta,
                item.TotalOutputTokens,
                item.TokensPerSecond,
                item.PromptCacheTokenDelta,
                item.TotalPromptCacheTokens,
                item.InputTokenDelta,
                item.TotalInputTokens,
                item.Estimated,
                item.ModelName,
                item.RouteName,
                item.WireApi,
                item.CacheState,
                item.IngressKind,
                item.SourceApplication,
                item.CaptureMode
            }).ToArray()
        };

    public object BuildLogsPayload(IReadOnlyList<TransparentProxyLogEntry> logs)
        => new
        {
            generatedAt = DateTimeOffset.UtcNow,
            count = logs.Count,
            logs = logs.Select(static item => new
            {
                item.Timestamp,
                item.Level,
                item.Method,
                item.Path,
                item.RouteName,
                item.StatusCode,
                item.ElapsedMs,
                item.Message,
                item.ModelName,
                item.RequestId,
                item.WireApi,
                item.AttemptSummary,
                item.IngressKind,
                item.SourceApplication,
                item.CaptureMode,
                item.TargetHost,
                item.WasTunnelOnly
            }).ToArray()
        };

    public object BuildIngressPayload(
        TransparentProxyMetricsSnapshot metrics)
        => new
        {
            generatedAt = DateTimeOffset.UtcNow,
            count = metrics.Ingresses?.Count ?? 0,
            ingresses = (metrics.Ingresses ?? Array.Empty<TransparentProxyIngressMetricsSnapshot>())
                .Select(static item => new
                {
                    item.IngressKind,
                    item.SourceApplication,
                    item.CaptureMode,
                    item.Requests,
                    item.Successes,
                    item.Failures,
                    item.TunnelOnlyRequests,
                    item.OutputTokens,
                    item.LastRequestAt,
                    successRate = CalculateRate(item.Successes, item.Requests)
                })
                .ToArray()
        };

    public object BuildCaptureAppsPayload(
        IReadOnlyList<TransparentProxyDetectedApp> apps,
        TransparentProxyMetricsSnapshot metrics)
        => new
        {
            generatedAt = DateTimeOffset.UtcNow,
            count = apps.Count,
            apps = apps.Select(app =>
            {
                var ingress = FindIngressForApp(app, metrics.Ingresses ?? Array.Empty<TransparentProxyIngressMetricsSnapshot>());
                return new
                {
                    app.Id,
                    app.DisplayName,
                    app.RecommendedMode,
                    app.Status,
                    app.IsDetected,
                    executablePath = ProbeTraceRedactor.RedactUrl(app.ExecutablePath ?? string.Empty),
                    configPath = ProbeTraceRedactor.RedactUrl(app.ConfigPath ?? string.Empty),
                    traffic = ingress is null
                        ? null
                        : new
                        {
                            ingress.IngressKind,
                            ingress.SourceApplication,
                            ingress.CaptureMode,
                            ingress.Requests,
                            ingress.Successes,
                            ingress.Failures,
                            ingress.TunnelOnlyRequests,
                            ingress.OutputTokens,
                            ingress.PromptCacheTokens,
                            ingress.LastRequestAt,
                            ingress.LastTokenActivityAt,
                            successRate = CalculateRate(ingress.Successes, ingress.Requests)
                        }
                };
            }).ToArray()
        };

    public object BuildCaptureDiagnosticsPayload(
        bool isRunning,
        TransparentProxyServerConfig? config,
        IReadOnlyList<TransparentProxyDetectedApp> apps,
        TransparentProxyPortInspectionResult portInspection,
        TransparentProxyCliEnvironmentSnapshot cliEnvironment,
        IReadOnlyList<TransparentProxyLaunchWrapperArtifact> launchWrappers)
        => new
        {
            generatedAt = DateTimeOffset.UtcNow,
            unifiedEndpoint = new
            {
                running = isRunning,
                host = "127.0.0.1",
                port = config?.Port ?? 0,
                baseUrl = config is null ? string.Empty : $"http://127.0.0.1:{config.Port}",
                openAiBaseUrl = config is null ? string.Empty : $"http://127.0.0.1:{config.Port}/v1",
                responsesUrl = config is null ? string.Empty : $"http://127.0.0.1:{config.Port}/v1/responses",
                anthropicUrl = config is null ? string.Empty : $"http://127.0.0.1:{config.Port}/v1/messages",
                modelsUrl = config is null ? string.Empty : $"http://127.0.0.1:{config.Port}/v1/models",
                healthUrl = config is null ? string.Empty : $"http://127.0.0.1:{config.Port}/relaybench/health",
                localOnly = true,
                portInspection = new
                {
                    portInspection.Port,
                    portInspection.IsListening,
                    portInspection.ProcessId,
                    portInspection.ProcessName,
                    portInspection.LocalAddress,
                    portInspection.Summary
                }
            },
            appCapture = new
            {
                mode = "optional",
                detectedCount = apps.Count,
                targets = apps.Select(static app => new
                {
                    app.Id,
                    app.DisplayName,
                    app.RecommendedMode,
                    app.Status,
                    app.IsDetected,
                    executablePath = ProbeTraceRedactor.RedactUrl(app.ExecutablePath ?? string.Empty),
                    configPath = ProbeTraceRedactor.RedactUrl(app.ConfigPath ?? string.Empty)
                }).ToArray(),
                launchWrappers = launchWrappers.Select(static item => new
                {
                    item.Id,
                    item.DisplayName,
                    item.ExistingCount,
                    powerShellPath = ProbeTraceRedactor.RedactUrl(item.PowerShellPath),
                    cmdPath = ProbeTraceRedactor.RedactUrl(item.CmdPath),
                    item.PowerShellExists,
                    item.CmdExists
                }).ToArray()
            },
            safety = new
            {
                localOnly = true,
                systemProxyUnchanged = true,
                appConfigOptInOnly = true,
                recoveryAvailableForAppConfigs = true
            },
            cliEnvironment = new
            {
                cliEnvironment.OpenAiBaseUrl,
                cliEnvironment.AnthropicBaseUrl,
                token = "***local***",
                cliEnvironment.PowerShell,
                cliEnvironment.Cmd,
                cliEnvironment.Notes
            },
            managementEndpoints = new[]
            {
                "/relaybench/health",
                "/relaybench/metrics",
                "/relaybench/usage",
                "/relaybench/ingress",
                "/relaybench/capture/apps",
                "/relaybench/capture/diagnostics",
                "/relaybench/capture/recovery"
            }
        };

    public object BuildCaptureRecoveryPayload(
        bool executed,
        IReadOnlyList<TransparentProxyCaptureRecoveryItem> items)
        => new
        {
            generatedAt = DateTimeOffset.UtcNow,
            executed,
            count = items.Count,
            succeeded = items.Count(item => item.Succeeded),
            failed = items.Count(item => !item.Succeeded),
            summary = executed
                ? "Capture recovery finished. Items without a backup are reported as skipped."
                : "Recovery preview only. Use POST to restore the latest RelayBench app-capture backups.",
            items = items.Select(static item => new
            {
                item.Id,
                item.DisplayName,
                item.Status,
                item.Succeeded,
                item.Summary,
                path = ProbeTraceRedactor.RedactUrl(item.Path ?? string.Empty),
                backupPath = ProbeTraceRedactor.RedactUrl(item.BackupPath ?? string.Empty)
            }).ToArray()
        };

    public object BuildProtocolsPayload(IReadOnlyList<TransparentProxyRoute> routes)
        => new
        {
            generatedAt = DateTimeOffset.UtcNow,
            fallbackOrder = new[]
            {
                new { wireApi = ProxyWireApiProbeService.ResponsesWireApi, name = "Responses" },
                new { wireApi = ProxyWireApiProbeService.AnthropicMessagesWireApi, name = "Anthropic Messages" },
                new { wireApi = ProxyWireApiProbeService.ChatCompletionsWireApi, name = "OpenAI Chat" }
            },
            routes = routes.Select(static route => new
            {
                id = route.Id,
                name = route.Name,
                baseUrl = ProbeTraceRedactor.RedactText(route.BaseUrl),
                preferred = DisplayWireApi(route.PreferredWireApi ?? string.Empty),
                preferredWireApi = route.PreferredWireApi,
                checkedAt = route.ProtocolCheckedAt,
                support = new
                {
                    responses = route.ResponsesSupported,
                    anthropic = route.AnthropicMessagesSupported,
                    chat = route.ChatCompletionsSupported
                },
                models = route.Models.Count,
                protocols = new[]
                {
                    new { wireApi = ProxyWireApiProbeService.ResponsesWireApi, name = "Responses", supported = route.ResponsesSupported },
                    new { wireApi = ProxyWireApiProbeService.AnthropicMessagesWireApi, name = "Anthropic Messages", supported = route.AnthropicMessagesSupported },
                    new { wireApi = ProxyWireApiProbeService.ChatCompletionsWireApi, name = "OpenAI Chat", supported = route.ChatCompletionsSupported }
                }
            }).ToArray()
        };

    public object BuildRoutesPayload(
        bool isRunning,
        TransparentProxyServerConfig? config,
        TransparentProxyMetricsSnapshot metrics)
    {
        var metricsByRouteId = metrics.Routes
            .GroupBy(static route => route.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var routes = (config?.Routes ?? Array.Empty<TransparentProxyRoute>())
            .Select(route =>
            {
                metricsByRouteId.TryGetValue(route.Id, out var routeMetrics);
                return new
                {
                    route.Id,
                    route.Name,
                    baseUrl = ProbeTraceRedactor.RedactUrl(route.BaseUrl),
                    apiKey = BuildApiKeyPreview(route.ApiKey),
                    route.Priority,
                    route.Prefix,
                    route.Models,
                    modelMappings = route.ModelMappings.Select(static mapping => new
                    {
                        upstream = mapping.Name,
                        alias = mapping.EffectiveAlias
                    }).ToArray(),
                    auth = new
                    {
                        mode = route.AuthMode,
                        provider = route.IsCodexOAuth ? route.OAuthProvider : string.Empty,
                        credential = route.IsCodexOAuth ? MaskCredentialReference(route.OAuthCredentialId) : string.Empty,
                        codexBackendBaseUrl = route.IsCodexOAuth ? ProbeTraceRedactor.RedactUrl(route.CodexBackendBaseUrl) : string.Empty
                    },
                    protocol = new
                    {
                        preferred = routeMetrics?.PreferredWireApi ?? route.PreferredWireApi,
                        chat = routeMetrics?.ChatCompletionsSupported ?? route.ChatCompletionsSupported,
                        responses = routeMetrics?.ResponsesSupported ?? route.ResponsesSupported,
                        anthropic = routeMetrics?.AnthropicMessagesSupported ?? route.AnthropicMessagesSupported,
                        checkedAt = routeMetrics?.ProtocolCheckedAt ?? route.ProtocolCheckedAt
                    },
                    health = routeMetrics is null
                        ? null
                        : new
                        {
                            routeMetrics.Sent,
                            routeMetrics.Success,
                            routeMetrics.Failed,
                            routeMetrics.LastStatusCode,
                            routeMetrics.LastLatencyMs,
                            routeMetrics.ConsecutiveFailures,
                            routeMetrics.CircuitState,
                            routeMetrics.CircuitOpenUntil,
                            modelCooldowns = routeMetrics.ModelCooldowns ?? Array.Empty<TransparentProxyModelCooldownSnapshot>()
                        }
                };
            })
            .ToArray();

        return new
        {
            running = isRunning,
            port = config?.Port ?? 0,
            routeCount = routes.Length,
            routes
        };
    }

    private static string DisplayWireApi(string wireApi)
        => wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic Messages",
            _ => "OpenAI Chat"
        };

    private static string MaskCredentialReference(string credentialId)
    {
        var normalized = credentialId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.Contains('@', StringComparison.Ordinal)
            ? CodexOAuthCredential.MaskEmail(normalized)
            : normalized.Length <= 8
                ? "***"
                : $"{normalized[..4]}***{normalized[^4..]}";
    }

    private static double CalculateRate(long numerator, long denominator)
        => denominator <= 0
            ? 0d
            : Math.Round(numerator * 100d / denominator, 2);

    private static TransparentProxyIngressMetricsSnapshot? FindIngressForApp(
        TransparentProxyDetectedApp app,
        IReadOnlyList<TransparentProxyIngressMetricsSnapshot> ingresses)
    {
        if (ingresses.Count == 0)
        {
            return null;
        }

        var needles = BuildIngressNeedles(app).Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray();
        if (needles.Length == 0)
        {
            return null;
        }

        return ingresses
            .OrderByDescending(static ingress => ingress.LastRequestAt ?? ingress.LastTokenActivityAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault(ingress => needles.Any(needle =>
                ingress.SourceApplication.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                ingress.CaptureMode.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> BuildIngressNeedles(TransparentProxyDetectedApp app)
    {
        yield return app.DisplayName;
        if (app.Id.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Claude";
        }

        if (app.Id.Contains("vs", StringComparison.OrdinalIgnoreCase))
        {
            yield return "VS";
            yield return "Code";
        }

        if (app.Id.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Codex";
        }
    }

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

internal sealed record TransparentProxyCaptureRecoveryItem(
    string Id,
    string DisplayName,
    string Status,
    bool Succeeded,
    string Summary,
    string? Path,
    string? BackupPath);
