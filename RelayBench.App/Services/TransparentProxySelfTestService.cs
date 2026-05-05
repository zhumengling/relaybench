using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.App.Infrastructure;
using RelayBench.App.ViewModels;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

public sealed class TransparentProxySelfTestService
{
    public async Task<TransparentProxySelfTestResult> RunAsync(CancellationToken cancellationToken = default)
    {
        List<TransparentProxySelfTestCheck> checks = [];
        CheckConfigStore(checks);
        CheckAppDetector(checks);
        CheckCaptureArtifactStore(checks);
        CheckCliEnvironmentService(checks);
        CheckLaunchWrapperService(checks);
        CheckCodexConfigService(checks);
        CheckClaudeConfigService(checks);
        CheckVsCodeSettingsService(checks);
        CheckTunForwardProxyAndLegacyRecoveryGuard(checks);
        CheckTunGenerationAndNetworkGuard(checks);
        CheckPortInspectorService(checks);
        await CheckUnifiedEndpointWithoutRoutesAsync(checks, cancellationToken);
        CheckRouteTextCodec(checks);
        CheckPersistentResponseCache(checks);
        CheckPromptSessionCache(checks);
        CheckAnthropicCacheControlOptimizer(checks);
        await CheckPersistentLogStoreAsync(checks, cancellationToken);
        CheckRouteHealthStore(checks);
        CheckWireProtocolRegistry(checks);
        CheckResponsesTextExtraction(checks);
        CheckTranslatorRouteOptions(checks);
        CheckUsageTokenExtraction(checks);
        CheckUsageEventQueue(checks);
        CheckMetricsService(checks);
        CheckGatewayManagementAndCooldown(checks);
        CheckSessionAffinityKeyService(checks);
        CheckSchedulerCooldownAndAffinity(checks);
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
        await CheckThroughputBenchmarkAsync(responsesUpstream, checks, cancellationToken);

        var runtimeCachePath = Path.Combine(Path.GetTempPath(), $"relaybench-transparent-proxy-runtime-cache-{Guid.NewGuid():N}.sqlite");
        await using var proxy = new TransparentProxyService(new TransparentProxyResponseCacheService(runtimeCachePath));
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
            DeleteSqliteDatabaseFiles(runtimeCachePath);
        }

        var passed = checks.Count(check => check.Passed);
        var summary = checks.All(check => check.Passed)
            ? $"\u900f\u660e\u4ee3\u7406\u672c\u5730\u81ea\u68c0\u901a\u8fc7\uff1a{passed}/{checks.Count} \u9879\u3002Responses\u3001Anthropic fallback\u3001OpenAI Chat fallback\u3001\u6a21\u578b\u805a\u5408\u548c\u77ed\u7f13\u5b58\u5747\u6b63\u5e38\u3002"
            : $"\u900f\u660e\u4ee3\u7406\u672c\u5730\u81ea\u68c0\u672a\u5b8c\u5168\u901a\u8fc7\uff1a{passed}/{checks.Count} \u9879\u3002\u5931\u8d25\uff1a{string.Join("\uff1b", checks.Where(check => !check.Passed).Select(check => check.Name))}";
        return new TransparentProxySelfTestResult(checks.All(check => check.Passed), summary, checks);
    }

    private static void CheckResponsesTextExtraction(List<TransparentProxySelfTestCheck> checks)
    {
        const string officialResponsesShape = """
            {
              "id": "resp_selftest",
              "object": "response",
              "status": "completed",
              "output": [
                {
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    { "type": "output_text", "text": "proxy-ok" }
                  ]
                }
              ],
              "usage": { "input_tokens": 5, "output_tokens": 2 }
            }
            """;
        const string nestedTextValueShape = """
            {
              "id": "resp_nested",
              "object": "response",
              "status": "completed",
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": { "value": "proxy-ok" } }
                  ]
                }
              ]
            }
            """;
        const string chatDeltaShape = """
            {
              "choices": [
                { "delta": { "content": "proxy-ok" } }
              ]
            }
            """;

        var official = ModelResponseTextExtractor.TryExtractAssistantText(officialResponsesShape);
        var nested = ModelResponseTextExtractor.TryExtractAssistantText(nestedTextValueShape);
        var chatDelta = ModelResponseTextExtractor.TryExtractAssistantText(chatDeltaShape);

        checks.Add(new(
            "Responses 文本提取",
            string.Equals(official, "proxy-ok", StringComparison.Ordinal) &&
            string.Equals(nested, "proxy-ok", StringComparison.Ordinal) &&
            string.Equals(chatDelta, "proxy-ok", StringComparison.Ordinal),
            $"Responses 探针应能从标准 output/content、text.value 变体和 Chat delta 中提取 assistant 文本。official={official ?? "-"}，nested={nested ?? "-"}，chat={chatDelta ?? "-"}。"));
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
                !migrated.StartUnifiedEndpointOnLaunch &&
                !migrated.EnableAppCapture &&
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

    private static void CheckUsageTokenExtraction(List<TransparentProxySelfTestCheck> checks)
    {
        const string chatCompletionsUsage = "{\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":72,\"total_tokens\":81}}";
        const string responsesUsage = "{\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":9,\"output_tokens\":72,\"total_tokens\":81}}}";
        const string anthropicUsage = "{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":72}}";
        const string delta = "{\"choices\":[{\"delta\":{\"content\":\"hello world\"}}]}";

        var parserOk =
            ChatSseParser.TryExtractOutputTokenCount(chatCompletionsUsage, out var chatTokens) &&
            chatTokens == 72 &&
            ChatSseParser.TryExtractOutputTokenCount(responsesUsage, out var responsesTokens) &&
            responsesTokens == 72 &&
            ChatSseParser.TryExtractOutputTokenCount(anthropicUsage, out var anthropicTokens) &&
            anthropicTokens == 72;

        var telemetry = new TransparentProxyTokenTelemetryService();
        var tracker = telemetry.CreateStreamTracker();
        tracker.TrackSseData(delta);
        tracker.TrackSseData(chatCompletionsUsage);
        var snapshot = telemetry.CreateSnapshot();

        checks.Add(new(
            "Usage Token 统一解析",
            parserOk && snapshot.TotalOutputTokens == 72,
            "对话页和透明代理悬浮窗应优先使用上游 usage 中的真实 output token，并把流式估算值在结束包处校正。"));
    }

    private static void CheckSchedulerCooldownAndAffinity(List<TransparentProxySelfTestCheck> checks)
    {
        var routeA = new TransparentProxyRoute(
            "scheduler-a",
            "Scheduler A",
            "http://127.0.0.1:19101",
            "sk-a",
            "shared-model",
            models: ["shared-model"],
            priority: 1);
        var routeB = new TransparentProxyRoute(
            "scheduler-b",
            "Scheduler B",
            "http://127.0.0.1:19102",
            "sk-b",
            "shared-model",
            models: ["shared-model"],
            priority: 2);
        var config = new TransparentProxyServerConfig(
            19100,
            [routeA, routeB],
            RateLimitPerMinute: 60,
            MaxConcurrency: 2,
            EnableFallback: true,
            EnableCache: true,
            CacheTtlSeconds: 60,
            RewriteModel: false,
            IgnoreTlsErrors: true,
            UpstreamTimeoutSeconds: 5)
        {
            RouteStrategy = TransparentProxyRouteStrategies.SessionAffinity,
            SessionAffinityTtlSeconds = 60,
            ModelCooldownSeconds = 30
        };
        Dictionary<string, TransparentProxyRouteRuntimeState> states = new(StringComparer.OrdinalIgnoreCase)
        {
            [routeA.Id] = new(routeA),
            [routeB.Id] = new(routeB)
        };
        var circuitBreaker = new TransparentProxyCircuitBreakerService();
        var cooldown = new TransparentProxyCooldownService();
        var scheduler = new TransparentProxySchedulerService();
        var cursor = 0;
        var stickyCandidates = scheduler.BuildCandidateRoutes(
            config,
            "shared-model",
            routeB.Id,
            states,
            circuitBreaker,
            DateTimeOffset.UtcNow,
            ref cursor);

        cooldown.RecordModelFailure(states[routeA.Id], "shared-model", 404, null, config.ModelCooldownSeconds);
        var modelCooling = cooldown.IsModelCooling(states[routeA.Id], "shared-model", DateTimeOffset.UtcNow, out var modelCooldownUntil);
        var permit = new TransparentProxyRoutePermit(routeA.Id, UsedHalfOpenPermit: false);
        var routeCooldown = circuitBreaker.RecordFailure(
            states[routeA.Id],
            429,
            latencyMs: 12,
            permit,
            retryAfter: TimeSpan.FromSeconds(17));
        var routeCooling = routeCooldown.Opened &&
                           !circuitBreaker.IsRouteAvailable(states[routeA.Id], DateTimeOffset.UtcNow);
        cooldown.RecordModelSuccess(states[routeA.Id], "shared-model");
        var modelCleared = !cooldown.IsModelCooling(states[routeA.Id], "shared-model", DateTimeOffset.UtcNow, out _);

        checks.Add(new(
            "调度粘滞与冷却",
            stickyCandidates.FirstOrDefault()?.Id == routeB.Id &&
            modelCooling &&
            modelCooldownUntil > DateTimeOffset.UtcNow &&
            routeCooling &&
            modelCleared,
            "Session affinity 应优先绑定节点；model_not_found/429 应进入模型级或路由级冷却，成功后清除模型冷却。"));
    }

    private static void CheckSessionAffinityKeyService(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            var service = new TransparentProxySessionAffinityKeyService();
            static byte[] Body(object payload) => JsonSerializer.SerializeToUtf8Bytes(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            static Func<string, string?> Headers(IReadOnlyDictionary<string, string> values)
                => name => values.TryGetValue(name, out var value) ? value : null;

            var metadataKey = service.Build(
                Headers(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Session-ID"] = "header-session"
                }),
                Body(new
                {
                    model = "gpt-5.5",
                    metadata = new
                    {
                        user_id = "metadata-user"
                    },
                    messages = new[]
                    {
                        new { role = "user", content = "hello" }
                    }
                }));
            var ampKey = service.Build(
                Headers(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Amp-Thread-Id"] = "amp-thread"
                }),
                Body(new
                {
                    model = "gpt-5.5",
                    messages = new[]
                    {
                        new { role = "user", content = "same text" }
                    }
                }));
            var clientRequestKey = service.Build(
                Headers(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Client-Request-Id"] = "client-request"
                }),
                Body(new
                {
                    model = "gpt-5.5",
                    messages = new[]
                    {
                        new { role = "user", content = "same text" }
                    }
                }));
            var firstModelSession = service.Build(
                Headers(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Session_id"] = "shared-session"
                }),
                Body(new { model = "model-a", input = "hi" }));
            var secondModelSession = service.Build(
                Headers(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Session_id"] = "shared-session"
                }),
                Body(new { model = "model-b", input = "hi" }));

            checks.Add(new(
                "Session affinity key extraction",
                metadataKey == "gpt-5.5\u001Fmetadata-user" &&
                ampKey == "gpt-5.5\u001Famp-thread" &&
                clientRequestKey == "gpt-5.5\u001Fclient-request" &&
                firstModelSession == "model-a\u001Fshared-session" &&
                secondModelSession == "model-b\u001Fshared-session" &&
                !string.Equals(firstModelSession, secondModelSession, StringComparison.Ordinal),
                "Session affinity key 应按文档优先读取 metadata.user_id、Session_id、X-Amp-Thread-Id、X-Client-Request-Id，并用模型隔离同名会话。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Session affinity key extraction",
                false,
                $"Session affinity key self-test failed: {ex.Message}"));
        }
    }

    private static void CheckGatewayManagementAndCooldown(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            var gateway = new TransparentProxyGatewayService();
            var route = new TransparentProxyRoute(
                "management-route",
                "Management Route",
                "http://127.0.0.1:19201",
                "sk-management-secret",
                "management-model",
                models: ["management-model"]);
            var config = new TransparentProxyServerConfig(
                19200,
                [route],
                RateLimitPerMinute: 60,
                MaxConcurrency: 2,
                EnableFallback: true,
                EnableCache: true,
                CacheTtlSeconds: 60,
                RewriteModel: false,
                IgnoreTlsErrors: true,
                UpstreamTimeoutSeconds: 5);
            var metrics = new TransparentProxyMetricsSnapshot(
                false,
                config.Port,
                0,
                1,
                1,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                [
                    new TransparentProxyRouteMetrics(
                        route.Id,
                        route.Name,
                        1,
                        1,
                        0,
                        200,
                        12,
                        0,
                        1,
                        TransparentProxyCircuitState.Closed.ToString(),
                        DateTimeOffset.MinValue,
                        DateTimeOffset.UtcNow,
                        ProxyWireApiProbeService.ResponsesWireApi,
                        true,
                        true,
                        false,
                        DateTimeOffset.UtcNow)
                ],
                0,
                0d,
                null);
            var managementApi = new TransparentProxyManagementApiService();
            var cachePayload = JsonSerializer.Serialize(managementApi.BuildCachePayload(
                new TransparentProxyCacheStats(
                    ResponseEntries: 1,
                    ModelListEntries: 1,
                    Hits: 3,
                    Misses: 1,
                    Stores: 1,
                    Evictions: 0,
                    ModelListHits: 2,
                    ModelListMisses: 0,
                    InFlightKeys: 1,
                    LeaseWaits: 4),
                new TransparentProxyPromptSessionCacheStats(Entries: 1, Hits: 2, Misses: 1),
                config,
                new { ok = true }));
            var routePayload = JsonSerializer.Serialize(managementApi.BuildRoutesPayload(false, config, metrics));
            var protocolPayload = JsonSerializer.Serialize(managementApi.BuildProtocolsPayload(config.Routes));
            var captureAppsPayload = JsonSerializer.Serialize(managementApi.BuildCaptureAppsPayload(
                [
                    new TransparentProxyDetectedApp(
                        "codex-cli",
                        "Codex CLI",
                        "Codex config",
                        "detected",
                        "C:\\Tools\\codex.exe",
                        "C:\\Users\\relaybench\\.codex\\config.toml")
                ],
                metrics));
            var networkGuard = new TransparentProxyNetworkGuardService();
            var tunService = new TransparentProxyTunService();
            var tunConfig = tunService.BuildMihomoConfig(new TransparentProxyTunConfigOptions(19200, 19201, 19202, 19203, 19204));
            var captureDiagnosticsPayload = JsonSerializer.Serialize(managementApi.BuildCaptureDiagnosticsPayload(
                false,
                config,
                [new TransparentProxyDetectedApp("claude-cli", "Claude CLI", "settings env", "detected", null, "C:\\Users\\relaybench\\.claude\\settings.json")],
                networkGuard.Inspect(),
                networkGuard.ValidateMihomoConfig(tunConfig),
                tunService.InspectResidualSession(),
                new TransparentProxySystemProxyInspection(
                    true,
                    false,
                    string.Empty,
                    false,
                    string.Empty,
                    string.Empty,
                    "系统代理：未由 RelayBench 接管。"),
                new TransparentProxyPortInspectionResult(19200, true, 4321, "RelayBench", "127.0.0.1:19200", "端口 19200 已被 RelayBench (PID 4321) 占用，监听 127.0.0.1:19200。"),
                new TransparentProxyCliEnvironmentService().Build(19200),
                [new TransparentProxyLaunchWrapperArtifact("codex-cli", "Codex CLI", "C:\\relaybench\\codex-cli.ps1", "C:\\relaybench\\codex-cli.cmd", true, true)],
                "127.0.0.1:19201",
                "127.0.0.1:19202",
                "127.0.0.1:19203"));
            var captureRecoveryPayload = JsonSerializer.Serialize(managementApi.BuildCaptureRecoveryPayload(
                false,
                [
                    new TransparentProxyCaptureRecoveryItem(
                        "codex-cli",
                        "Codex CLI",
                        "preview",
                        true,
                        "preview only",
                        "C:\\Users\\relaybench\\.codex\\config.toml",
                        null)
                ]));

            var cooldown = new TransparentProxyCooldownService();
            var state = new TransparentProxyRouteRuntimeState(route);
            var modelCooled = cooldown.RecordModelFailure(state, "management-model", 404, null, defaultCooldownSeconds: 30) &&
                              cooldown.IsModelCooling(state, "management-model", DateTimeOffset.UtcNow, out _);
            cooldown.RecordModelSuccess(state, "management-model");
            var modelCleared = !cooldown.IsModelCooling(state, "management-model", DateTimeOffset.UtcNow, out _);

            checks.Add(new(
                "Gateway / Management / Cooldown services",
                gateway.IsCorsPreflight("OPTIONS") &&
                gateway.TryResolveManagementEndpoint("/relaybench/usage?limit=8", out var usageEndpoint) &&
                usageEndpoint == TransparentProxyManagementEndpoint.Usage &&
                gateway.TryResolveManagementEndpoint("/relaybench/ingress", out var ingressEndpoint) &&
                ingressEndpoint == TransparentProxyManagementEndpoint.Ingress &&
                gateway.TryResolveManagementEndpoint("/relaybench/capture/apps", out var captureAppsEndpoint) &&
                captureAppsEndpoint == TransparentProxyManagementEndpoint.CaptureApps &&
                gateway.TryResolveManagementEndpoint("/relaybench/capture/diagnostics", out var captureDiagnosticsEndpoint) &&
                captureDiagnosticsEndpoint == TransparentProxyManagementEndpoint.CaptureDiagnostics &&
                gateway.TryResolveManagementEndpoint("/relaybench/capture/recovery?execute=false", out var captureRecoveryEndpoint) &&
                captureRecoveryEndpoint == TransparentProxyManagementEndpoint.CaptureRecovery &&
                gateway.TryResolveManagementEndpoint("/relaybench/scheduler?model=management-model", out var schedulerEndpoint) &&
                schedulerEndpoint == TransparentProxyManagementEndpoint.Scheduler &&
                gateway.CreateRequestId().StartsWith("rb-", StringComparison.Ordinal) &&
                cachePayload.Contains("\"hitRate\":75", StringComparison.Ordinal) &&
                routePayload.Contains("management-route", StringComparison.Ordinal) &&
                routePayload.Contains("sk-management-secret", StringComparison.Ordinal) is false &&
                protocolPayload.Contains("Responses", StringComparison.Ordinal) &&
                captureAppsPayload.Contains("codex-cli", StringComparison.Ordinal) &&
                captureDiagnosticsPayload.Contains("oneClickRecoveryAvailable", StringComparison.Ordinal) &&
                captureDiagnosticsPayload.Contains("cliEnvironment", StringComparison.Ordinal) &&
                captureDiagnosticsPayload.Contains("launchWrappers", StringComparison.Ordinal) &&
                captureDiagnosticsPayload.Contains("portInspection", StringComparison.Ordinal) &&
                captureDiagnosticsPayload.Contains("residualSession", StringComparison.Ordinal) &&
                captureDiagnosticsPayload.Contains("legacySystemProxy", StringComparison.Ordinal) &&
                captureRecoveryPayload.Contains("\"executed\":false", StringComparison.Ordinal) &&
                cooldown.Classify(429, "quota exceeded") == TransparentProxyCooldownKind.Quota &&
                modelCooled &&
                modelCleared,
                "Gateway、Management API 与模型级冷却应从主代理服务中独立出来，并保持管理端点、脱敏和冷却分类可验证。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Gateway / Management / Cooldown services",
                false,
                $"Gateway / Management / Cooldown self-test failed: {ex.Message}"));
        }
    }

    private static void CheckUsageEventQueue(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            TransparentProxyUsageEvent? latestEvent = null;
            var telemetry = new TransparentProxyTokenTelemetryService();
            telemetry.UsageEvents.UsageEmitted += (_, usageEvent) => latestEvent = usageEvent;

            using (telemetry.PushUsageContext("gpt-selftest", "selftest-route", "responses", "upstream", "AppCapture", "Codex CLI", "Codex config"))
            {
                var tracker = telemetry.CreateStreamTracker();
                tracker.TrackSseData("{\"choices\":[{\"delta\":{\"content\":\"hello relaybench\"}}]}");
                tracker.TrackSseData("{\"choices\":[],\"usage\":{\"completion_tokens\":72}}");
                telemetry.TrackPromptCache("{\"usage\":{\"input_tokens_details\":{\"cached_tokens\":12}}}");
            }

            var snapshot = telemetry.CreateSnapshot();
            var events = telemetry.UsageEvents.Snapshot(16);
            var hasEstimate = events.Any(static item => item.Kind == TransparentProxyUsageEventKind.OutputDelta && item.Estimated);
            var hasReconciliation = events.Any(static item => item.Kind == TransparentProxyUsageEventKind.OutputReconciled && !item.Estimated);
            var hasPromptCache = events.Any(static item => item.Kind == TransparentProxyUsageEventKind.PromptCache && item.PromptCacheTokenDelta == 12);
            var hasSource = events.Any(static item =>
                item.SourceApplication == "Codex CLI" &&
                item.IngressKind == "AppCapture" &&
                item.CaptureMode == "Codex config");
            telemetry.Reset();
            var resetEvents = telemetry.UsageEvents.Snapshot(4);

            checks.Add(new(
                "Usage event queue",
                snapshot.TotalOutputTokens == 72 &&
                snapshot.PromptCacheTokens == 12 &&
                latestEvent is not null &&
                hasEstimate &&
                hasReconciliation &&
                hasPromptCache &&
                hasSource &&
                resetEvents.Count == 1 &&
                resetEvents[0].Kind == TransparentProxyUsageEventKind.Reset,
                "Token 遥测应把流式估算、真实 usage 校正和 prompt cache token 发布到同一个有界事件队列，供 UI、日志和悬浮窗共享。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Usage event queue",
                false,
                $"Usage event queue self-test failed: {ex.Message}"));
        }
    }

    private static void CheckMetricsService(List<TransparentProxySelfTestCheck> checks)
    {
        var metrics = new TransparentProxyMetricsService();
        metrics.TrackLatency(10);
        metrics.TrackLatency(20);
        metrics.TrackLatency(30);

        var snapshot = metrics.CreateSnapshot(
            isRunning: true,
            config: new TransparentProxyServerConfig(
                Port: 19001,
                Routes: Array.Empty<TransparentProxyRoute>(),
                RateLimitPerMinute: 60,
                MaxConcurrency: 4,
                EnableFallback: true,
                EnableCache: true,
                CacheTtlSeconds: 60,
                RewriteModel: false,
                IgnoreTlsErrors: true,
                UpstreamTimeoutSeconds: 15),
            counters: new TransparentProxyMetricsCounters(
                ActiveRequests: 1,
                TotalRequests: 3,
                SuccessRequests: 2,
                FailedRequests: 1,
                FallbackRequests: 1,
                CacheHits: 1,
                RateLimitedRequests: 0),
            routes: Array.Empty<TransparentProxyRouteMetrics>(),
            tokenSnapshot: new TransparentProxyTokenTelemetrySnapshot(
                TotalOutputTokens: 128,
                TokensPerSecond: 42.5,
                LastTokenActivityAt: DateTimeOffset.UtcNow,
                PromptCacheTokens: 16),
            cacheStats: new TransparentProxyCacheStats(
                ResponseEntries: 2,
                ModelListEntries: 1,
                Hits: 5,
                Misses: 2,
                Stores: 3,
                Evictions: 1,
                ModelListHits: 4,
                ModelListMisses: 1,
                InFlightKeys: 1,
                LeaseWaits: 2),
            promptSessionStats: new TransparentProxyPromptSessionCacheStats(
                Entries: 1,
                Hits: 2,
                Misses: 1),
            modelPools: Array.Empty<TransparentProxyModelPoolSnapshot>(),
            usageEvents: Array.Empty<TransparentProxyUsageEvent>(),
            ingressMetrics:
            [
                new TransparentProxyIngressMetricsSnapshot(
                    "UnifiedLocalEndpoint",
                    "本地统一出口",
                    "显式 Base URL",
                    Requests: 3,
                    Successes: 2,
                    Failures: 1,
                    TunnelOnlyRequests: 0,
                    OutputTokens: 128,
                    PromptCacheTokens: 16,
                    LastRequestAt: DateTimeOffset.UtcNow,
                    LastTokenActivityAt: DateTimeOffset.UtcNow)
            ]);

        checks.Add(new(
            "Metrics service",
            snapshot.IsRunning &&
            snapshot.Port == 19001 &&
            snapshot.ActiveRequests == 1 &&
            snapshot.TotalRequests == 3 &&
            snapshot.P50LatencyMs == 20 &&
            snapshot.P95LatencyMs == 30 &&
            snapshot.CacheEntryCount == 4 &&
            snapshot.TotalOutputTokens == 128 &&
            snapshot.ResponseCacheLeaseWaits == 2 &&
            snapshot.Ingresses?.Count == 1 &&
            snapshot.Ingresses[0].OutputTokens == 128,
            "透明代理指标、P50/P95 延迟窗口、缓存统计和 Token 快照应由独立 Metrics 服务聚合，主代理只负责编排。"));
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

    private static void CheckAnthropicCacheControlOptimizer(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            var optimizer = new TransparentProxyPromptCacheOptimizer();
            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            var body = Encoding.UTF8.GetBytes("""
                {
                  "model": "anthropic-cache-model",
                  "tools": [
                    { "name": "tool_a", "description": "first" },
                    { "name": "tool_b", "description": "second" }
                  ],
                  "system": "RelayBench cache control system prompt.",
                  "messages": [
                    { "role": "user", "content": [{ "type": "text", "text": "first user turn" }] },
                    { "role": "assistant", "content": [
                      { "type": "thinking", "thinking": "internal" },
                      { "type": "text", "text": "stable assistant prefix" }
                    ] },
                    { "role": "user", "content": [{ "type": "text", "text": "second user turn" }] }
                  ]
                }
                """);
            var optimized = optimizer.Apply(
                body,
                new TransparentProxyPromptSessionMaterial("prompt-key", "session-id", "anthropic-cache-model", "selftest"),
                ProxyWireApiProbeService.AnthropicMessagesWireApi,
                headers,
                out _);

            using var document = JsonDocument.Parse(optimized);
            var root = document.RootElement;
            var cacheControlCount = CountJsonProperty(root, "cache_control");
            var toolsOk =
                root.TryGetProperty("tools", out var tools) &&
                tools.ValueKind == JsonValueKind.Array &&
                tools.GetArrayLength() == 2 &&
                tools[1].TryGetProperty("cache_control", out _);
            var systemOk =
                root.TryGetProperty("system", out var system) &&
                system.ValueKind == JsonValueKind.Array &&
                system.GetArrayLength() == 1 &&
                system[0].TryGetProperty("cache_control", out _);
            var assistantOk = TryFindAssistantTextCacheControl(root);

            checks.Add(new(
                "Anthropic cache_control optimizer",
                cacheControlCount <= 4 && toolsOk && systemOk && assistantOk,
                $"Anthropic prompt cache optimizer should add stable cache_control breakpoints for tools, system and the latest assistant text block without exceeding four blocks. count={cacheControlCount}."));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Anthropic cache_control optimizer",
                false,
                $"Anthropic cache_control optimizer self-test failed: {ex.Message}"));
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
                    "SelfTest Responses/Responses:200",
                    "AppCapture",
                    "Codex CLI",
                    "Codex config",
                    "api.openai.com",
                    false),
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
                entries[0].IngressKind == "AppCapture" &&
                entries[0].SourceApplication == "Codex CLI" &&
                entries[0].CaptureMode == "Codex config" &&
                entries[0].TargetHost == "api.openai.com" &&
                afterClear.Count == 0 &&
                File.Exists(exportPath) &&
                exported.Contains("source_application", StringComparison.OrdinalIgnoreCase) &&
                exported.Contains("Codex CLI", StringComparison.OrdinalIgnoreCase) &&
                !combined.Contains("sk-selftest-secret", StringComparison.OrdinalIgnoreCase) &&
                !combined.Contains("raw-token", StringComparison.OrdinalIgnoreCase),
                "Transparent proxy logs should persist ingress/source fields to SQLite, export CSV, clear on demand and redact secrets before storage."));
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

    private static void CheckAppDetector(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            var apps = new TransparentProxyAppDetectorService().Detect();
            var ids = apps.Select(static app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            checks.Add(new(
                "AI 应用接管检测",
                apps.Count == 4 &&
                ids.Contains("codex-cli") &&
                ids.Contains("claude-cli") &&
                ids.Contains("vs-codex") &&
                ids.Contains("codex-desktop"),
                "AI 应用接管应能稳定产出 Codex、VS Codex、Codex CLI 和 Claude CLI 的检测状态，即使应用未安装也不能报错。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "AI 应用接管检测",
                false,
                $"AI 应用检测自测失败：{ex.Message}"));
        }
    }

    private static void CheckCaptureArtifactStore(List<TransparentProxySelfTestCheck> checks)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-capture-artifacts-{Guid.NewGuid():N}");
        var appDataDirectory = Path.Combine(rootDirectory, "AppData");
        try
        {
            var codexConfigPath = Path.Combine(rootDirectory, ".codex", "config.toml");
            var claudeSettingsPath = Path.Combine(rootDirectory, ".claude", "settings.json");
            var vsCodeSettingsPath = Path.Combine(appDataDirectory, "Code", "User", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(codexConfigPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(claudeSettingsPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(vsCodeSettingsPath)!);
            File.WriteAllText(codexConfigPath, "model_provider = \"relaybench\"");
            File.WriteAllText(claudeSettingsPath, "{}");
            File.WriteAllText(vsCodeSettingsPath, "{}");
            File.WriteAllText($"{codexConfigPath}.relaybench-app-capture-backup-20260505010101", "codex old");
            File.WriteAllText($"{codexConfigPath}.relaybench-app-capture-backup-20260505020202", "codex newer");
            File.WriteAllText($"{claudeSettingsPath}.relaybench-app-capture-backup-20260505010101", "claude old");

            var artifacts = new TransparentProxyCaptureArtifactStore(rootDirectory, appDataDirectory)
                .ScanDefaultArtifacts();
            var codex = artifacts.FirstOrDefault(static item => item.TargetId == "codex-cli");
            var claude = artifacts.FirstOrDefault(static item => item.TargetId == "claude-cli");
            var vsCode = artifacts.FirstOrDefault(static item => item.TargetId == "vs-codex");

            checks.Add(new(
                "接管恢复点索引",
                artifacts.Count >= 3 &&
                codex is { BackupCount: 2, TargetExists: true, LatestBackupPath: not null } &&
                codex.LatestBackupPath.EndsWith("20260505020202", StringComparison.OrdinalIgnoreCase) &&
                claude is { BackupCount: 1, TargetExists: true } &&
                vsCode is { BackupCount: 0, TargetExists: true, Status: "missing" },
                "应用接管恢复点应能按 Codex、Claude、VS Code 配置文件索引最近备份，供 UI 诊断和管理 API 预览使用。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "接管恢复点索引",
                false,
                $"接管恢复点索引自测失败：{ex.Message}"));
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

    private static void CheckCliEnvironmentService(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            var snapshot = new TransparentProxyCliEnvironmentService().Build(19200);
            checks.Add(new(
                "CLI 临时环境接入片段",
                snapshot.OpenAiBaseUrl == "http://127.0.0.1:19200/v1" &&
                snapshot.AnthropicBaseUrl == "http://127.0.0.1:19200" &&
                snapshot.PowerShell.Contains("OPENAI_BASE_URL", StringComparison.OrdinalIgnoreCase) &&
                snapshot.PowerShell.Contains("ANTHROPIC_BASE_URL", StringComparison.OrdinalIgnoreCase) &&
                snapshot.Cmd.Contains("set RELAYBENCH_BASE_URL=", StringComparison.OrdinalIgnoreCase) &&
                snapshot.Notes.Contains("do not change system proxy", StringComparison.OrdinalIgnoreCase),
                "本地 agent 和临时终端应能通过一次性环境变量片段接入统一出口，不修改系统代理设置。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "CLI 临时环境接入片段",
                false,
                $"CLI 环境片段自测失败：{ex.Message}"));
        }
    }

    private static void CheckLaunchWrapperService(List<TransparentProxySelfTestCheck> checks)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-launch-wrapper-{Guid.NewGuid():N}");
        try
        {
            var service = new TransparentProxyLaunchWrapperService(rootDirectory);
            var preview = service.Preview("codex-cli", "Codex CLI", "codex", 19200);
            var write = service.Write("claude-cli", "Claude CLI", "claude", 19200);
            var artifacts = service.ScanKnownLaunchers();
            var powerShell = File.ReadAllText(write.PowerShellPath);
            var cmd = File.ReadAllText(write.CmdPath);
            var cleanup = service.DeleteKnownLaunchers();

            checks.Add(new(
                "CLI 临时启动器",
                preview.PowerShellScript.Contains("$env:OPENAI_BASE_URL", StringComparison.OrdinalIgnoreCase) &&
                preview.PowerShellScript.Contains("& 'codex' @args", StringComparison.OrdinalIgnoreCase) &&
                write.Succeeded &&
                artifacts.Any(static item => item.Id == "claude-cli" && item.ExistingCount == 2) &&
                powerShell.Contains("ANTHROPIC_BASE_URL", StringComparison.OrdinalIgnoreCase) &&
                cmd.Contains("set OPENAI_BASE_URL=http://127.0.0.1:19200/v1", StringComparison.OrdinalIgnoreCase) &&
                !cmd.Contains("HTTP_PROXY", StringComparison.OrdinalIgnoreCase) &&
                cleanup.Succeeded &&
                cleanup.DeletedCount == 2 &&
                !File.Exists(write.PowerShellPath) &&
                !File.Exists(write.CmdPath),
                "CLI 临时启动器应生成只影响子进程的 PowerShell/CMD wrapper，不修改系统代理或全局环境，并能被一键恢复清理。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "CLI 临时启动器",
                false,
                $"CLI 临时启动器自测失败：{ex.Message}"));
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

    private static void CheckCodexConfigService(List<TransparentProxySelfTestCheck> checks)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-codex-config-{Guid.NewGuid():N}");
        try
        {
            var service = new TransparentProxyCodexConfigService(rootDirectory);
            var configPath = Path.Combine(rootDirectory, ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            const string originalConfig = """
                model_provider = "other"
                model = "gpt-4.1"

                [model_providers.other]
                name = "Other"
                base_url = "https://example.invalid/v1"
                wire_api = "chat"
                """;
            File.WriteAllText(configPath, originalConfig);

            var preview = service.Preview(
                "http://127.0.0.1:17880",
                "gpt-5.4",
                "responses");
            var firstApply = service.Apply(
                "http://127.0.0.1:17880",
                "gpt-5.4",
                "responses");
            var appliedConfig = File.ReadAllText(configPath);
            var backupCountAfterFirstApply = Directory
                .GetFiles(Path.GetDirectoryName(configPath)!, "config.toml.relaybench-app-capture-backup-*")
                .Length;
            var secondApply = service.Apply(
                "http://127.0.0.1:17880/v1",
                "gpt-5.4",
                "responses");
            var backupCountAfterSecondApply = Directory
                .GetFiles(Path.GetDirectoryName(configPath)!, "config.toml.relaybench-app-capture-backup-*")
                .Length;
            File.AppendAllText(configPath, $"{Environment.NewLine}[custom.after_apply]{Environment.NewLine}value = \"keep\"{Environment.NewLine}");
            var restore = service.RestoreLatestBackup();
            var restoredConfig = File.ReadAllText(configPath);

            checks.Add(new(
                "Codex CLI 接管配置",
                preview.Changed &&
                preview.PreviewText.Contains("[model_providers.relaybench]", StringComparison.OrdinalIgnoreCase) &&
                firstApply.Succeeded &&
                !string.IsNullOrWhiteSpace(firstApply.BackupPath) &&
                File.Exists(firstApply.BackupPath) &&
                appliedConfig.Contains("model_provider = \"relaybench\"", StringComparison.OrdinalIgnoreCase) &&
                appliedConfig.Contains("model = \"gpt-5.4\"", StringComparison.OrdinalIgnoreCase) &&
                appliedConfig.Contains("base_url = \"http://127.0.0.1:17880/v1\"", StringComparison.OrdinalIgnoreCase) &&
                appliedConfig.Contains("wire_api = \"responses\"", StringComparison.OrdinalIgnoreCase) &&
                appliedConfig.Contains("[model_providers.other]", StringComparison.OrdinalIgnoreCase) &&
                secondApply.Succeeded &&
                string.IsNullOrWhiteSpace(secondApply.BackupPath) &&
                backupCountAfterSecondApply == backupCountAfterFirstApply &&
                restore.Succeeded &&
                restoredConfig.Contains("model_provider = \"other\"", StringComparison.OrdinalIgnoreCase) &&
                restoredConfig.Contains("[custom.after_apply]", StringComparison.OrdinalIgnoreCase) &&
                !restoredConfig.Contains("[model_providers.relaybench]", StringComparison.OrdinalIgnoreCase),
                "Codex CLI 接管配置应支持预览、写入、幂等重写、备份和恢复，并且只维护 RelayBench provider。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Codex CLI 接管配置",
                false,
                $"Codex CLI 接管配置自测失败：{ex.Message}"));
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

    private static void CheckClaudeConfigService(List<TransparentProxySelfTestCheck> checks)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-claude-config-{Guid.NewGuid():N}");
        try
        {
            var service = new TransparentProxyClaudeConfigService(rootDirectory);
            var settingsPath = Path.Combine(rootDirectory, ".claude", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            const string originalSettings = """
                {
                  "theme": "dark",
                  "env": {
                    "EXISTING_FLAG": "keep",
                    "ANTHROPIC_BASE_URL": "https://api.anthropic.com"
                  }
                }
                """;
            File.WriteAllText(settingsPath, originalSettings);

            var preview = service.Preview("http://127.0.0.1:17880/v1");
            var firstApply = service.Apply("http://127.0.0.1:17880/v1");
            var appliedSettings = File.ReadAllText(settingsPath);
            var backupCountAfterFirstApply = Directory
                .GetFiles(Path.GetDirectoryName(settingsPath)!, "settings.json.relaybench-app-capture-backup-*")
                .Length;
            var secondApply = service.Apply("http://127.0.0.1:17880");
            var backupCountAfterSecondApply = Directory
                .GetFiles(Path.GetDirectoryName(settingsPath)!, "settings.json.relaybench-app-capture-backup-*")
                .Length;
            var afterApplySettings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            afterApplySettings["postApplySetting"] = "keep";
            File.WriteAllText(settingsPath, afterApplySettings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            var restore = service.RestoreLatestBackup();
            var restoredSettings = File.ReadAllText(settingsPath);

            checks.Add(new(
                "Claude CLI 接管配置",
                preview.Changed &&
                preview.PreviewText.Contains("ANTHROPIC_BASE_URL", StringComparison.OrdinalIgnoreCase) &&
                firstApply.Succeeded &&
                !string.IsNullOrWhiteSpace(firstApply.BackupPath) &&
                File.Exists(firstApply.BackupPath) &&
                appliedSettings.Contains("\"ANTHROPIC_BASE_URL\": \"http://127.0.0.1:17880\"", StringComparison.OrdinalIgnoreCase) &&
                appliedSettings.Contains("\"ANTHROPIC_AUTH_TOKEN\": \"relaybench-local\"", StringComparison.OrdinalIgnoreCase) &&
                appliedSettings.Contains("\"EXISTING_FLAG\": \"keep\"", StringComparison.OrdinalIgnoreCase) &&
                appliedSettings.Contains("\"theme\": \"dark\"", StringComparison.OrdinalIgnoreCase) &&
                secondApply.Succeeded &&
                string.IsNullOrWhiteSpace(secondApply.BackupPath) &&
                backupCountAfterSecondApply == backupCountAfterFirstApply &&
                restore.Succeeded &&
                restoredSettings.Contains("\"ANTHROPIC_BASE_URL\": \"https://api.anthropic.com\"", StringComparison.OrdinalIgnoreCase) &&
                restoredSettings.Contains("\"postApplySetting\": \"keep\"", StringComparison.OrdinalIgnoreCase) &&
                !restoredSettings.Contains("relaybench-local", StringComparison.OrdinalIgnoreCase),
                "Claude CLI 接管配置应支持结构化 JSON 预览、写入、幂等重写、备份和恢复，并保留用户原有设置。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "Claude CLI 接管配置",
                false,
                $"Claude CLI 接管配置自测失败：{ex.Message}"));
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

    private static void CheckVsCodeSettingsService(List<TransparentProxySelfTestCheck> checks)
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"relaybench-vscode-config-{Guid.NewGuid():N}");
        try
        {
            var service = new TransparentProxyVsCodeSettingsService(rootDirectory);
            var settingsPath = Path.Combine(rootDirectory, "Code", "User", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            const string originalSettings = """
                {
                  "editor.fontSize": 14,
                  "terminal.integrated.env.windows": {
                    "EXISTING_FLAG": "keep",
                    "OPENAI_BASE_URL": "https://api.openai.com/v1"
                  }
                }
                """;
            File.WriteAllText(settingsPath, originalSettings);

            var preview = service.Preview("http://127.0.0.1:17880/v1");
            var firstApply = service.Apply("http://127.0.0.1:17880/v1");
            var appliedSettings = File.ReadAllText(settingsPath);
            var backupCountAfterFirstApply = Directory
                .GetFiles(Path.GetDirectoryName(settingsPath)!, "settings.json.relaybench-app-capture-backup-*")
                .Length;
            var secondApply = service.Apply("http://127.0.0.1:17880");
            var backupCountAfterSecondApply = Directory
                .GetFiles(Path.GetDirectoryName(settingsPath)!, "settings.json.relaybench-app-capture-backup-*")
                .Length;
            var afterApplySettings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            afterApplySettings["workbench.colorTheme"] = "RelayBench Keep";
            ((JsonObject)afterApplySettings["terminal.integrated.env.windows"]!)["USER_AFTER_APPLY"] = "keep";
            File.WriteAllText(settingsPath, afterApplySettings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            var restore = service.RestoreLatestBackups();
            var restoredSettings = File.ReadAllText(settingsPath);
            var userSettingsAfterRestore = File.ReadAllText(settingsPath);

            var workspaceRoot = Path.Combine(rootDirectory, "workspace-a");
            var workspaceSettingsPath = Path.Combine(workspaceRoot, ".vscode", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(workspaceSettingsPath)!);
            const string originalWorkspaceSettings = """
                {
                  "files.autoSave": "off"
                }
                """;
            File.WriteAllText(workspaceSettingsPath, originalWorkspaceSettings);
            var workspacePreview = service.Preview(
                "http://127.0.0.1:17880/v1",
                TransparentProxyVsCodeSettingsScope.Workspace,
                workspaceRoot);
            var workspaceApply = service.Apply(
                "http://127.0.0.1:17880/v1",
                TransparentProxyVsCodeSettingsScope.Workspace,
                workspaceRoot);
            var userSettingsAfterWorkspaceApply = File.ReadAllText(settingsPath);
            var workspaceAppliedSettings = File.ReadAllText(workspaceSettingsPath);
            var workspaceRestore = service.RestoreLatestBackups(
                TransparentProxyVsCodeSettingsScope.Workspace,
                workspaceRoot);
            var workspaceRestoredSettings = File.ReadAllText(workspaceSettingsPath);

            checks.Add(new(
                "VS Code 终端接管配置",
                preview.Changed &&
                preview.PreviewText.Contains("terminal.integrated.env.windows", StringComparison.OrdinalIgnoreCase) &&
                firstApply.Succeeded &&
                firstApply.ChangedFiles.Count == 1 &&
                firstApply.BackupFiles.Count == 1 &&
                File.Exists(firstApply.BackupFiles[0]) &&
                appliedSettings.Contains("\"OPENAI_BASE_URL\": \"http://127.0.0.1:17880/v1\"", StringComparison.OrdinalIgnoreCase) &&
                appliedSettings.Contains("\"ANTHROPIC_BASE_URL\": \"http://127.0.0.1:17880\"", StringComparison.OrdinalIgnoreCase) &&
                appliedSettings.Contains("\"OPENAI_API_KEY\": \"relaybench-local\"", StringComparison.OrdinalIgnoreCase) &&
                appliedSettings.Contains("\"ANTHROPIC_AUTH_TOKEN\": \"relaybench-local\"", StringComparison.OrdinalIgnoreCase) &&
                appliedSettings.Contains("\"EXISTING_FLAG\": \"keep\"", StringComparison.OrdinalIgnoreCase) &&
                appliedSettings.Contains("\"editor.fontSize\": 14", StringComparison.OrdinalIgnoreCase) &&
                secondApply.Succeeded &&
                secondApply.ChangedFiles.Count == 0 &&
                backupCountAfterSecondApply == backupCountAfterFirstApply &&
                restore.Succeeded &&
                restoredSettings.Contains("\"OPENAI_BASE_URL\": \"https://api.openai.com/v1\"", StringComparison.OrdinalIgnoreCase) &&
                restoredSettings.Contains("\"workbench.colorTheme\": \"RelayBench Keep\"", StringComparison.OrdinalIgnoreCase) &&
                restoredSettings.Contains("\"USER_AFTER_APPLY\": \"keep\"", StringComparison.OrdinalIgnoreCase) &&
                !restoredSettings.Contains("relaybench-local", StringComparison.OrdinalIgnoreCase) &&
                workspacePreview.SettingsPaths.Count == 1 &&
                string.Equals(workspacePreview.SettingsPaths[0], workspaceSettingsPath, StringComparison.OrdinalIgnoreCase) &&
                workspacePreview.PreviewText.Contains("当前工作区级", StringComparison.OrdinalIgnoreCase) &&
                workspaceApply.Succeeded &&
                workspaceApply.ChangedFiles.Count == 1 &&
                workspaceApply.BackupFiles.Count == 1 &&
                string.Equals(userSettingsAfterRestore, userSettingsAfterWorkspaceApply, StringComparison.Ordinal) &&
                workspaceAppliedSettings.Contains("\"OPENAI_BASE_URL\": \"http://127.0.0.1:17880/v1\"", StringComparison.OrdinalIgnoreCase) &&
                workspaceAppliedSettings.Contains("\"files.autoSave\": \"off\"", StringComparison.OrdinalIgnoreCase) &&
                workspaceRestore.Succeeded &&
                workspaceRestoredSettings.Contains("\"files.autoSave\": \"off\"", StringComparison.OrdinalIgnoreCase) &&
                !workspaceRestoredSettings.Contains("relaybench-local", StringComparison.OrdinalIgnoreCase),
                "VS Code 终端接管配置应只写官方终端环境变量设置，支持用户级/工作区级预览、写入、幂等重写、备份和恢复，并保留用户原有设置。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "VS Code 终端接管配置",
                false,
                $"VS Code 终端接管配置自测失败：{ex.Message}"));
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

    private static void CheckTunForwardProxyAndLegacyRecoveryGuard(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            var savedProxy = new TransparentProxySystemProxySnapshot
            {
                AutoConfigUrl = "https://example.com/original.pac",
                ProxyEnable = 1,
                ProxyServer = "127.0.0.1:8080",
                AppliedPacUrl = "http://127.0.0.1:17880/relaybench/pac"
            };
            var relayBenchCurrentProxy = new TransparentProxySystemProxySnapshot
            {
                AutoConfigUrl = "http://127.0.0.1:17880/relaybench/pac"
            };
            var userChangedCurrentProxy = new TransparentProxySystemProxySnapshot
            {
                AutoConfigUrl = "https://example.com/user-new.pac"
            };
            checks.Add(new(
                "TUN 内部转发器与旧版 PAC 清理",
                TransparentProxyForwardProxyService.IsAllowedTunnelHost("api.openai.com") &&
                TransparentProxyForwardProxyService.IsAllowedTunnelHost("api.anthropic.com") &&
                TransparentProxyForwardProxyService.IsAllowedTunnelHost("sub.api.openai.com") &&
                !TransparentProxyForwardProxyService.IsAllowedTunnelHost("chat.openai.com") &&
                !TransparentProxyForwardProxyService.IsAllowedTunnelHost("chatgpt.com") &&
                !TransparentProxyForwardProxyService.IsAllowedTunnelHost("platform.openai.com") &&
                !TransparentProxyForwardProxyService.IsAllowedTunnelHost("github.com") &&
                !TransparentProxyForwardProxyService.IsAllowedTunnelHost("registry.npmjs.org") &&
                TransparentProxySystemProxyService.IsRelayBenchPacUrl("http://127.0.0.1:17880/relaybench/pac") &&
                !TransparentProxySystemProxyService.IsRelayBenchPacUrl("https://example.com/proxy.pac") &&
                TransparentProxySystemProxyService.ShouldRestoreSnapshot(relayBenchCurrentProxy, savedProxy) &&
                !TransparentProxySystemProxyService.ShouldRestoreSnapshot(userChangedCurrentProxy, savedProxy),
                "TUN 内部 HTTP CONNECT 转发器应只允许 AI API 域名；旧版系统 PAC 恢复只能清理 RelayBench 自己写入的备份，不能覆盖用户后续改动。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "TUN 内部转发器与旧版 PAC 清理",
                false,
                $"TUN 内部转发器自测失败：{ex.Message}"));
        }
    }

    private static void CheckTunGenerationAndNetworkGuard(List<TransparentProxySelfTestCheck> checks)
    {
        var tunRoot = Path.Combine(Path.GetTempPath(), $"relaybench-tun-session-{Guid.NewGuid():N}");
        try
        {
            var options = new TransparentProxyTunConfigOptions(17880, 17881, 17882, 17883, 17884);
            var tunService = new TransparentProxyTunService(tunRoot);
            var config = tunService.BuildMihomoConfig(options);
            var guard = new TransparentProxyNetworkGuardService();
            var validation = guard.ValidateMihomoConfig(config);
            var unsafeValidation = guard.ValidateMihomoConfig(
                """
                rules:
                  - DOMAIN,api.openai.com,RelayBench
                  - PROCESS-NAME,Code.exe,RelayBench
                  - MATCH,RelayBench
                """);
            var diagnostics = guard.Inspect();
            Directory.CreateDirectory(Path.GetDirectoryName(tunService.ResidualSessionPath)!);
            File.WriteAllText(
                tunService.ResidualSessionPath,
                JsonSerializer.Serialize(
                    new TransparentProxyTunSessionSnapshot
                    {
                        ProcessId = 999999,
                        StartedAt = DateTimeOffset.Now.AddMinutes(-15),
                        SidecarPath = Path.Combine(tunRoot, "mihomo.exe"),
                        ConfigPath = Path.Combine(tunRoot, "mihomo-relaybench.yaml"),
                        MixedPort = 17882,
                        ControllerPort = 17883,
                        DnsPort = 17884,
                        ForwardProxyPort = 17881
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            var residual = tunService.InspectResidualSession();
            var residualCleanup = tunService.StopResidualSessionAsync().GetAwaiter().GetResult();

            checks.Add(new(
                "TUN 高级模式与网络守护",
                validation.IsSafe &&
                !unsafeValidation.IsSafe &&
                diagnostics.Diagnostics.Count >= 2 &&
                config.Contains("route-exclude-address", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("127.0.0.0/8", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("github.com,DIRECT", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("chat.openai.com,DIRECT", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("chatgpt.com,DIRECT", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("oaistatic.com,DIRECT", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("oaiusercontent.com,DIRECT", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("npmjs.org,DIRECT", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("marketplace.visualstudio.com,DIRECT", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("api.openai.com,RelayBench", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("api.anthropic.com,RelayBench", StringComparison.OrdinalIgnoreCase) &&
                config.Contains("MATCH,DIRECT", StringComparison.OrdinalIgnoreCase) &&
                !config.Contains("PROCESS-NAME,Code.exe,RelayBench", StringComparison.OrdinalIgnoreCase) &&
                !config.Contains("PROCESS-NAME,node.exe,RelayBench", StringComparison.OrdinalIgnoreCase) &&
                !config.Contains("PROCESS-NAME,codex.exe,RelayBench", StringComparison.OrdinalIgnoreCase) &&
                !config.Contains("PROCESS-NAME,claude.exe,RelayBench", StringComparison.OrdinalIgnoreCase) &&
                residual.HasSession &&
                !residual.IsProcessRunning &&
                residualCleanup.Cleared &&
                !File.Exists(tunService.ResidualSessionPath),
                "TUN 配置必须只接管 AI API 域名，并保留 GitHub、npm、VS Code marketplace、局域网和本机 DIRECT；默认不能按进程全量接管，异常退出残留会话可被发现并安全清理。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "TUN 高级模式与网络守护",
                false,
                $"TUN 高级模式自测失败：{ex.Message}"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tunRoot))
                {
                    Directory.Delete(tunRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void CheckPortInspectorService(List<TransparentProxySelfTestCheck> checks)
    {
        try
        {
            const string sample = """
                  TCP    127.0.0.1:17880        0.0.0.0:0              LISTENING       4321
                  TCP    [::1]:17881            [::]:0                 LISTENING       4322
                """;
            var match = TransparentProxyPortInspectorService.ParseNetstatOutput(17880, sample);
            var missing = TransparentProxyPortInspectorService.ParseNetstatOutput(17882, sample);

            checks.Add(new(
                "统一出口端口占用诊断",
                match.IsListening &&
                match.ProcessId == 4321 &&
                match.LocalAddress == "127.0.0.1:17880" &&
                !missing.IsListening,
                "端口冲突诊断应能解析 netstat 监听行，并在启动失败时提示占用 PID/监听地址。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "统一出口端口占用诊断",
                false,
                $"端口占用诊断自测失败：{ex.Message}"));
        }
    }

    private static async Task CheckUnifiedEndpointWithoutRoutesAsync(
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        await using var proxy = new TransparentProxyService(new TransparentProxyResponseCacheService(
            Path.Combine(Path.GetTempPath(), $"relaybench-empty-route-cache-{Guid.NewGuid():N}.sqlite")));
        var port = GetFreeTcpPort();
        try
        {
            await proxy.StartAsync(new TransparentProxyServerConfig(
                port,
                Array.Empty<TransparentProxyRoute>(),
                RateLimitPerMinute: 60,
                MaxConcurrency: 2,
                EnableFallback: true,
                EnableCache: true,
                CacheTtlSeconds: 60,
                RewriteModel: false,
                IgnoreTlsErrors: true,
                UpstreamTimeoutSeconds: 5));

            using var client = new HttpClient();
            using var healthResponse = await client.GetAsync(
                $"http://127.0.0.1:{port}/relaybench/health",
                cancellationToken);
            var healthBody = await healthResponse.Content.ReadAsStringAsync(cancellationToken);
            using var modelsResponse = await client.GetAsync(
                $"http://127.0.0.1:{port}/v1/models",
                cancellationToken);

            checks.Add(new(
                "统一出口空路由启动",
                healthResponse.IsSuccessStatusCode &&
                healthBody.Contains("\"routes\":0", StringComparison.OrdinalIgnoreCase) &&
                (int)modelsResponse.StatusCode == 503,
                "本地统一出口应允许先启动健康检查入口；未配置上游时业务请求返回 503，而不是阻止监听启动。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "统一出口空路由启动",
                false,
                $"统一出口空路由启动自测失败：{ex.Message}"));
        }
        finally
        {
            await proxy.StopAsync();
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
            var diagnostics = new ProxyDiagnosticsService();
            var protocolProbeService = new ProxyEndpointProtocolProbeService(diagnostics, cache);
            var discovery = new TransparentProxyProtocolDiscoveryService(diagnostics, cache, protocolProbeService);
            var routes = BuildRoutes(responsesUpstream, anthropicUpstream, chatUpstream);
            List<TransparentProxyRoute> routesWithBrokenProbe = [];
            routesWithBrokenProbe.AddRange(routes.Take(2));
            routesWithBrokenProbe.Add(new(
                "selftest-broken",
                "SelfTest Broken",
                "http://127.0.0.1:1",
                "sk-selftest-route",
                "broken-model",
                models: ["broken-model"],
                priority: 4));
            routesWithBrokenProbe.AddRange(routes.Skip(2));
            var result = await discovery.DiscoverAsync(
                routesWithBrokenProbe,
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
            var sharedProbeSettings = new ProxyEndpointSettings(
                responsesUpstream.BaseUrl,
                "sk-selftest-route",
                "rb-selftest-model",
                IgnoreTlsErrors: true,
                TimeoutSeconds: 8);
            var sharedProbe = await protocolProbeService.ResolveAsync(
                sharedProbeSettings,
                new ProxyEndpointProtocolProbeOptions(
                    ForceProbe: true,
                    UseCache: true,
                    SaveResult: true),
                cancellationToken);
            var sharedCachedProbe = await protocolProbeService.ResolveAsync(
                sharedProbeSettings,
                new ProxyEndpointProtocolProbeOptions(
                    ForceProbe: false,
                    UseCache: true,
                    SaveResult: true),
                cancellationToken);

            await using var slowParallelA = await FakeUpstreamServer.StartAsync(
                "parallel-a",
                "parallel-a-model",
                FakeUpstreamMode.Responses,
                cancellationToken,
                responseDelayMs: 450);
            await using var slowParallelB = await FakeUpstreamServer.StartAsync(
                "parallel-b",
                "parallel-b-model",
                FakeUpstreamMode.Responses,
                cancellationToken,
                responseDelayMs: 450);
            var parallelStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var parallelResult = await discovery.DiscoverAsync(
                [
                    new(
                        "selftest-parallel-a",
                        "SelfTest Parallel A",
                        slowParallelA.BaseUrl,
                        "sk-selftest-route",
                        "parallel-a-model",
                        models: ["parallel-a-model"],
                        priority: 1),
                    new(
                        "selftest-parallel-b",
                        "SelfTest Parallel B",
                        slowParallelB.BaseUrl,
                        "sk-selftest-route",
                        "parallel-b-model",
                        models: ["parallel-b-model"],
                        priority: 2)
                ],
                new TransparentProxyProtocolDiscoveryOptions(
                    ForceProbe: true,
                    FetchCatalogModels: true,
                    IgnoreTlsErrors: true,
                    UpstreamTimeoutSeconds: 8,
                    FallbackModel: "rb-selftest-model"),
                cancellationToken: cancellationToken);
            parallelStopwatch.Stop();
            var parallelDiscoveryOk =
                parallelResult.HydratedRoutes.Count == 2 &&
                parallelResult.HydratedRoutes.All(static route =>
                    route.PreferredWireApi == ProxyWireApiProbeService.ResponsesWireApi) &&
                parallelStopwatch.Elapsed < TimeSpan.FromMilliseconds(3600);

            var responses = result.HydratedRoutes.FirstOrDefault(static route => route.Id == "selftest-responses");
            var anthropic = result.HydratedRoutes.FirstOrDefault(static route => route.Id == "selftest-anthropic");
            var broken = result.HydratedRoutes.FirstOrDefault(static route => route.Id == "selftest-broken");
            var chat = result.HydratedRoutes.FirstOrDefault(static route => route.Id == "selftest-chat");
            checks.Add(new(
                "\u534f\u8bae\u53d1\u73b0\u670d\u52a1",
                responses is
                {
                    PreferredWireApi: ProxyWireApiProbeService.ResponsesWireApi,
                    ChatCompletionsSupported: true,
                    AnthropicMessagesSupported: true,
                    ResponsesSupported: true
                } &&
                sharedProbe.FromCache == false &&
                sharedProbe.Result.ChatCompletionsSupported &&
                sharedProbe.Result.AnthropicMessagesSupported &&
                sharedProbe.Result.ResponsesSupported &&
                sharedCachedProbe.FromCache &&
                sharedCachedProbe.CachedInfo?.ProtocolProbeVersion == ProxyWireApiProbeService.CurrentProtocolProbeVersion &&
                anthropic is
                {
                    PreferredWireApi: ProxyWireApiProbeService.AnthropicMessagesWireApi,
                    ChatCompletionsSupported: true,
                    AnthropicMessagesSupported: true,
                    ResponsesSupported: false
                } &&
                broken is
                {
                    ProtocolCheckedAt: not null,
                    ResponsesSupported: false,
                    AnthropicMessagesSupported: false,
                    ChatCompletionsSupported: false
                } &&
                chat is
                {
                    PreferredWireApi: ProxyWireApiProbeService.ChatCompletionsWireApi,
                    ChatCompletionsSupported: true,
                    ResponsesSupported: false,
                    AnthropicMessagesSupported: false
                } &&
                result.ProbedModels >= 4 &&
                cached.CachedModels >= 3 &&
                parallelDiscoveryOk,
                $"Protocol discovery should cache models, isolate failed routes, probe multiple routes in parallel and record all supported wire APIs. parallel={parallelStopwatch.ElapsedMilliseconds}ms."));
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

    private static async Task CheckThroughputBenchmarkAsync(
        FakeUpstreamServer responsesUpstream,
        List<TransparentProxySelfTestCheck> checks,
        CancellationToken cancellationToken)
    {
        try
        {
            var diagnostics = new ProxyDiagnosticsService();
            var settings = new ProxyEndpointSettings(
                responsesUpstream.BaseUrl,
                "sk-selftest-route",
                "rb-selftest-model",
                IgnoreTlsErrors: true,
                TimeoutSeconds: 8);
            var baseline = await diagnostics.RunAsync(
                settings,
                cancellationToken: cancellationToken,
                streamThroughputSampleCount: 1);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var benchmark = await diagnostics.RunThroughputBenchmarkAsync(
                settings,
                requestedSampleCount: 1,
                requestedSegmentCount: 24,
                baselineResult: baseline,
                cancellationToken: cancellationToken);
            stopwatch.Stop();

            checks.Add(new(
                "独立吞吐探针",
                benchmark.CompletedSampleCount == 1 &&
                benchmark.SuccessfulSampleCount == 1 &&
                benchmark.MedianOutputTokensPerSecond is > 0 &&
                benchmark.AverageOutputTokenCount is > 0 &&
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "独立吞吐应复用基础诊断协议、使用短流式吞吐探针，并在首 token 超时前快速给出样本。"));
        }
        catch (Exception ex)
        {
            checks.Add(new(
                "独立吞吐探针",
                false,
                $"独立吞吐探针自检失败：{ex.Message}"));
        }
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
        using var cacheResponse = await client.GetAsync("relaybench/cache", cancellationToken);
        var cacheBody = await cacheResponse.Content.ReadAsStringAsync(cancellationToken);
        using var schedulerResponse = await client.GetAsync("relaybench/scheduler?model=self-visible", cancellationToken);
        var schedulerBody = await schedulerResponse.Content.ReadAsStringAsync(cancellationToken);
        using var protocolsResponse = await client.GetAsync("relaybench/protocols", cancellationToken);
        var protocolsBody = await protocolsResponse.Content.ReadAsStringAsync(cancellationToken);
        using var usageResponse = await client.GetAsync("relaybench/usage", cancellationToken);
        var usageBody = await usageResponse.Content.ReadAsStringAsync(cancellationToken);
        using var ingressResponse = await client.GetAsync("relaybench/ingress", cancellationToken);
        var ingressBody = await ingressResponse.Content.ReadAsStringAsync(cancellationToken);
        using var captureAppsResponse = await client.GetAsync("relaybench/capture/apps", cancellationToken);
        var captureAppsBody = await captureAppsResponse.Content.ReadAsStringAsync(cancellationToken);
        using var captureDiagnosticsResponse = await client.GetAsync("relaybench/capture/diagnostics", cancellationToken);
        var captureDiagnosticsBody = await captureDiagnosticsResponse.Content.ReadAsStringAsync(cancellationToken);
        using var captureRecoveryResponse = await client.GetAsync("relaybench/capture/recovery", cancellationToken);
        var captureRecoveryBody = await captureRecoveryResponse.Content.ReadAsStringAsync(cancellationToken);
        using var logsResponse = await client.GetAsync("relaybench/logs?limit=20", cancellationToken);
        var logsBody = await logsResponse.Content.ReadAsStringAsync(cancellationToken);

        var healthOk = false;
        var metricsOk = false;
        var cacheOk = false;
        var schedulerOk = false;
        var protocolsOk = false;
        var usageOk = false;
        var ingressOk = false;
        var captureAppsOk = false;
        var captureDiagnosticsOk = false;
        var captureRecoveryOk = false;
        var logsOk = false;
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

            using var cache = JsonDocument.Parse(cacheBody);
            cacheOk =
                cache.RootElement.TryGetProperty("response", out var cacheResponseNode) &&
                cacheResponseNode.TryGetProperty("hitRate", out var hitRate) &&
                hitRate.ValueKind == JsonValueKind.Number &&
                cache.RootElement.TryGetProperty("policy", out var policy) &&
                policy.TryGetProperty("bypassReasons", out var bypassReasons) &&
                bypassReasons.ValueKind == JsonValueKind.Array &&
                bypassReasons.GetArrayLength() >= 4;

            using var scheduler = JsonDocument.Parse(schedulerBody);
            schedulerOk =
                scheduler.RootElement.TryGetProperty("ready", out var schedulerReady) &&
                schedulerReady.ValueKind == JsonValueKind.True &&
                scheduler.RootElement.TryGetProperty("candidateCount", out var candidateCount) &&
                candidateCount.GetInt32() >= 1 &&
                scheduler.RootElement.TryGetProperty("routes", out var schedulerRoutes) &&
                schedulerRoutes.ValueKind == JsonValueKind.Array &&
                schedulerRoutes.GetArrayLength() >= 3;

            using var protocols = JsonDocument.Parse(protocolsBody);
            protocolsOk =
                protocols.RootElement.TryGetProperty("fallbackOrder", out var fallbackOrder) &&
                fallbackOrder.ValueKind == JsonValueKind.Array &&
                fallbackOrder.GetArrayLength() == 3 &&
                protocols.RootElement.TryGetProperty("routes", out var protocolRoutes) &&
                protocolRoutes.ValueKind == JsonValueKind.Array &&
                protocolRoutes.GetArrayLength() >= 3;

            using var usage = JsonDocument.Parse(usageBody);
            usageOk =
                usage.RootElement.TryGetProperty("totals", out var totals) &&
                totals.TryGetProperty("outputTokens", out var outputTokens) &&
                outputTokens.GetInt64() >= 0 &&
                usage.RootElement.TryGetProperty("events", out var usageEvents) &&
                usageEvents.ValueKind == JsonValueKind.Array;

            using var ingress = JsonDocument.Parse(ingressBody);
            ingressOk =
                ingress.RootElement.TryGetProperty("ingresses", out var ingresses) &&
                ingresses.ValueKind == JsonValueKind.Array;

            using var captureApps = JsonDocument.Parse(captureAppsBody);
            captureAppsOk =
                captureApps.RootElement.TryGetProperty("apps", out var apps) &&
                apps.ValueKind == JsonValueKind.Array &&
                apps.GetArrayLength() >= 3;

            using var captureDiagnostics = JsonDocument.Parse(captureDiagnosticsBody);
            captureDiagnosticsOk =
                captureDiagnostics.RootElement.TryGetProperty("unifiedEndpoint", out var unifiedEndpoint) &&
                unifiedEndpoint.TryGetProperty("localOnly", out var localOnly) &&
                localOnly.ValueKind == JsonValueKind.True &&
                captureDiagnostics.RootElement.TryGetProperty("safety", out var safety) &&
                safety.TryGetProperty("oneClickRecoveryAvailable", out var oneClickRecoveryAvailable) &&
                oneClickRecoveryAvailable.ValueKind == JsonValueKind.True;

            using var captureRecovery = JsonDocument.Parse(captureRecoveryBody);
            captureRecoveryOk =
                captureRecovery.RootElement.TryGetProperty("executed", out var executed) &&
                executed.ValueKind == JsonValueKind.False &&
                captureRecovery.RootElement.TryGetProperty("items", out var recoveryItems) &&
                recoveryItems.ValueKind == JsonValueKind.Array &&
                recoveryItems.GetArrayLength() >= 3;

            using var logs = JsonDocument.Parse(logsBody);
            logsOk =
                logs.RootElement.TryGetProperty("count", out var logCount) &&
                logCount.GetInt32() > 0 &&
                logs.RootElement.TryGetProperty("logs", out var logRows) &&
                logRows.ValueKind == JsonValueKind.Array &&
                logRows.EnumerateArray().Any(static row =>
                    row.TryGetProperty("modelName", out var modelName) &&
                    !string.IsNullOrWhiteSpace(modelName.GetString()) &&
                    row.TryGetProperty("routeName", out var routeName) &&
                    !string.IsNullOrWhiteSpace(routeName.GetString()));
        }
        catch
        {
            healthOk = false;
            metricsOk = false;
            cacheOk = false;
            schedulerOk = false;
            protocolsOk = false;
            usageOk = false;
            ingressOk = false;
            captureAppsOk = false;
            captureDiagnosticsOk = false;
            captureRecoveryOk = false;
            logsOk = false;
        }

        checks.Add(new(
            "Management endpoints",
            healthResponse.IsSuccessStatusCode &&
            metricsResponse.IsSuccessStatusCode &&
            cacheResponse.IsSuccessStatusCode &&
            schedulerResponse.IsSuccessStatusCode &&
            protocolsResponse.IsSuccessStatusCode &&
            usageResponse.IsSuccessStatusCode &&
            ingressResponse.IsSuccessStatusCode &&
            captureAppsResponse.IsSuccessStatusCode &&
            captureDiagnosticsResponse.IsSuccessStatusCode &&
            captureRecoveryResponse.IsSuccessStatusCode &&
            logsResponse.IsSuccessStatusCode &&
            healthOk &&
            metricsOk &&
            cacheOk &&
            schedulerOk &&
            protocolsOk &&
            usageOk &&
            ingressOk &&
            captureAppsOk &&
            captureDiagnosticsOk &&
            captureRecoveryOk &&
            logsOk,
            "Local /relaybench/* management endpoints should expose health, metrics, cache policy, scheduler previews, protocol state, usage events, ingress/capture diagnostics and redacted recent logs."));
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

    private static int CountJsonProperty(JsonElement element, string propertyName)
    {
        var count = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }

                    count += CountJsonProperty(property.Value, propertyName);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    count += CountJsonProperty(item, propertyName);
                }

                break;
        }

        return count;
    }

    private static bool TryFindAssistantTextCacheControl(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("role", out var role) ||
                role.ValueKind != JsonValueKind.String ||
                !string.Equals(role.GetString(), "assistant", StringComparison.OrdinalIgnoreCase) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    block.TryGetProperty("cache_control", out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void DeleteSqliteDatabaseFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
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
        private readonly int _responseDelayMs;
        private Task? _loopTask;

        private FakeUpstreamServer(string name, string model, FakeUpstreamMode mode, int port, int responseDelayMs)
        {
            _name = name;
            _model = model;
            _mode = mode;
            _responseDelayMs = Math.Max(0, responseDelayMs);
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
        }

        public string BaseUrl { get; }

        public static Task<FakeUpstreamServer> StartAsync(
            string name,
            string model,
            FakeUpstreamMode mode,
            CancellationToken cancellationToken,
            int responseDelayMs = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = new FakeUpstreamServer(name, model, mode, GetFreeTcpPort(), responseDelayMs);
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
                if (_responseDelayMs > 0)
                {
                    await Task.Delay(_responseDelayMs, cancellationToken);
                }

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
