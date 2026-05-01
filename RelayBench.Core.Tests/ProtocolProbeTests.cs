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

internal static class ProtocolProbeTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("protocol probe accepts real messages and responses success without model name gating", () =>
    {
        AssertEqual(ResolvePreferredWireApiForProtocolProbe(chatSupported: false, responsesSupported: false, anthropicSupported: true) ?? string.Empty, "anthropic");
        AssertEqual(ResolvePreferredWireApiForProtocolProbe(chatSupported: false, responsesSupported: true, anthropicSupported: false) ?? string.Empty, "responses");
        AssertEqual(ResolvePreferredWireApiForProtocolProbe(chatSupported: true, responsesSupported: false, anthropicSupported: false) ?? string.Empty, "chat");
        });

        yield return new TestCase("protocol probe falls back to chat only after messages and responses fail", () =>
    {
        AssertFalse(ShouldProbeChatCompletionsForProtocolProbe(anthropicSupported: true, responsesSupported: false), "Messages success should skip chat fallback.");
        AssertFalse(ShouldProbeChatCompletionsForProtocolProbe(anthropicSupported: false, responsesSupported: true), "Responses success should skip chat fallback.");
        AssertTrue(ShouldProbeChatCompletionsForProtocolProbe(anthropicSupported: false, responsesSupported: false), "Chat should be probed only when messages and responses both fail.");
        });

        yield return new TestCase("wire api probe service centralizes preferred protocol decisions", () =>
    {
        AssertEqual(
            ProxyWireApiProbeService.ResolvePreferredWireApi(
                chatSupported: false,
                responsesSupported: true,
                anthropicSupported: true) ?? string.Empty,
            "responses");
        AssertFalse(
            ProxyWireApiProbeService.ShouldProbeChatCompletions(
                anthropicSupported: true,
                responsesSupported: false),
            "Chat probing must be skipped when Anthropic Messages already works.");
        AssertTrue(
            ProxyWireApiProbeService.ShouldProbeChatCompletions(
                anthropicSupported: false,
                responsesSupported: false),
            "Chat probing is only the final fallback.");
        });

        yield return new TestCase("wire api probe service normalizes protocol aliases in one place", () =>
    {
        AssertEqual(
            ProxyWireApiProbeService.NormalizeWireApi("openai-responses") ?? string.Empty,
            "responses");
        AssertEqual(
            ProxyWireApiProbeService.NormalizeWireApi("anthropic/messages") ?? string.Empty,
            "anthropic");
        AssertEqual(
            ProxyWireApiProbeService.NormalizeWireApi("chat/completions") ?? string.Empty,
            "chat");
        AssertTrue(
            ProxyWireApiProbeService.NormalizeWireApi("unknown-wire-api") is null,
            "Unknown wire_api values should not silently become a supported protocol.");
        });

        yield return new TestCase("endpoint model cache does not mark anthropic transport as chat support", () =>
    {
        RunEndpointModelCacheWireApiSupportAsync().GetAwaiter().GetResult();
        });

        yield return new TestCase("anthropic messages stream payload and parser support message stop", () =>
    {
        var payload = BuildAnthropicMessagesPayload("mimo-v2.5-pro", stream: true);
        using var document = JsonDocument.Parse(payload);

        AssertTrue(document.RootElement.GetProperty("stream").GetBoolean(), "Anthropic stream payload must set stream=true.");
        AssertEqual(
            TryParseAnthropicStreamContent("""{"type":"content_block_delta","delta":{"type":"text_delta","text":"proxy-ok"}}""") ?? string.Empty,
            "proxy-ok");
        AssertTrue(IsAnthropicStreamDone("""{"type":"message_stop"}"""), "message_stop must be treated as the Anthropic stream terminator.");
        });

        yield return new TestCase("base protocol probe payloads are served by shared payload factory", () =>
    {
        AssertEqual(
            ProxyProbePayloadFactory.BuildChatPayload("gpt-test", stream: false),
            BuildChatPayload("gpt-test", stream: false));
        AssertEqual(
            ProxyProbePayloadFactory.BuildChatPayload("gpt-test", stream: true),
            BuildChatPayload("gpt-test", stream: true));
        AssertEqual(
            ProxyProbePayloadFactory.BuildResponsesPayload("gpt-test"),
            BuildResponsesPayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildStructuredOutputPayload("gpt-test"),
            BuildStructuredOutputPayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildAnthropicMessagesPayload("mimo-v2.5-pro", stream: true),
            BuildAnthropicMessagesPayload("mimo-v2.5-pro", stream: true));
        AssertEqual(
            ProxyProbePayloadFactory.BuildLongStreamingPayload("gpt-test", 120),
            BuildLongStreamingPayload("gpt-test", 120));
        });

        yield return new TestCase("anthropic conversation payload disables thinking and raises small token budgets", () =>
    {
        var payload = BuildConversationWirePayloadForTest(
            "anthropic",
            """
            {
              "model": "mimo-v2.5-pro",
              "max_tokens": 128,
              "messages": [
                { "role": "system", "content": "Reply briefly." },
                { "role": "user", "content": "Say proxy-ok only." }
              ]
            }
            """);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        AssertEqual(root.GetProperty("max_tokens").GetInt32().ToString(), "512");
        AssertEqual(root.GetProperty("thinking").GetProperty("type").GetString() ?? string.Empty, "disabled");
        AssertEqual(root.GetProperty("system").GetString() ?? string.Empty, "Reply briefly.");
        });

        yield return new TestCase("anthropic chat window payload disables thinking and raises small token budgets", () =>
    {
        var options = new ChatRequestOptions(
            "https://relay.example.com/anthropic",
            "sk-test",
            "mimo-v2.5-pro",
            "Reply briefly.",
            0,
            128,
            false,
            10,
            ChatReasoningEffort.Auto,
            false);
        var messages = new[]
        {
            new ChatMessage(
                Guid.NewGuid().ToString("N"),
                "user",
                "Say proxy-ok only.",
                DateTimeOffset.Now,
                Array.Empty<ChatAttachment>(),
                null,
                null)
        };
        var payload = BuildAnthropicChatPayloadForTest(options, messages);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        AssertEqual(root.GetProperty("max_tokens").GetInt32().ToString(), "512");
        AssertEqual(root.GetProperty("thinking").GetProperty("type").GetString() ?? string.Empty, "disabled");
        AssertEqual(root.GetProperty("system").GetString() ?? string.Empty, "Reply briefly.");
        });

        yield return new TestCase("chat conversation falls back from openai chat 404 to anthropic messages", () =>
    {
        RunAnthropicChatConversationFallbackAsync().GetAwaiter().GetResult();
        });

        yield return new TestCase("diagnostics advanced and batch probes reuse anthropic messages transport", () =>
    {
        RunAnthropicDiagnosticsAdvancedAndBatchProbesAsync().GetAwaiter().GetResult();
        });

        yield return new TestCase("anthropic messages success is enough for anthropic endpoint verdict", () =>
    {
        var anthropicKind = Enum.Parse<ProxyProbeScenarioKind>("AnthropicMessages");
        var verdict = BuildVerdictForScenarios(
        [
            CreateFailedScenario(ProxyProbeScenarioKind.Models, "模型列表") with { StatusCode = 404 },
            CreateScenario(anthropicKind, "Anthropic Messages"),
            CreateFailedScenario(ProxyProbeScenarioKind.ChatCompletions, "普通对话") with { StatusCode = 404 },
            CreateFailedScenario(ProxyProbeScenarioKind.Responses, "Responses") with { StatusCode = 404 }
        ]);

        AssertContains(verdict, "Anthropic");
        });

        yield return new TestCase("anthropic unknown model response is classified as model not found", () =>
    {
        var anthropicKind = Enum.Parse<ProxyProbeScenarioKind>("AnthropicMessages");
        var kind = ClassifyResponseFailureForTest(
            anthropicKind,
            400,
            """{"error":{"message":"Not supported model unknown-model"}}""");

        AssertTrue(kind == ProxyFailureKind.ModelNotFound, $"Expected ModelNotFound, got {kind}.");
        });
    }

    private static async Task RunEndpointModelCacheWireApiSupportAsync()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"relaybench-cache-{Guid.NewGuid():N}.sqlite");
        try
        {
            var service = new ProxyEndpointModelCacheService(databasePath);
            var settings = new ProxyEndpointSettings(
                "https://relay.example.com/anthropic",
                "sk-test",
                "mimo-v2.5-pro",
                false,
                20);
            var anthropicChatTrace = CreateProbeTrace(success: true) with
            {
                Path = "v1/messages",
                WireApi = "Anthropic Messages"
            };
            var result = CreateProxyDiagnosticsResult(
            [
                CreateScenario(ProxyProbeScenarioKind.AnthropicMessages, "Anthropic Messages"),
                CreateScenario(ProxyProbeScenarioKind.ChatCompletions, "普通对话") with
                {
                    Trace = anthropicChatTrace
                },
                CreateFailedScenario(ProxyProbeScenarioKind.Responses, "Responses")
            ]) with
            {
                BaseUrl = settings.BaseUrl,
                RequestedModel = settings.Model,
                EffectiveModel = settings.Model,
                ChatRequestSucceeded = true
            };

            await service.SaveDiagnosticsAsync(settings, result);
            var cached = await service.TryResolveAsync(settings.BaseUrl, settings.ApiKey, settings.Model);

            AssertTrue(cached is not null, "Diagnostics result should be cached.");
            AssertEqual(cached!.PreferredWireApi ?? string.Empty, "anthropic");
            AssertTrue(cached.AnthropicMessagesSupported == true, "Anthropic support should be cached.");
            AssertTrue(cached.ChatCompletionsSupported != true, "Anthropic transport must not be advertised as OpenAI Chat support.");
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }
}
