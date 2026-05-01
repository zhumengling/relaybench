using RelayBench.Core.Models;
using RelayBench.Core.Services;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class ChatConversationServiceTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase(
            "chat conversation streams openai chat completions",
            RunOpenAiChatStreamingConversationAsync,
            group: "protocol",
            timeout: TimeSpan.FromSeconds(10));

        yield return new TestCase(
            "chat conversation prefers responses and accepts response completed terminal event",
            RunResponsesStreamingConversationAsync,
            group: "protocol",
            timeout: TimeSpan.FromSeconds(10));

        yield return new TestCase(
            "chat conversation falls back from responses failures to openai chat",
            RunResponsesFallbackToChatConversationAsync,
            group: "protocol",
            timeout: TimeSpan.FromSeconds(10));

        yield return new TestCase(
            "chat conversation reports partial sse disconnect as failure",
            RunPartialSseDisconnectConversationAsync,
            group: "protocol",
            timeout: TimeSpan.FromSeconds(10));
    }

    private static async Task RunOpenAiChatStreamingConversationAsync()
    {
        var calls = new List<string>();
        await using var server = await ScriptedHttpServer.StartAsync(async request =>
        {
            lock (calls)
            {
                calls.Add(request.Path);
            }

            await Task.CompletedTask;
            return request.Path.Contains("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? ScriptedHttpResponse.Sse(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"chat-ok\"}}]}\n\n" +
                    "data: [DONE]\n\n")
                : ScriptedHttpResponse.Json(404, "{\"error\":{\"message\":\"unexpected path\"}}");
        });

        var updates = await CollectConversationUpdatesAsync(BuildOptions(server.BaseUrl, "gpt-test"));

        AssertEqual(JoinDeltas(updates), "chat-ok");
        AssertEqual(LastCompletedWireApi(updates), "chat");
        AssertTrue(calls.Count == 1, $"Expected one request, got {calls.Count}.");
        AssertContains(calls[0], "/v1/chat/completions");
    }

    private static async Task RunResponsesStreamingConversationAsync()
    {
        var calls = new List<string>();
        await using var server = await ScriptedHttpServer.StartAsync(async request =>
        {
            lock (calls)
            {
                calls.Add(request.Path);
            }

            await Task.CompletedTask;
            return request.Path.Contains("/v1/responses", StringComparison.OrdinalIgnoreCase)
                ? ScriptedHttpResponse.Sse(
                    "data: {\"type\":\"response.output_text.delta\",\"delta\":\"responses-ok\"}\n\n" +
                    "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_test\"}}\n\n")
                : ScriptedHttpResponse.Json(500, "{\"error\":{\"message\":\"responses should be first\"}}");
        });

        var options = BuildOptions(server.BaseUrl, "gpt-test") with
        {
            PreferResponsesApi = true,
            ReasoningEffort = ChatReasoningEffort.High
        };
        var updates = await CollectConversationUpdatesAsync(options);

        AssertEqual(JoinDeltas(updates), "responses-ok");
        AssertEqual(LastCompletedWireApi(updates), "responses");
        AssertTrue(calls.Count == 1, $"Expected responses to be the only request, got {calls.Count}.");
        AssertContains(calls[0], "/v1/responses");
    }

    private static async Task RunResponsesFallbackToChatConversationAsync()
    {
        var calls = new List<string>();
        await using var server = await ScriptedHttpServer.StartAsync(async request =>
        {
            lock (calls)
            {
                calls.Add(request.Path);
            }

            await Task.CompletedTask;
            if (request.Path.Contains("/v1/responses", StringComparison.OrdinalIgnoreCase))
            {
                return ScriptedHttpResponse.Json(400, "{\"error\":{\"message\":\"responses unsupported\"}}");
            }

            if (request.Path.Contains("/v1/messages", StringComparison.OrdinalIgnoreCase))
            {
                return ScriptedHttpResponse.Json(404, "{\"error\":{\"message\":\"messages unsupported\"}}");
            }

            return request.Path.Contains("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? ScriptedHttpResponse.Sse(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"fallback-ok\"}}]}\n\n" +
                    "data: [DONE]\n\n")
                : ScriptedHttpResponse.Json(404, "{\"error\":{\"message\":\"unknown path\"}}");
        });

        var options = BuildOptions(server.BaseUrl, "gpt-test") with
        {
            PreferResponsesApi = true,
            ReasoningEffort = ChatReasoningEffort.Medium
        };
        var updates = await CollectConversationUpdatesAsync(options);

        AssertEqual(JoinDeltas(updates), "fallback-ok");
        AssertEqual(LastCompletedWireApi(updates), "chat");
        AssertTrue(calls.Count == 3, $"Expected responses, messages, then chat fallback; got {calls.Count} calls.");
        AssertContains(calls[0], "/v1/responses");
        AssertContains(calls[1], "/v1/messages");
        AssertContains(calls[2], "/v1/chat/completions");
    }

    private static async Task RunPartialSseDisconnectConversationAsync()
    {
        await using var server = await ScriptedHttpServer.StartAsync(async request =>
        {
            await Task.CompletedTask;
            return request.Path.Contains("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? ScriptedHttpResponse.Sse(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")
                : ScriptedHttpResponse.Json(404, "{\"error\":{\"message\":\"unexpected path\"}}");
        });

        var updates = await CollectConversationUpdatesAsync(
            BuildOptions(server.BaseUrl, "gpt-test") with { PreferredWireApi = "chat" });

        AssertEqual(JoinDeltas(updates), "partial");
        AssertTrue(
            updates.Any(static update => update.Kind == ChatStreamUpdateKind.Failed),
            "A stream that closes before a terminal event should surface a failure.");
        AssertFalse(
            updates.Any(static update => update.Kind == ChatStreamUpdateKind.Completed),
            "A partial stream disconnect must not be reported as completed.");
        AssertContains(
            updates.Last(static update => update.Kind == ChatStreamUpdateKind.Failed).Error,
            "stream");
    }

    private static async Task<IReadOnlyList<ChatStreamUpdate>> CollectConversationUpdatesAsync(ChatRequestOptions options)
    {
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var service = new ChatConversationService();
        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in service.SendStreamingAsync(
            options,
            [BuildUserMessage("Reply with the expected token.")],
            Array.Empty<ChatAttachment>(),
            cancellationSource.Token))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static ChatRequestOptions BuildOptions(string baseUrl, string model)
        => new(
            baseUrl,
            "sk-test",
            model,
            string.Empty,
            0,
            128,
            false,
            5,
            ChatReasoningEffort.Auto,
            false);

    private static ChatMessage BuildUserMessage(string content)
        => new(
            Guid.NewGuid().ToString("N"),
            "user",
            content,
            DateTimeOffset.UnixEpoch,
            Array.Empty<ChatAttachment>(),
            null,
            null);

    private static string JoinDeltas(IEnumerable<ChatStreamUpdate> updates)
        => string.Concat(updates
            .Where(static update => update.Kind == ChatStreamUpdateKind.Delta)
            .Select(static update => update.Delta));

    private static string LastCompletedWireApi(IEnumerable<ChatStreamUpdate> updates)
        => updates.Last(static update => update.Kind == ChatStreamUpdateKind.Completed).Metrics?.WireApi ?? string.Empty;
}
