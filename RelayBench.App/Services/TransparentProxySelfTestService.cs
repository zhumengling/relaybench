using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RelayBench.App.Infrastructure;
using RelayBench.App.ViewModels;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

public sealed class TransparentProxySelfTestService
{
    public async Task<TransparentProxySelfTestResult> RunAsync(CancellationToken cancellationToken = default)
    {
        List<TransparentProxySelfTestCheck> checks = [];
        CheckConfigStore(checks);
        CheckRouteTextCodec(checks);
        CheckPersistentResponseCache(checks);
        CheckPromptSessionCache(checks);
        await CheckPersistentLogStoreAsync(checks, cancellationToken);
        CheckRouteHealthStore(checks);
        CheckWireProtocolRegistry(checks);
        CheckTranslatorRouteOptions(checks);
        await using var responsesUpstream = await FakeUpstreamServer.StartAsync(
            "responses",
            "rb-selftest-model",
            FakeUpstreamMode.Responses,
            cancellationToken);
        await using var anthropicUpstream = await FakeUpstreamServer.StartAsync(
            "anthropic",
            "anthropic-only",
            FakeUpstreamMode.AnthropicOnly,
            cancellationToken);
        await using var chatUpstream = await FakeUpstreamServer.StartAsync(
            "chat",
            "chat-only",
            FakeUpstreamMode.ChatOnly,
            cancellationToken);
        await CheckProtocolDiscoveryAsync(
            responsesUpstream,
            anthropicUpstream,
            chatUpstream,
            checks,
            cancellationToken);

        await using var proxy = new TransparentProxyService();
        ConcurrentQueue<TransparentProxyLogEntry> logs = new();
        proxy.LogEmitted += (_, entry) => logs.Enqueue(entry);

        var proxyPort = GetFreeTcpPort();
        await proxy.StartAsync(new TransparentProxyServerConfig(
            proxyPort,
            BuildRoutes(responsesUpstream, anthropicUpstream, chatUpstream),
            RateLimitPerMinute: 120,
            MaxConcurrency: 4,
            EnableFallback: true,
            EnableCache: true,
            CacheTtlSeconds: 300,
            RewriteModel: false,
            IgnoreTlsErrors: true,
            UpstreamTimeoutSeconds: 8)
        {
            RouteStrategy = TransparentProxyRouteStrategies.Priority
        });

        try
        {
            using HttpClient client = new()
            {
                BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}/"),
                Timeout = TimeSpan.FromSeconds(12)
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-selftest-client");

            await CheckModelsListAsync(client, checks, cancellationToken);
            await CheckDirectResponsesApiAsync(client, responsesUpstream, checks, cancellationToken);
            await CheckDirectAnthropicApiAsync(client, anthropicUpstream, checks, cancellationToken);
            await CheckResponsesPathAndCacheAsync(client, responsesUpstream, checks, cancellationToken);
            await CheckVolatileCacheKeyNormalizationAsync(client, responsesUpstream, checks, cancellationToken);
            await CheckResponsesStreamingConversionAsync(client, responsesUpstream, checks, cancellationToken);
            await CheckAnthropicStreamingConversionAsync(client, anthropicUpstream, checks, cancellationToken);
            await CheckToolRequestsBypassCacheAsync(client, responsesUpstream, checks, cancellationToken);
            await CheckConcurrentSingleflightCacheAsync(client, responsesUpstream, checks, cancellationToken);
            await CheckAnthropicFallbackAsync(client, checks, cancellationToken);
            await CheckChatFallbackAsync(client, checks, cancellationToken);
            await CheckHealthAndMetricsEndpointsAsync(client, checks, cancellationToken);
            await WaitForAttemptChainLogsAsync(logs, cancellationToken);
            CheckLogs(logs, checks);
        }
        finally
        {
            await proxy.StopAsync();
        }

        var passed = checks.Count(check => check.Passed);
        var summary = checks.All(check => check.Passed)
            ? $"\u900f\u660e\u4ee3\u7406\u672c\u5730\u81ea\u68c0\u901a\u8fc7\uff1a{passed}/{checks.Count} \u9879\u3002Responses\u3001Anthropic fallback\u3001OpenAI Chat fallback\u3001\u6a21\u578b\u805a\u5408\u548c\u77ed\u7f13\u5b58\u5747\u6b63\u5e38\u3002"
            : $"\u900f\u660e\u4ee3\u7406\u672c\u5730\u81ea\u68c0\u672a\u5b8c\u5168\u901a\u8fc7\uff1a{passed}/{checks.Count} \u9879\u3002\u5931\u8d25\uff1a{string.Join("\uff1b", checks.Where(check => !check.Passed).Select(check => check.Name))}";
        return new TransparentProxySelfTestResult(checks.All(check => check.Passed), summary, checks);
    }

    private static void CheckConfigStore(List<TransparentProxySelfTestCheck> checks)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-transparent-proxy-config-{Guid.NewGuid():N}");
        try
        {
            var legacySnapshot = new AppStateSnapshot
            {
                TransparentProxyPortText = "19001",
                TransparentProxyRoutesText = "legacy | http://127.0.0.1:19002 | legacy-model | sk-legacy-secret",
                TransparentProxyRateLimitPerMinuteText = "77",
                TransparentProxyMaxConcurrencyText = "6",
                TransparentProxyRouteStrategyKey = TransparentProxyRouteStrategies.Priority,
                TransparentProxyEnableFallback = true,
                TransparentProxyEnableCache = true,
                TransparentProxyCacheTtlSecondsText = "222",
                TransparentProxyRewriteModel = true
            };

            var store = new TransparentProxyConfigStore(rootDirectory);
            var migrated = store.Load(legacySnapshot);
            migrated.RoutesText = "saved | http://127.0.0.1:19003 | saved-model | sk-config-secret";
            store.Save(migrated);
            var reloaded = store.Load();
            var storedJson = File.ReadAllText(Path.Combine(rootDirectory, "config", "transparent-proxy.json"));

            checks.Add(new(
                "\u914d\u7f6e\u8fc1\u79fb",
                migrated.PortText == "19001" &&
                migrated.RouteStrategyKey == TransparentProxyRouteStrategies.Priority &&
                reloaded.RoutesText.Contains("sk-config-secret", StringComparison.Ordinal) &&
                !storedJson.Contains("sk-config-secret", StringComparison.Ordinal),
                "\u65e7 app-state \u4e2d\u7684\u900f\u660e\u4ee3\u7406\u914d\u7f6e\u5e94\u8fc1\u79fb\u5230\u72ec\u7acb\u914d\u7f6e\uff0c\u5e76\u5bf9\u8def\u7531 API key \u505a\u672c\u5730\u52a0\u5bc6\u4fdd\u5b58\u3002"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "\u914d\u7f6e\u8fc1\u79fb",
                false,
                $"\u900f\u660e\u4ee3\u7406\u72ec\u7acb\u914d\u7f6e\u81ea\u6d4b\u5931\u8d25\uff1a{ex.Message}"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootDirectory))
                {
                    Directory.Delete(rootDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void CheckRouteTextCodec(List<TransparentProxySelfTestCheck> checks)
    {
        const string text = "v3 | Codec Route | http://127.0.0.1:19004 | sk-codec | upstream-model=>public-model,second-model | 2 | codec | X-Test: yes;X-Trace: 123 | *hidden*";
        var routes = TransparentProxyRouteTextCodec.ParseRoutes(text);
        var editorItems = TransparentProxyRouteTextCodec.ParseEditorItems(text);
        var roundTrip = TransparentProxyRouteTextCodec.BuildRoutesTextFromEditor(editorItems);
        var roundTripRoutes = TransparentProxyRouteTextCodec.ParseRoutes(roundTrip);
        var route = routes.FirstOrDefault();
        var v4Editor = editorItems.FirstOrDefault();
        if (v4Editor is not null)
        {
            v4Editor.OutboundProxy = "direct";
            v4Editor.RequestRetryText = "2";
            v4Editor.MaxRetryIntervalSecondsText = "5";
            v4Editor.ModelCooldownSecondsText = "90";
            v4Editor.PayloadRulesText = "{\"override\":[{\"models\":[\"public-model\"],\"params\":{\"temperature\":0.2}}]}";
        }

        var v4RoundTripRoute = v4Editor is null
            ? null
            : TransparentProxyRouteTextCodec.ParseRoutes(TransparentProxyRouteTextCodec.BuildRoutesTextFromEditor([v4Editor])).FirstOrDefault();

        checks.Add(new(
            "\u8def\u7531\u7f16\u89e3\u7801",
            route is not null &&
            route.Priority == 2 &&
            route.Prefix == "codec" &&
            route.Models.Contains("upstream-model", StringComparer.OrdinalIgnoreCase) &&
            route.ModelMappings.Any(mapping =>
                string.Equals(mapping.Name, "upstream-model", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(mapping.Alias, "public-model", StringComparison.OrdinalIgnoreCase)) &&
            route.Headers.TryGetValue("X-Test", out var headerValue) &&
            string.Equals(headerValue, "yes", StringComparison.OrdinalIgnoreCase) &&
            route.ExcludedModelPatterns.Contains("*hidden*", StringComparer.OrdinalIgnoreCase) &&
            roundTripRoutes.Count == 1 &&
            string.Equals(roundTripRoutes[0].Id, route.Id, StringComparison.OrdinalIgnoreCase) &&
            v4RoundTripRoute is not null &&
            v4RoundTripRoute.OutboundProxy == "direct" &&
            v4RoundTripRoute.RequestRetry == 2 &&
            v4RoundTripRoute.MaxRetryIntervalSeconds == 5 &&
            v4RoundTripRoute.ModelCooldownSeconds == 90 &&
            v4RoundTripRoute.PayloadRulesText.Contains("temperature", StringComparison.OrdinalIgnoreCase),
                "\u8def\u7531 v3/v4 \u6587\u672c\u5e94\u7a33\u5b9a\u652f\u6301\u6a21\u578b\u6620\u5c04\u3001headers\u3001prefix\u3001\u4f18\u5148\u7ea7\u3001\u6392\u9664\u89c4\u5219\u548c CPA \u5f0f\u8c03\u5ea6\u9009\u9879\u3002"));
    }

    private static void CheckPersistentResponseCache(List<TransparentProxySelfTestCheck> checks)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-transparent-proxy-cache-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(rootDirectory);
            var databasePath = Path.Combine(rootDirectory, "transparent-proxy-cache.sqlite");
            const string cacheKey = "selftest-persistent-cache-key";
            var body = Encoding.UTF8.GetBytes("{\"ok\":true,\"message\":\"persistent cache ok\"}");
            var route = new TransparentProxyRoute(
                "persistent-cache-route",
                "Persistent Cache Route",
                "http://127.0.0.1:19014",
                "sk-cache",
                "persistent-model");
            var config = new TransparentProxyServerConfig(
                19015,
                [route],
                RateLimitPerMinute: 60,
                MaxConcurrency: 2,
                EnableFallback: true,
                EnableCache: true,
                CacheTtlSeconds: 300,
                RewriteModel: false,
                IgnoreTlsErrors: true,
                UpstreamTimeoutSeconds: 5);
            var first = new TransparentProxyResponseCacheService(databasePath);
            first.StoreResponse(cacheKey, 200, "application/json; charset=utf-8", body, "persistent-model", 4096);
            first.StoreModelsList("/v1/models", config, new
            {
                @object = "list",
                data = new[] { new { id = "persistent-model", @object = "model" } }
            });

            var second = new TransparentProxyResponseCacheService(databasePath);
            var hit = second.TryGetResponse(cacheKey, 300, out var cached);
            var modelHit = second.TryGetModelsList("/v1/models", config, out var cachedModels);
            var text = hit ? Encoding.UTF8.GetString(cached.Body) : string.Empty;
            var modelsText = modelHit ? Encoding.UTF8.GetString(cachedModels) : string.Empty;
            var cleared = second.Clear();
            var missAfterClear = !second.TryGetResponse(cacheKey, 300, out _);
            var modelMissAfterClear = !second.TryGetModelsList("/v1/models", config, out _);

            checks.Add(new(
                "Persistent response cache",
                hit &&
                modelHit &&
                cached.StatusCode == 200 &&
                text.Contains("persistent cache ok", StringComparison.OrdinalIgnoreCase) &&
                modelsText.Contains("persistent-model", StringComparison.OrdinalIgnoreCase) &&
                cleared >= 1 &&
                missAfterClear &&
                modelMissAfterClear,
                $"Response and model-list cache should survive service recreation and clear from disk on demand. hit={hit}, modelHit={modelHit}, cleared={cleared}."));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Persistent response cache",
                false,
                $"Persistent response cache self-test failed: {ex.Message}"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootDirectory))
                {
                    Directory.Delete(rootDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void CheckPromptSessionCache(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            var route = new TransparentProxyRoute(
                "prompt-cache-route",
                "Prompt Cache Route",
                "http://127.0.0.1:19016",
                "sk-prompt-cache",
                "prompt-cache-model",
                responsesSupported: true);
            var translator = new TransparentProxyProtocolTranslatorService();
            var first = translator.BuildPreparedUpstreamRequests(
                "POST",
                "/v1/chat/completions",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    model = "prompt-cache-model",
                    user = "relaybench-user",
                    messages = new[]
                    {
                        new { role = "system", content = "RelayBench prompt cache self test system prompt." },
                        new { role = "user", content = "Hello" }
                    }
                })),
                route,
                streamRequested: false).First(static item => string.Equals(item.WireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal));
            var second = translator.BuildPreparedUpstreamRequests(
                "POST",
                "/v1/chat/completions",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    model = "prompt-cache-model",
                    user = "relaybench-user",
                    messages = new[]
                    {
                        new { role = "system", content = "RelayBench prompt cache self test system prompt." },
                        new { role = "user", content = "Hello again" }
                    }
                })),
                route,
                streamRequested: false).First(static item => string.Equals(item.WireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal));
            var third = translator.BuildPreparedUpstreamRequests(
                "POST",
                "/v1/chat/completions",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    model = "prompt-cache-model",
                    user = "another-user",
                    messages = new[]
                    {
                        new { role = "system", content = "RelayBench prompt cache self test system prompt." },
                        new { role = "user", content = "Hello" }
                    }
                })),
                route,
                streamRequested: false).First(static item => string.Equals(item.WireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal));

            var firstKey = ReadJsonString(first.Body, "prompt_cache_key");
            var secondKey = ReadJsonString(second.Body, "prompt_cache_key");
            var thirdKey = ReadJsonString(third.Body, "prompt_cache_key");
            var firstSession = first.ExtraHeaders.TryGetValue("Session_id", out var session) ? session : string.Empty;
            var secondSession = second.ExtraHeaders.TryGetValue("Session_id", out var sessionAgain) ? sessionAgain : string.Empty;
            var stats = translator.PromptSessionCacheStats;

            checks.Add(new(
                "Prompt session cache",
                !string.IsNullOrWhiteSpace(firstKey) &&
                string.Equals(firstKey, secondKey, StringComparison.Ordinal) &&
                !string.Equals(firstKey, thirdKey, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(firstSession) &&
                string.Equals(firstSession, secondSession, StringComparison.Ordinal) &&
                stats.Entries >= 2 &&
                stats.Hits >= 1,
                "Responses protocol requests should reuse stable prompt_cache_key and Session_id per credential/model/user scope while separating different users."));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Prompt session cache",
                false,
                $"Prompt session cache self-test failed: {ex.Message}"));
        }
    }

    private static async Task CheckPersistentLogStoreAsync(
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-transparent-proxy-logs-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(rootDirectory);
            var databasePath = Path.Combine(rootDirectory, "transparent-proxy-logs.sqlite");
            var store = new TransparentProxyLogStore(databasePath);
            await store.AppendAsync(
                new TransparentProxyLogEntry(
                    DateTimeOffset.Now,
                    "INFO",
                    "POST",
                    "/v1/chat/completions?api_key=sk-selftest-secret",
                    "selftest-route",
                    200,
                    12,
                    "Authorization: Bearer sk-selftest-secret token=raw-token",
                    "rb-selftest-model",
                    "req-log-store",
                    "responses",
                    "SelfTest Responses/Responses:200"),
                cancellationToken);

            var entries = await store.LoadRecentAsync(10, cancellationToken);
            var exportPath = await store.ExportCsvAsync(rootDirectory, cancellationToken);
            var exported = await File.ReadAllTextAsync(exportPath, cancellationToken);
            await store.ClearAsync(cancellationToken);
            var afterClear = await store.LoadRecentAsync(10, cancellationToken);
            var combined = string.Join("\n", entries.Select(static entry => $"{entry.Path}\n{entry.Message}")) + "\n" + exported;

            checks.Add(new(
                "Persistent log store",
                entries.Count == 1 &&
                afterClear.Count == 0 &&
                File.Exists(exportPath) &&
                !combined.Contains("sk-selftest-secret", StringComparison.OrdinalIgnoreCase) &&
                !combined.Contains("raw-token", StringComparison.OrdinalIgnoreCase),
                "Transparent proxy logs should persist to SQLite, export CSV, clear on demand and redact secrets before storage."));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Persistent log store",
                false,
                $"Persistent log store self-test failed: {ex.Message}"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootDirectory))
                {
                    Directory.Delete(rootDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void CheckRouteHealthStore(List<TransparentProxySelfTestCheck> checks)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-transparent-proxy-route-health-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(rootDirectory);
            var databasePath = Path.Combine(rootDirectory, "transparent-proxy-route-health.sqlite");
            var store = new TransparentProxyRouteHealthStore(databasePath);
            var route = new TransparentProxyRoute(
                "route-health",
                "Route Health",
                "http://127.0.0.1:19010",
                "sk-health",
                "health-model");
            var state = new TransparentProxyRouteRuntimeState(route)
            {
                Sent = 7,
                Success = 3,
                Failed = 4,
                LastStatusCode = 503,
                LastLatencyMs = 432,
                ConsecutiveFailures = 4,
                CircuitWindowRequests = 7,
                CircuitWindowFailures = 4,
                LastSeenAt = DateTimeOffset.UtcNow
            };
            var retryAt = state.TransitionToOpen(DateTimeOffset.UtcNow, 120);
            store.Save(state);

            var reloaded = store.Load(["route-health"]);
            var fresh = new TransparentProxyRouteRuntimeState(route);
            if (reloaded.TryGetValue("route-health", out var snapshot))
            {
                fresh.ApplyHealthSnapshot(snapshot);
            }

            store.Reset("route-health");
            var afterReset = store.Load(["route-health"]);

            checks.Add(new(
                "Route health persistence",
                fresh.Sent == 7 &&
                fresh.Failed == 4 &&
                fresh.CircuitState == TransparentProxyCircuitState.Open &&
                fresh.CircuitOpenUntil >= retryAt.AddSeconds(-1) &&
                afterReset.Count == 0,
                "Route health and circuit breaker state should survive service recreation and clear on manual reset."));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Route health persistence",
                false,
                $"Route health persistence self-test failed: {ex.Message}"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootDirectory))
                {
                    Directory.Delete(rootDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void CheckWireProtocolRegistry(List<TransparentProxySelfTestCheck> checks)
    {
        var registry = new TransparentProxyWireProtocolRegistry();
        var unknown = new TransparentProxyRoute(
            "registry-unknown",
            "Registry Unknown",
            "http://127.0.0.1:19011",
            "sk-registry",
            "registry-model");
        var anthropicOnly = new TransparentProxyRoute(
            "registry-anthropic",
            "Registry Anthropic",
            "http://127.0.0.1:19012",
            "sk-registry",
            "registry-model",
            chatCompletionsSupported: false,
            responsesSupported: false,
            anthropicMessagesSupported: true);
        var failedProbe = new TransparentProxyRoute(
            "registry-fallback",
            "Registry Fallback",
            "http://127.0.0.1:19013",
            "sk-registry",
            "registry-model",
            chatCompletionsSupported: false,
            responsesSupported: false,
            anthropicMessagesSupported: false);

        var unknownAttempts = registry.BuildWireApiAttempts(unknown);
        var anthropicAttempts = registry.BuildWireApiAttempts(anthropicOnly);
        var fallbackAttempts = registry.BuildWireApiAttempts(failedProbe);

        checks.Add(new(
            "Wire protocol registry",
            unknownAttempts.SequenceEqual([
                ProxyWireApiProbeService.ResponsesWireApi,
                ProxyWireApiProbeService.AnthropicMessagesWireApi,
                ProxyWireApiProbeService.ChatCompletionsWireApi
            ]) &&
            anthropicAttempts.SequenceEqual([ProxyWireApiProbeService.AnthropicMessagesWireApi]) &&
            fallbackAttempts.SequenceEqual([ProxyWireApiProbeService.ChatCompletionsWireApi]),
            "Protocol conversion should be registry-driven and keep Responses -> Anthropic -> OpenAI Chat fallback order."));
    }

    private static void CheckTranslatorRouteOptions(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            var route = new TransparentProxyRoute(
                "translator-options",
                "Translator Options",
                "http://127.0.0.1:19014",
                "sk-translator-options",
                "upstream-a",
                chatCompletionsSupported: true,
                responsesSupported: false,
                anthropicMessagesSupported: false,
                modelMappings:
                [
                    new TransparentProxyModelMapping("upstream-a", "public"),
                    new TransparentProxyModelMapping("upstream-b", "public")
                ],
                payloadRulesText: "{\"override\":[{\"models\":[\"public\"],\"protocol\":\"openai\",\"params\":{\"temperature\":0.2}}],\"filter\":[{\"models\":[\"public\"],\"params\":[\"metadata\"]}]}");
            var translator = new TransparentProxyProtocolTranslatorService();
            var requestBody = JsonSerializer.SerializeToUtf8Bytes(new
            {
                model = "public",
                temperature = 1.0,
                metadata = new { trace = "remove-me" },
                messages = new[]
                {
                    new { role = "user", content = "Check route model pool and payload rules." }
                }
            });

            var prepared = translator.BuildPreparedUpstreamRequests(
                "POST",
                "/v1/chat/completions",
                requestBody,
                route,
                streamRequested: false);
            var chatAttempts = prepared
                .Where(static item => string.Equals(item.WireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal))
                .ToArray();
            var upstreamModels = chatAttempts
                .Select(static item => item.UpstreamModel)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var payloadsValid = chatAttempts.Length >= 2 &&
                                chatAttempts.All(static item => PayloadRuleBodyIsValid(item.Body));

            checks.Add(new(
                "Translator route options",
                upstreamModels.Contains("upstream-a", StringComparer.OrdinalIgnoreCase) &&
                upstreamModels.Contains("upstream-b", StringComparer.OrdinalIgnoreCase) &&
                chatAttempts.All(static item => string.Equals(item.ResponseModel, "public", StringComparison.OrdinalIgnoreCase)) &&
                payloadsValid,
                "Translator should expand alias model pools, keep the public response model name and apply per-route payload override/filter rules."));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Translator route options",
                false,
                $"Translator route options self-test failed: {ex.Message}"));
        }
    }

    private static bool PayloadRuleBodyIsValid(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return !root.TryGetProperty("metadata", out _) &&
               root.TryGetProperty("temperature", out var temperature) &&
               Math.Abs(temperature.GetDouble() - 0.2d) < 0.001d &&
               root.TryGetProperty("model", out var model) &&
               model.ValueKind == JsonValueKind.String &&
               (string.Equals(model.GetString(), "upstream-a", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.GetString(), "upstream-b", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<TransparentProxyRoute> BuildRoutes(
        FakeUpstreamServer responsesUpstream,
        FakeUpstreamServer anthropicUpstream,
        FakeUpstreamServer chatUpstream)
        =>
        [
            new(
                "selftest-responses",
                "SelfTest Responses",
                responsesUpstream.BaseUrl,
                "sk-selftest-route",
                "rb-selftest-model",
                models: ["rb-selftest-model", "rb-selftest-hidden"],
                priority: 1,
                prefix: "self",
                modelMappings:
                [
                    new TransparentProxyModelMapping("rb-selftest-model", "self-visible"),
                    new TransparentProxyModelMapping("rb-selftest-hidden", "self-hidden")
                ],
                excludedModelPatterns: ["*hidden*"]),
            new(
                "selftest-anthropic",
                "SelfTest Anthropic",
                anthropicUpstream.BaseUrl,
                "sk-selftest-route",
                "anthropic-only",
                models: ["anthropic-only"],
                priority: 2,
                excludedModelPatterns: ["*hidden*"]),
            new(
                "selftest-chat",
                "SelfTest Chat",
                chatUpstream.BaseUrl,
                "sk-selftest-route",
                "chat-only",
                models: ["chat-only"],
                priority: 3,
                excludedModelPatterns: ["*hidden*"])
        ];

    private static async Task CheckProtocolDiscoveryAsync(
        FakeUpstreamServer responsesUpstream,
        FakeUpstreamServer anthropicUpstream,
        FakeUpstreamServer chatUpstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-transparent-proxy-discovery-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(rootDirectory);
            var cache = new ProxyEndpointModelCacheService(Path.Combine(rootDirectory, "endpoint-model-cache.sqlite"));
            var discovery = new TransparentProxyProtocolDiscoveryService(new ProxyDiagnosticsService(), cache);
            var routes = BuildRoutes(responsesUpstream, anthropicUpstream, chatUpstream);
            var result = await discovery.DiscoverAsync(
                routes,
                new TransparentProxyProtocolDiscoveryOptions(
                    ForceProbe: true,
                    FetchCatalogModels: true,
                    IgnoreTlsErrors: true,
                    UpstreamTimeoutSeconds: 8,
                    FallbackModel: "rb-selftest-model"),
                cancellationToken: cancellationToken);
            var cached = await discovery.DiscoverAsync(
                result.HydratedRoutes,
                new TransparentProxyProtocolDiscoveryOptions(
                    ForceProbe: false,
                    FetchCatalogModels: false,
                    IgnoreTlsErrors: true,
                    UpstreamTimeoutSeconds: 8,
                    FallbackModel: "rb-selftest-model"),
                cancellationToken: cancellationToken);

            var responses = result.HydratedRoutes.FirstOrDefault(static route => route.Id == "selftest-responses");
            var anthropic = result.HydratedRoutes.FirstOrDefault(static route => route.Id == "selftest-anthropic");
            var chat = result.HydratedRoutes.FirstOrDefault(static route => route.Id == "selftest-chat");
            checks.Add(new(
                "\u534f\u8bae\u53d1\u73b0\u670d\u52a1",
                responses?.PreferredWireApi == ProxyWireApiProbeService.ResponsesWireApi &&
                anthropic?.PreferredWireApi == ProxyWireApiProbeService.AnthropicMessagesWireApi &&
                chat?.PreferredWireApi == ProxyWireApiProbeService.ChatCompletionsWireApi &&
                result.ProbedModels >= 3 &&
                cached.CachedModels >= 3,
                "\u72ec\u7acb\u534f\u8bae\u53d1\u73b0\u670d\u52a1\u5e94\u62c9\u53d6\u6a21\u578b\u3001\u5199\u5165\u534f\u8bae\u7f13\u5b58\uff0c\u5e76\u6309 Responses \u2192 Anthropic \u2192 OpenAI Chat \u987a\u5e8f\u9009\u62e9\u8def\u7531\u534f\u8bae\u3002"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "\u534f\u8bae\u53d1\u73b0\u670d\u52a1",
                false,
                $"\u72ec\u7acb\u534f\u8bae\u53d1\u73b0\u670d\u52a1\u81ea\u6d4b\u5931\u8d25\uff1a{ex.Message}"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootDirectory))
                {
                    Directory.Delete(rootDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task CheckModelsListAsync(
        HttpClient client,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("v1/models", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var ids = ReadModelIds(body);
        checks.Add(new(
            "模型聚合",
            response.IsSuccessStatusCode && ids.Contains("self-visible", StringComparer.OrdinalIgnoreCase),
            "聚合 /v1/models 应返回可见别名模型。"));
        checks.Add(new(
            "模型前缀",
            ids.Contains("self/self-visible", StringComparer.OrdinalIgnoreCase),
            "带 Prefix 的路由应额外暴露 prefix/model 形式。"));
        checks.Add(new(
            "模型排除",
            ids.All(id => !id.Contains("hidden", StringComparison.OrdinalIgnoreCase)),
            "排除规则应从聚合模型列表中移除隐藏模型。"));
    }

    private static async Task CheckDirectResponsesApiAsync(
        HttpClient client,
        FakeUpstreamServer upstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var before = upstream.Count("/v1/responses");
        using var response = await PostJsonAsync(
            client,
            "v1/responses",
            BuildResponsesRequest("rb-selftest-model"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var calls = upstream.Count("/v1/responses") - before;

        checks.Add(new(
            "Direct Responses API",
            response.IsSuccessStatusCode &&
            body.Contains("\"object\":\"response\"", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("selftest responses ok", StringComparison.OrdinalIgnoreCase) &&
            calls == 1,
            $"Direct /v1/responses requests should pass through the selected Responses route. calls={calls}."));
    }

    private static async Task CheckDirectAnthropicApiAsync(
        HttpClient client,
        FakeUpstreamServer upstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var before = upstream.Count("/v1/messages");
        using var response = await PostJsonAsync(
            client,
            "v1/messages",
            BuildAnthropicMessagesRequest("anthropic-only"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var calls = upstream.Count("/v1/messages") - before;

        checks.Add(new(
            "Direct Anthropic Messages API",
            response.IsSuccessStatusCode &&
            body.Contains("\"type\":\"message\"", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("selftest anthropic ok", StringComparison.OrdinalIgnoreCase) &&
            calls == 1,
            $"Direct /v1/messages requests should pass through the selected Anthropic route. calls={calls}."));
    }

    private static async Task CheckResponsesPathAndCacheAsync(
        HttpClient client,
        FakeUpstreamServer upstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildChatRequest("rb-selftest-model");
        var before = upstream.Count("/v1/responses");
        using var first = await PostJsonAsync(client, "v1/chat/completions", requestBody, cancellationToken);
        var firstBody = await first.Content.ReadAsStringAsync(cancellationToken);
        var firstText = ModelResponseTextExtractor.TryExtractAssistantText(firstBody) ?? string.Empty;
        using var second = await PostJsonAsync(client, "v1/chat/completions", requestBody, cancellationToken);
        var secondBody = await second.Content.ReadAsStringAsync(cancellationToken);
        var secondText = ModelResponseTextExtractor.TryExtractAssistantText(secondBody) ?? string.Empty;
        var cacheHit = second.Headers.TryGetValues("X-RelayBench-Cache", out var cacheValues) &&
                       cacheValues.Any(value => string.Equals(value, "HIT", StringComparison.OrdinalIgnoreCase));

        var responsesUpstreamCalls = upstream.Count("/v1/responses") - before;
        checks.Add(new(
            "Responses 转换",
            first.IsSuccessStatusCode && firstText.Contains("responses ok", StringComparison.OrdinalIgnoreCase),
            "OpenAI Chat 入站应优先转 Responses 并归一化为 Chat 响应。"));
        checks.Add(new(
            "短缓存命中",
            second.IsSuccessStatusCode &&
            secondText.Contains("responses ok", StringComparison.OrdinalIgnoreCase) &&
            cacheHit &&
            responsesUpstreamCalls == 1,
            $"相同非流式请求第二次应命中本地短缓存，不再打上游。cache header={cacheHit}，responses calls={responsesUpstreamCalls}。"));
    }

    private static async Task CheckVolatileCacheKeyNormalizationAsync(
        HttpClient client,
        FakeUpstreamServer upstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var before = upstream.Count("/v1/responses");
        var firstBody = BuildChatRequestWithVolatileFields(
            "RelayBench volatile cache key normalization probe.",
            "req-a",
            "idem-a",
            "trace-a");
        var secondBody = BuildChatRequestWithVolatileFields(
            "RelayBench volatile cache key normalization probe.",
            "req-b",
            "idem-b",
            "trace-b");

        using var first = await PostJsonAsync(
            client,
            "v1/chat/completions?beta=stable&request_id=req-a&api_key=sk-a",
            firstBody,
            cancellationToken);
        await first.Content.ReadAsStringAsync(cancellationToken);
        using var second = await PostJsonAsync(
            client,
            "v1/chat/completions?api_key=sk-b&request_id=req-b&beta=stable",
            secondBody,
            cancellationToken);
        await second.Content.ReadAsStringAsync(cancellationToken);

        var secondCacheHit = second.Headers.TryGetValues("X-RelayBench-Cache", out var cacheValues) &&
                             cacheValues.Any(value => string.Equals(value, "HIT", StringComparison.OrdinalIgnoreCase));
        var calls = upstream.Count("/v1/responses") - before;
        checks.Add(new(
            "缓存 key 规范化",
            first.IsSuccessStatusCode && second.IsSuccessStatusCode && secondCacheHit && calls == 1,
            $"短缓存应忽略 request_id、idempotency_key、metadata 和 query 顺序/鉴权差异。cache header={secondCacheHit}，responses calls={calls}。"));
    }

    private static async Task CheckResponsesStreamingConversionAsync(
        HttpClient client,
        FakeUpstreamServer upstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var before = upstream.Count("/v1/responses");
        using var response = await PostJsonStreamingAsync(
            client,
            "v1/chat/completions",
            BuildChatRequest("rb-selftest-model", "RelayBench responses stream conversion probe.", stream: true),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var stream = ReadOpenAiChatStream(body);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var calls = upstream.Count("/v1/responses") - before;

        checks.Add(new(
            "Responses stream conversion",
            response.IsSuccessStatusCode &&
            contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) &&
            stream.Done &&
            stream.ChunkCount >= 2 &&
            stream.Text.Contains("stream responses ok", StringComparison.OrdinalIgnoreCase) &&
            calls == 1 &&
            !body.Contains("response.output_text.delta", StringComparison.OrdinalIgnoreCase),
            $"Responses SSE should be fragmented safely and normalized to OpenAI Chat SSE. chunks={stream.ChunkCount}, done={stream.Done}, calls={calls}."));
    }

    private static async Task CheckAnthropicStreamingConversionAsync(
        HttpClient client,
        FakeUpstreamServer upstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var beforeResponses = upstream.Count("/v1/responses");
        var beforeMessages = upstream.Count("/v1/messages");
        using var response = await PostJsonStreamingAsync(
            client,
            "v1/chat/completions",
            BuildChatRequest("anthropic-only", "RelayBench anthropic stream conversion probe.", stream: true),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var stream = ReadOpenAiChatStream(body);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var responsesCalls = upstream.Count("/v1/responses") - beforeResponses;
        var messagesCalls = upstream.Count("/v1/messages") - beforeMessages;

        checks.Add(new(
            "Anthropic stream conversion",
            response.IsSuccessStatusCode &&
            contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) &&
            stream.Done &&
            stream.ChunkCount >= 2 &&
            stream.Text.Contains("stream anthropic ok", StringComparison.OrdinalIgnoreCase) &&
            responsesCalls == 1 &&
            messagesCalls == 1 &&
            !body.Contains("content_block_delta", StringComparison.OrdinalIgnoreCase),
            $"Anthropic SSE fallback should normalize to OpenAI Chat SSE after Responses fails. chunks={stream.ChunkCount}, done={stream.Done}, responses={responsesCalls}, messages={messagesCalls}."));
    }

    private static async Task CheckToolRequestsBypassCacheAsync(
        HttpClient client,
        FakeUpstreamServer upstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var before = upstream.Count("/v1/responses");
        var requestBody = BuildChatRequest("rb-selftest-model", "RelayBench tool cache bypass probe.", includeTools: true);
        using var first = await PostJsonAsync(client, "v1/chat/completions", requestBody, cancellationToken);
        await first.Content.ReadAsStringAsync(cancellationToken);
        using var second = await PostJsonAsync(client, "v1/chat/completions", requestBody, cancellationToken);
        await second.Content.ReadAsStringAsync(cancellationToken);
        var secondCacheHit = second.Headers.TryGetValues("X-RelayBench-Cache", out var cacheValues) &&
                             cacheValues.Any(value => string.Equals(value, "HIT", StringComparison.OrdinalIgnoreCase));
        var calls = upstream.Count("/v1/responses") - before;
        checks.Add(new(
            "工具请求不缓存",
            first.IsSuccessStatusCode && second.IsSuccessStatusCode && !secondCacheHit && calls == 2,
            $"带 tools 的请求应绕过本地响应缓存，避免工具结果串用。cache header={secondCacheHit}，responses calls={calls}。"));
    }

    private static async Task CheckConcurrentSingleflightCacheAsync(
        HttpClient client,
        FakeUpstreamServer upstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        var before = upstream.Count("/v1/responses");
        var requestBody = BuildChatRequest("rb-selftest-model", "RelayBench selftest singleflight cache probe.");
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => PostJsonAsync(client, "v1/chat/completions", requestBody, cancellationToken))
            .ToArray();
        var responses = await Task.WhenAll(tasks);
        try
        {
            var success = true;
            var cacheHits = 0;
            foreach (var response in responses)
            {
                success &= response.IsSuccessStatusCode;
                await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.Headers.TryGetValues("X-RelayBench-Cache", out var cacheValues) &&
                    cacheValues.Any(value => string.Equals(value, "HIT", StringComparison.OrdinalIgnoreCase)))
                {
                    cacheHits++;
                }
            }

            var calls = upstream.Count("/v1/responses") - before;
            checks.Add(new(
                "并发请求合并",
                success && calls == 1 && cacheHits >= 3,
                $"同一非流式请求并发进入时应合并为一次上游调用，其余命中缓存。cache hits={cacheHits}，responses calls={calls}。"));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static async Task CheckAnthropicFallbackAsync(
        HttpClient client,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        using var response = await PostJsonAsync(client, "v1/chat/completions", BuildChatRequest("anthropic-only"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var text = ModelResponseTextExtractor.TryExtractAssistantText(body) ?? string.Empty;
        checks.Add(new(
            "Anthropic fallback",
            response.IsSuccessStatusCode && text.Contains("anthropic ok", StringComparison.OrdinalIgnoreCase),
            "Responses 404 后应在同一路由内切到 Anthropic Messages。"));
    }

    private static async Task CheckChatFallbackAsync(
        HttpClient client,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        using var response = await PostJsonAsync(client, "v1/chat/completions", BuildChatRequest("chat-only"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var text = ModelResponseTextExtractor.TryExtractAssistantText(body) ?? string.Empty;
        checks.Add(new(
            "OpenAI Chat fallback",
            response.IsSuccessStatusCode && text.Contains("chat ok", StringComparison.OrdinalIgnoreCase),
            "Responses 和 Anthropic 都不可用时应回退 OpenAI Chat。"));
    }

    private static async Task CheckHealthAndMetricsEndpointsAsync(
        HttpClient client,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        using var healthResponse = await client.GetAsync("relaybench/health", cancellationToken);
        var healthBody = await healthResponse.Content.ReadAsStringAsync(cancellationToken);
        using var metricsResponse = await client.GetAsync("relaybench/metrics", cancellationToken);
        var metricsBody = await metricsResponse.Content.ReadAsStringAsync(cancellationToken);

        var healthOk = false;
        var metricsOk = false;
        try
        {
            using var health = JsonDocument.Parse(healthBody);
            healthOk =
                health.RootElement.TryGetProperty("status", out var status) &&
                string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase) &&
                health.RootElement.TryGetProperty("routes", out var routes) &&
                routes.GetInt32() >= 3 &&
                health.RootElement.TryGetProperty("metrics", out var healthMetrics) &&
                healthMetrics.TryGetProperty("totalRequests", out var healthTotalRequests) &&
                healthTotalRequests.GetInt32() > 0;

            using var metrics = JsonDocument.Parse(metricsBody);
            metricsOk =
                metrics.RootElement.TryGetProperty("isRunning", out var isRunning) &&
                isRunning.ValueKind == JsonValueKind.True &&
                metrics.RootElement.TryGetProperty("totalRequests", out var totalRequests) &&
                totalRequests.GetInt32() > 0 &&
                metrics.RootElement.TryGetProperty("routes", out var metricRoutes) &&
                metricRoutes.ValueKind == JsonValueKind.Array &&
                metricRoutes.GetArrayLength() >= 3 &&
                metrics.RootElement.TryGetProperty("responseCacheHits", out var cacheHits) &&
                cacheHits.GetInt64() >= 0;
        }
        catch
        {
            healthOk = false;
            metricsOk = false;
        }

        checks.Add(new(
            "Health and metrics endpoints",
            healthResponse.IsSuccessStatusCode &&
            metricsResponse.IsSuccessStatusCode &&
            healthOk &&
            metricsOk,
            "Local /relaybench/health and /relaybench/metrics endpoints should expose running state, routes, request counts and cache stats."));
    }

    private static async Task WaitForAttemptChainLogsAsync(
        ConcurrentQueue<TransparentProxyLogEntry> logs,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < 20; index++)
        {
            var entries = logs.ToArray();
            var hasAnthropicChain = entries.Any(entry =>
                entry.AttemptSummary.Contains("Responses", StringComparison.OrdinalIgnoreCase) &&
                entry.AttemptSummary.Contains("Anthropic", StringComparison.OrdinalIgnoreCase));
            var hasChatChain = entries.Any(entry =>
                entry.AttemptSummary.Contains("OpenAI Chat", StringComparison.OrdinalIgnoreCase));
            if (hasAnthropicChain && hasChatChain)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private static void CheckLogs(
        IEnumerable<TransparentProxyLogEntry> logs,
        List<TransparentProxySelfTestCheck> checks)
    {
        var entries = logs.ToArray();
        checks.Add(new(
            "日志模型名",
            entries.Any(entry => entry.ModelName.Contains("rb-selftest-model", StringComparison.OrdinalIgnoreCase) ||
                                 entry.ModelName.Contains("anthropic-only", StringComparison.OrdinalIgnoreCase) ||
                                 entry.ModelName.Contains("chat-only", StringComparison.OrdinalIgnoreCase)),
            "代理日志应包含请求模型名。"));
        checks.Add(new(
            "日志尝试链",
            entries.Any(entry => entry.AttemptSummary.Contains("Responses", StringComparison.OrdinalIgnoreCase) &&
                                 entry.AttemptSummary.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)) &&
            entries.Any(entry => entry.AttemptSummary.Contains("OpenAI Chat", StringComparison.OrdinalIgnoreCase)),
            "代理日志应记录协议尝试链。"));
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string relativePath,
        string body,
        CancellationToken cancellationToken)
        => await client.PostAsync(
            relativePath,
            new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken);

    private static async Task<HttpResponseMessage> PostJsonStreamingAsync(
        HttpClient client,
        string relativePath,
        string body,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static TransparentProxySelfTestStream ReadOpenAiChatStream(string body)
    {
        StringBuilder text = new();
        var done = false;
        var chunkCount = 0;
        using StringReader reader = new(body);
        while (reader.ReadLine() is { } line)
        {
            if (!ChatSseParser.TryReadDataLine(line, out var data))
            {
                continue;
            }

            if (ChatSseParser.IsDone(data))
            {
                done = true;
                continue;
            }

            var delta = ChatSseParser.TryExtractDelta(data);
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            chunkCount++;
            text.Append(delta);
        }

        return new TransparentProxySelfTestStream(text.ToString(), done, chunkCount);
    }

    private static string BuildResponsesRequest(string model)
        => JsonSerializer.Serialize(new
        {
            model,
            input = "RelayBench direct Responses API self test.",
            stream = false,
            max_output_tokens = 64
        });

    private static string BuildAnthropicMessagesRequest(string model)
        => JsonSerializer.Serialize(new
        {
            model,
            messages = new[]
            {
                new { role = "user", content = "RelayBench direct Anthropic Messages API self test." }
            },
            stream = false,
            max_tokens = 64
        });

    private static string BuildChatRequest(string model, string content = "RelayBench transparent proxy self test.", bool includeTools = false, bool stream = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new { role = "user", content }
            },
            ["stream"] = stream,
            ["max_tokens"] = 64
        };
        if (includeTools)
        {
            payload["tools"] = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "relaybench_cache_probe",
                        description = "Return a synthetic cache probe marker.",
                        parameters = new
                        {
                            type = "object",
                            properties = new { },
                            additionalProperties = false
                        }
                    }
                }
            };
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildChatRequestWithVolatileFields(
        string content,
        string requestId,
        string idempotencyKey,
        string traceId)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = "rb-selftest-model",
            ["request_id"] = requestId,
            ["idempotency_key"] = idempotencyKey,
            ["metadata"] = new
            {
                trace_id = traceId,
                ui_session = Guid.NewGuid().ToString("N")
            },
            ["messages"] = new[]
            {
                new { role = "user", content }
            },
            ["stream"] = false,
            ["max_tokens"] = 64
        });

    private static IReadOnlyList<string> ReadModelIds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return data.EnumerateArray()
                .Select(static item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id!.Trim())
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string ReadJsonString(byte[] json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class FakeUpstreamServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellationSource = new();
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _name;
        private readonly string _model;
        private readonly FakeUpstreamMode _mode;
        private Task? _loopTask;

        private FakeUpstreamServer(string name, string model, FakeUpstreamMode mode, int port)
        {
            _name = name;
            _model = model;
            _mode = mode;
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
        }

        public string BaseUrl { get; }

        public static Task<FakeUpstreamServer> StartAsync(
            string name,
            string model,
            FakeUpstreamMode mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = new FakeUpstreamServer(name, model, mode, GetFreeTcpPort());
            server._listener.Start();
            server._loopTask = Task.Run(() => server.RunAsync(server._cancellationSource.Token), CancellationToken.None);
            return Task.FromResult(server);
        }

        public int Count(string path)
            => _counts.TryGetValue(path, out var count) ? count : 0;

        public async ValueTask DisposeAsync()
        {
            await _cancellationSource.CancelAsync();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
            }

            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask;
                }
                catch
                {
                }
            }

            _cancellationSource.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                    _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
                }
                catch
                {
                    if (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                    {
                        return;
                    }
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            _counts.AddOrUpdate(path, 1, static (_, count) => count + 1);
            try
            {
                if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context, 200, new
                    {
                        @object = "list",
                        data = new[]
                        {
                            new { id = _model, @object = "model", created = 0, owned_by = _name },
                            new { id = "rb-selftest-hidden", @object = "model", created = 0, owned_by = _name }
                        }
                    }, cancellationToken);
                    return;
                }

                if (path.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
                {
                    if (_mode == FakeUpstreamMode.Responses)
                    {
                        if (await IsStreamRequestAsync(context.Request, cancellationToken))
                        {
                            await WriteResponsesSseAsync(context, cancellationToken);
                            return;
                        }

                        await WriteJsonAsync(context, 200, new
                        {
                            id = "resp_selftest",
                            @object = "response",
                            model = _model,
                            output = new[]
                            {
                                new
                                {
                                    type = "message",
                                    role = "assistant",
                                    content = new[]
                                    {
                                        new { type = "output_text", text = "selftest responses ok" }
                                    }
                                }
                            },
                            usage = new { input_tokens = 7, output_tokens = 3, total_tokens = 10 }
                        }, cancellationToken);
                        return;
                    }

                    await WriteProtocolErrorAsync(context, "responses", cancellationToken);
                    return;
                }

                if (path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
                {
                    if (_mode is FakeUpstreamMode.Responses or FakeUpstreamMode.AnthropicOnly)
                    {
                        if (await IsStreamRequestAsync(context.Request, cancellationToken))
                        {
                            await WriteAnthropicSseAsync(context, cancellationToken);
                            return;
                        }

                        await WriteJsonAsync(context, 200, new
                        {
                            id = "msg_selftest",
                            type = "message",
                            role = "assistant",
                            model = _model,
                            content = new[]
                            {
                                new { type = "text", text = "selftest anthropic ok" }
                            },
                            usage = new { input_tokens = 8, output_tokens = 4 }
                        }, cancellationToken);
                        return;
                    }

                    await WriteProtocolErrorAsync(context, "anthropic", cancellationToken);
                    return;
                }

                if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsStreamRequestAsync(context.Request, cancellationToken))
                    {
                        await WriteChatSseAsync(context, cancellationToken);
                        return;
                    }

                    await WriteJsonAsync(context, 200, new
                    {
                        id = "chatcmpl_selftest",
                        @object = "chat.completion",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model = _model,
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                message = new { role = "assistant", content = "selftest chat ok" },
                                finish_reason = "stop"
                            }
                        },
                        usage = new { prompt_tokens = 5, completion_tokens = 2, total_tokens = 7 }
                    }, cancellationToken);
                    return;
                }

                await WriteJsonAsync(context, 404, new { error = new { message = "not found" } }, cancellationToken);
            }
            catch
            {
                try
                {
                    context.Response.Abort();
                }
                catch
                {
                }
            }
        }

        private static async Task<bool> IsStreamRequestAsync(
            HttpListenerRequest request,
            CancellationToken cancellationToken)
        {
            if (!request.HasEntityBody)
            {
                return false;
            }

            using StreamReader reader = new(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, leaveOpen: false);
            var body = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.TryGetProperty("stream", out var stream) &&
                       stream.ValueKind == JsonValueKind.True;
            }
            catch
            {
                return false;
            }
        }

        private static async Task WriteResponsesSseAsync(
            HttpListenerContext context,
            CancellationToken cancellationToken)
            => await WriteSseFramesAsync(
                context,
                [
                    ": relaybench responses heartbeat\n\n",
                    "event: response.output_text.delta\n" +
                    SseData(new { type = "response.output_text.delta", delta = "stream responses " }),
                    SseData(new { type = "response.output_text.delta", delta = "ok" }),
                    SseData(new { type = "response.completed" })
                ],
                cancellationToken);

        private static async Task WriteAnthropicSseAsync(
            HttpListenerContext context,
            CancellationToken cancellationToken)
            => await WriteSseFramesAsync(
                context,
                [
                    "event: message_start\n" +
                    SseData(new { type = "message_start", message = new { id = "msg_selftest_stream", type = "message" } }),
                    "event: content_block_delta\n" +
                    SseData(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "stream anthropic " } }),
                    "event: ping\n" +
                    SseData(new { type = "ping" }),
                    "event: content_block_delta\n" +
                    SseData(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "ok" } }),
                    "event: message_stop\n" +
                    SseData(new { type = "message_stop" })
                ],
                cancellationToken);

        private static async Task WriteChatSseAsync(
            HttpListenerContext context,
            CancellationToken cancellationToken)
            => await WriteSseFramesAsync(
                context,
                [
                    SseData(new
                    {
                        id = "chatcmpl_selftest_stream",
                        @object = "chat.completion.chunk",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model = "chat-only",
                        choices = new[] { new { index = 0, delta = new { content = "stream chat ok" }, finish_reason = (string?)null } }
                    }),
                    "data: [DONE]\n\n"
                ],
                cancellationToken);

        private static string SseData(object payload)
            => $"data: {JsonSerializer.Serialize(payload)}\n\n";

        private static async Task WriteSseFramesAsync(
            HttpListenerContext context,
            IReadOnlyList<string> frames,
            CancellationToken cancellationToken)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.SendChunked = true;
            foreach (var frame in frames)
            {
                await WriteFragmentedUtf8Async(context.Response.OutputStream, frame, cancellationToken);
            }

            context.Response.OutputStream.Close();
        }

        private static async Task WriteFragmentedUtf8Async(
            Stream outputStream,
            string text,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var offset = 0;
            var chunkSize = 7;
            while (offset < bytes.Length)
            {
                var count = Math.Min(chunkSize, bytes.Length - offset);
                await outputStream.WriteAsync(bytes.AsMemory(offset, count), cancellationToken);
                await outputStream.FlushAsync(cancellationToken);
                offset += count;
                chunkSize = chunkSize == 7 ? 11 : 7;
            }
        }

        private static async Task WriteProtocolErrorAsync(
            HttpListenerContext context,
            string protocol,
            CancellationToken cancellationToken)
            => await WriteJsonAsync(
                context,
                404,
                new { error = new { message = $"{protocol} is not supported by this self-test upstream" } },
                cancellationToken);

        private static async Task WriteJsonAsync(
            HttpListenerContext context,
            int statusCode,
            object payload,
            CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.OutputStream.Close();
        }
    }

    private enum FakeUpstreamMode
    {
        Responses,
        AnthropicOnly,
        ChatOnly
    }
}

internal sealed record TransparentProxySelfTestStream(string Text, bool Done, int ChunkCount);

public sealed record TransparentProxySelfTestResult(
    bool Success,
    string Summary,
    IReadOnlyList<TransparentProxySelfTestCheck> Checks);

public sealed record TransparentProxySelfTestCheck(
    string Name,
    bool Passed,
    string Detail)
{
    public string DisplayText
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{(Passed ? "PASS" : "FAIL")} {Name}: {Detail}");
}
