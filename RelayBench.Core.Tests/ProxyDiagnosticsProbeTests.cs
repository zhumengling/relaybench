using RelayBench.Core.Services;
using RelayBench.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.App.Services;
using RelayBench.App.ViewModels;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class ProxyDiagnosticsProbeTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("loose preview does not treat empty json as readable content", () =>
    {
        var preview = BuildLooseSuccessPreview("""{"data":[]}""");

        AssertTrue(preview is null, $"Empty JSON should not become a success preview, got {preview}.");
        });

        yield return new TestCase("loose preview still extracts compatible chat text", () =>
    {
        var preview = BuildLooseSuccessPreview("""{"choices":[{"message":{"content":"proxy-ok"}}]}""");

        AssertEqual(preview ?? string.Empty, "proxy-ok");
        });

        yield return new TestCase("embeddings parser rejects empty vectors", () =>
    {
        var preview = ParseEmbeddingsPreview("""{"data":[{"embedding":[]}]}""");

        AssertTrue(preview is null, $"Empty embedding vectors should not pass, got {preview}.");
        });

        yield return new TestCase("images parser rejects empty base64 payloads", () =>
    {
        var preview = ParseImagesPreview("""{"data":[{"b64_json":""}]}""");

        AssertTrue(preview is null, $"Empty image payloads should not pass, got {preview}.");
        });

        yield return new TestCase("audio speech response rejects json error payloads", () =>
    {
        var accepted = IsLikelyAudioSpeechResponse(
            "application/json",
            Encoding.UTF8.GetBytes("""{"error":{"message":"speech failed"}}"""));

        AssertFalse(accepted, "JSON bodies are not usable audio speech payloads.");
        });

        yield return new TestCase("audio speech response accepts audio bytes", () =>
    {
        var accepted = IsLikelyAudioSpeechResponse("audio/mpeg", [0x49, 0x44, 0x33, 0x04]);

        AssertTrue(accepted, "Audio content types with bytes should pass.");
        });

        yield return new TestCase("image capability payload leaves size to provider default", () =>
    {
        using var document = JsonDocument.Parse(BuildImagesPayload("gpt-image-test"));

        AssertFalse(document.RootElement.TryGetProperty("size", out _), "Image probes should not force a size that many providers reject.");
        });

        yield return new TestCase("error transparency rejects opaque bad request wording", () =>
    {
        var transparent = LooksLikeTransparentBadRequest("""{"error":{"message":"Bad Request"}}""");

        AssertFalse(transparent, "A generic Bad Request message is not enough evidence of transparent validation.");
        });

        yield return new TestCase("error transparency accepts field-specific validation errors", () =>
    {
        var transparent = LooksLikeTransparentBadRequest("""{"error":{"message":"Invalid type for messages: expected an array"}}""");

        AssertTrue(transparent, "Field-specific invalid messages should count as transparent validation.");
        });

        yield return new TestCase("long streaming payload asks for compact segment lines", () =>
    {
        using var document = JsonDocument.Parse(BuildLongStreamingPayload("gpt-test", 120));
        var userContent = document.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Last()
            .GetProperty("content")
            .GetString() ?? string.Empty;

        AssertContains(userContent, "8 to 12 short English words");
        });

        yield return new TestCase("compatibility probe payloads are served by shared payload factory", () =>
    {
        AssertEqual(
            ProxyProbePayloadFactory.BuildSystemPromptPayload("gpt-test"),
            BuildSystemPromptPayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildFunctionCallingProbePayload("gpt-test"),
            BuildFunctionCallingProbePayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildFunctionCallingFollowUpPayload(
                "gpt-test",
                "call_123",
                "emit_probe_result",
                "{\"status\":\"proxy-ok\",\"channel\":\"function-calling\",\"round\":1}"),
            BuildFunctionCallingFollowUpPayload(
                "gpt-test",
                "call_123",
                "emit_probe_result",
                "{\"status\":\"proxy-ok\",\"channel\":\"function-calling\",\"round\":1}"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildErrorTransparencyPayload("gpt-test", "anthropic"),
            BuildErrorTransparencyPayload("gpt-test", "anthropic"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildErrorTransparencyPayload("gpt-test", "responses"),
            BuildErrorTransparencyPayload("gpt-test", "responses"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildErrorTransparencyPayload("gpt-test", "chat"),
            BuildErrorTransparencyPayload("gpt-test", "chat"));
        });

        yield return new TestCase("integrity and cache probe payloads are served by shared payload factory", () =>
    {
        AssertEqual(
            ProxyProbePayloadFactory.BuildStreamingIntegrityPayload("gpt-test", stream: true),
            BuildStreamingIntegrityPayload("gpt-test", stream: true));
        AssertEqual(
            ProxyProbePayloadFactory.BuildOfficialReferenceIntegrityPayload("gpt-test"),
            BuildOfficialReferenceIntegrityPayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildMultiModalPayload("gpt-test"),
            BuildMultiModalPayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildCacheProbePayload("gpt-test"),
            BuildCacheProbePayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildCacheIsolationPayload("gpt-test", "cache-isolation-owner=a; secret=b"),
            BuildCacheIsolationPayload("gpt-test", "cache-isolation-owner=a; secret=b"));
        });

        yield return new TestCase("non chat and pressure payloads are served by shared payload factory", () =>
    {
        AssertEqual(
            ProxyProbePayloadFactory.BuildEmbeddingsPayload("embed-test"),
            BuildEmbeddingsPayload("embed-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildImagesPayload("image-test"),
            BuildImagesPayload("image-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildModerationPayload("moderation-test"),
            BuildModerationPayload("moderation-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildAudioSpeechPayload("tts-test"),
            BuildAudioSpeechPayload("tts-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildMultiModelSpeedPayload("gpt-test"),
            BuildMultiModelSpeedPayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildConcurrencyPressurePayload("gpt-test", stream: true, "attempt-01"),
            BuildConcurrencyPressurePayload("gpt-test", stream: true, "attempt-01"));
        });

        yield return new TestCase("health score uses proxy tolerant latency and failure penalty", () =>
    {
        var scoreWithHighLatency = CalculateHealthScore(
            fullSuccessRate: 100d,
            streamSuccessRate: 100d,
            averageChatLatency: TimeSpan.FromMilliseconds(6000),
            averageTtft: TimeSpan.FromMilliseconds(4000),
            maxConsecutiveFailures: 0);
        var scoreWithFourFailures = CalculateHealthScore(
            fullSuccessRate: 100d,
            streamSuccessRate: 100d,
            averageChatLatency: TimeSpan.FromMilliseconds(800),
            averageTtft: TimeSpan.FromMilliseconds(500),
            maxConsecutiveFailures: 4);

        AssertTrue(scoreWithHighLatency >= 77, $"Domestic proxy latency should not collapse the score, got {scoreWithHighLatency}.");
        AssertTrue(scoreWithFourFailures == 76, $"Expected score 76, got {scoreWithFourFailures}.");
        });

        yield return new TestCase("cache hit detection accepts moderate TTFT speedup but rejects weak speedup", () =>
    {
        AssertTrue(IsLikelyCacheHit(650, 320, true), "650ms to 320ms should be treated as a likely cache hit.");
        AssertFalse(IsLikelyCacheHit(900, 500, true), "A weak 900ms to 500ms speedup should not be treated as a cache hit.");
        });

        yield return new TestCase("long streaming practical integrity tolerates one missing ordered segment", () =>
    {
        AssertTrue(HasPracticalLongStreamSequenceIntegrity([1, 2, 3, 4, 6, 7, 8, 9, 10], 10), "One missing ordered segment should pass practical integrity.");
        AssertFalse(HasPracticalLongStreamSequenceIntegrity([1, 4, 2, 3, 5, 6, 7, 8, 9], 10), "Out-of-order segments should fail practical integrity.");
        });

        yield return new TestCase("concurrency pressure reports practical limit with one rate limit", () =>
    {
        var stages = new[]
        {
            new ProxyConcurrencyPressureStageResult(4, 10, 9, 1, 0, 0, null, null, null, null, null, "x4"),
            new ProxyConcurrencyPressureStageResult(8, 10, 8, 2, 0, 0, null, null, null, null, null, "x8")
        };

        var practicalLimit = ResolvePracticalConcurrencyLimit(stages);
        AssertTrue(practicalLimit == 4, $"Expected practical limit 4, got {practicalLimit?.ToString() ?? "<null>"}.");
        });

        yield return new TestCase("concurrency pressure keeps stable and practical limits separate", () =>
        {
            var stages = new[]
            {
                new ProxyConcurrencyPressureStageResult(1, 10, 10, 0, 0, 0, 450, 700, 220, 360, 18, "x1"),
                new ProxyConcurrencyPressureStageResult(2, 10, 9, 1, 0, 0, 470, 740, 240, 410, 17, "x2"),
                new ProxyConcurrencyPressureStageResult(4, 10, 8, 2, 0, 0, 520, 980, 300, 700, 14, "x4")
            };

            AssertTrue(ResolveStableConcurrencyLimit(stages) == 1, "Stable limit should require no rate limits.");
            AssertTrue(ResolvePracticalConcurrencyLimit(stages) == 2, "Practical limit should allow one rate-limited request at 90% success.");
        }, group: "proxy");

        yield return new TestCase("concurrency pressure detects high risk from timeout or TTFT inflation", () =>
        {
            var ttftInflatedStages = new[]
            {
                new ProxyConcurrencyPressureStageResult(1, 10, 10, 0, 0, 0, 450, 700, 220, 500, 18, "x1"),
                new ProxyConcurrencyPressureStageResult(4, 10, 10, 0, 0, 0, 650, 1100, 320, 1100, 12, "x4")
            };
            var timeoutStages = new[]
            {
                new ProxyConcurrencyPressureStageResult(1, 10, 10, 0, 0, 0, 450, 700, 220, 500, 18, "x1"),
                new ProxyConcurrencyPressureStageResult(2, 10, 9, 0, 0, 1, 650, 1100, 320, 750, 12, "x2")
            };

            AssertTrue(ResolveHighRiskConcurrency(ttftInflatedStages) == 4, "P95 TTFT doubling should mark the first high-risk stage.");
            AssertTrue(ResolveHighRiskConcurrency(timeoutStages) == 2, "Any timeout should mark the first high-risk stage.");
        }, group: "proxy");

        yield return new TestCase("concurrency pressure normalizes custom stages and summary labels empty values", () =>
        {
            var normalized = NormalizeConcurrencyPressureStagesForTest([8, 2, 2, 0, 90, -1]);
            AssertEqual(string.Join(",", normalized), "2,8,64");

            var summary = BuildConcurrencyPressureSummaryForTest(
                [],
                stableConcurrencyLimit: null,
                practicalConcurrencyLimit: null,
                rateLimitStartConcurrency: null,
                highRiskConcurrency: null);
            AssertContains(summary, "未采集");
        }, group: "proxy");

        yield return new TestCase("capability matrix accepts successful non chat API shapes", async cancellationToken =>
        {
            await using var server = await ScriptedHttpServer.StartAsync(request =>
            {
                if (request.Path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ScriptedHttpResponse.Json(200, """{"data":[{"embedding":[0.1,0.2,0.3]}],"usage":{"total_tokens":4}}"""));
                }

                if (request.Path.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ScriptedHttpResponse.Json(200, """{"data":[{"url":"https://example.com/probe.png"}]}"""));
                }

                if (request.Path.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new ScriptedHttpResponse(200, "audio/mpeg", "ID3 relaybench audio", "OK"));
                }

                if (request.Path.EndsWith("/moderations", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ScriptedHttpResponse.Json(200, """{"results":[{"flagged":false}]}"""));
                }

                return Task.FromResult(ScriptedHttpResponse.Json(404, """{"error":{"message":"missing route"}}"""));
            });

            var service = new ProxyDiagnosticsService();
            var result = await service.ProbeCapabilityMatrixAsync(
                new ProxyEndpointSettings(
                    server.BaseUrl,
                    "sk-test",
                    "gpt-test",
                    false,
                    5,
                    EmbeddingsModel: "embed-test",
                    ImagesModel: "image-test",
                    AudioSpeechModel: "tts-test",
                    ModerationModel: "mod-test"),
                cancellationToken: cancellationToken);

            AssertTrue(result.Scenarios.Count == 4, $"Expected 4 scenarios, got {result.Scenarios.Count}.");
            AssertTrue(result.Scenarios.All(static scenario => scenario.Success), result.Summary);
            AssertContains(result.Summary, "通过 4/4");
        }, group: "proxy", timeout: TimeSpan.FromSeconds(10));

        yield return new TestCase("capability matrix rejects malformed non chat API shapes", async cancellationToken =>
        {
            await using var server = await ScriptedHttpServer.StartAsync(request =>
            {
                if (request.Path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ScriptedHttpResponse.Json(200, """{"data":[{"embedding":[]}]}"""));
                }

                if (request.Path.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ScriptedHttpResponse.Json(200, """{"data":[{"b64_json":""}]}"""));
                }

                if (request.Path.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ScriptedHttpResponse.Json(200, """{"error":{"message":"speech provider rejected voice"}}"""));
                }

                if (request.Path.EndsWith("/moderations", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ScriptedHttpResponse.Json(200, """{"results":[]}"""));
                }

                return Task.FromResult(ScriptedHttpResponse.Json(404, """{"error":{"message":"missing route"}}"""));
            });

            var service = new ProxyDiagnosticsService();
            var result = await service.ProbeCapabilityMatrixAsync(
                new ProxyEndpointSettings(
                    server.BaseUrl,
                    "sk-test",
                    "gpt-test",
                    false,
                    5,
                    EmbeddingsModel: "embed-test",
                    ImagesModel: "image-test",
                    AudioSpeechModel: "tts-test",
                    ModerationModel: "mod-test"),
                cancellationToken: cancellationToken);

            AssertTrue(result.Scenarios.Count == 4, $"Expected 4 scenarios, got {result.Scenarios.Count}.");
            AssertTrue(result.Scenarios.All(static scenario => !scenario.Success), "Malformed payloads should not be treated as supported.");
            AssertTrue(
                result.Scenarios.All(static scenario => scenario.FailureKind == ProxyFailureKind.ProtocolMismatch),
                "Malformed 200 responses should be classified as protocol mismatch.");
            AssertContains(result.Summary, "通过 0/4");
        }, group: "proxy", timeout: TimeSpan.FromSeconds(10));

        yield return new TestCase("capability matrix classifies non chat HTTP errors by status", async cancellationToken =>
        {
            await using var server = await ScriptedHttpServer.StartAsync(request =>
            {
                if (request.Path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ScriptedHttpResponse.Json(429, """{"error":{"message":"rate limit exceeded"}}"""));
                }

                if (request.Path.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ScriptedHttpResponse.Json(503, """{"error":{"message":"upstream unavailable"}}"""));
                }

                return Task.FromResult(ScriptedHttpResponse.Json(404, """{"error":{"message":"missing route"}}"""));
            });

            var service = new ProxyDiagnosticsService();
            var result = await service.ProbeCapabilityMatrixAsync(
                new ProxyEndpointSettings(
                    server.BaseUrl,
                    "sk-test",
                    "gpt-test",
                    false,
                    5,
                    EmbeddingsModel: "embed-test",
                    ImagesModel: "image-test"),
                cancellationToken: cancellationToken);

            var embeddings = result.Scenarios.First(static scenario => scenario.Scenario == ProxyProbeScenarioKind.Embeddings);
            var images = result.Scenarios.First(static scenario => scenario.Scenario == ProxyProbeScenarioKind.Images);
            AssertTrue(embeddings.FailureKind == ProxyFailureKind.RateLimited, $"Expected RateLimited, got {embeddings.FailureKind}.");
            AssertTrue(images.FailureKind == ProxyFailureKind.Http5xx, $"Expected Http5xx, got {images.FailureKind}.");
        }, group: "proxy", timeout: TimeSpan.FromSeconds(10));

        yield return new TestCase("HTTP failure classification covers auth rate limit unsupported and server errors", () =>
        {
            AssertTrue(
                ClassifyResponseFailureForTest(ProxyProbeScenarioKind.ChatCompletions, 401, """{"error":{"message":"bad key"}}""") == ProxyFailureKind.AuthRejected,
                "401 should be auth rejected.");
            AssertTrue(
                ClassifyResponseFailureForTest(ProxyProbeScenarioKind.ChatCompletions, 429, """{"error":{"message":"too many requests"}}""") == ProxyFailureKind.RateLimited,
                "429 should be rate limited.");
            AssertTrue(
                ClassifyResponseFailureForTest(ProxyProbeScenarioKind.Responses, 404, """{"error":{"message":"not found"}}""") == ProxyFailureKind.UnsupportedEndpoint,
                "404 on optional protocol endpoints should be unsupported endpoint.");
            AssertTrue(
                ClassifyResponseFailureForTest(ProxyProbeScenarioKind.FunctionCalling, 400, """{"error":{"message":"unknown parameter: tool_choice"}}""") == ProxyFailureKind.UnsupportedEndpoint,
                "Feature-specific 400 errors should be unsupported endpoint.");
            AssertTrue(
                ClassifyResponseFailureForTest(ProxyProbeScenarioKind.ChatCompletions, 503, """{"error":{"message":"upstream unavailable"}}""") == ProxyFailureKind.Http5xx,
                "5xx should be HTTP 5xx.");
            AssertTrue(
                ClassifyResponseFailureForTest(ProxyProbeScenarioKind.ChatCompletions, 400, """{"error":{"message":"bad request"}}""") == ProxyFailureKind.Http4xx,
                "Generic 400 should remain HTTP 4xx.");
        }, group: "proxy");

        yield return new TestCase("exception classification covers timeout DNS TCP and TLS failures", () =>
        {
            AssertTrue(
                ClassifyExceptionForTest(new TaskCanceledException("timed out")) == ProxyFailureKind.Timeout,
                "TaskCanceledException should be timeout.");
            AssertTrue(
                ClassifyExceptionForTest(new HttpRequestException("host not found", new SocketException((int)SocketError.HostNotFound))) == ProxyFailureKind.DnsFailure,
                "HostNotFound socket errors should be DNS failures.");
            AssertTrue(
                ClassifyExceptionForTest(new HttpRequestException("connection refused", new SocketException((int)SocketError.ConnectionRefused))) == ProxyFailureKind.TcpConnectFailure,
                "ConnectionRefused socket errors should be TCP failures.");
            AssertTrue(
                ClassifyExceptionForTest(new HttpRequestException("TLS failed", new System.Security.Authentication.AuthenticationException("bad certificate"))) == ProxyFailureKind.TlsHandshakeFailure,
                "AuthenticationException should be TLS handshake failure.");
        }, group: "proxy");
    }
}
