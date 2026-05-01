using RelayBench.Core.Services;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class ChatSseParserTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("chat sse parser reads non empty data lines", () =>
    {
        AssertTrue(ChatSseParser.TryReadDataLine("data: {\"ok\":true}", out var data), "Expected data line to be parsed.");
        AssertEqual(data, "{\"ok\":true}");
        AssertFalse(ChatSseParser.TryReadDataLine("event: message", out _), "Non-data lines should be ignored.");
        AssertFalse(ChatSseParser.TryReadDataLine("data:   ", out _), "Empty data payload should be ignored.");
        }, group: "chat");

        yield return new TestCase("chat sse parser detects openai and anthropic terminal events", () =>
    {
        AssertTrue(ChatSseParser.IsDone("[DONE]"), "[DONE] should terminate OpenAI-compatible streams.");
        AssertTrue(ChatSseParser.IsDone("""{"type":"message_stop"}"""), "message_stop should terminate Anthropic streams.");
        AssertTrue(ChatSseParser.IsDone("""{"type":"response.completed"}"""), "response.completed should terminate Responses streams.");
        AssertFalse(ChatSseParser.IsDone("""{"type":"content_block_delta"}"""), "Content deltas are not terminal.");
        AssertFalse(ChatSseParser.IsDone("{bad json"), "Malformed events are not terminal.");
        }, group: "chat");

        yield return new TestCase("chat sse parser extracts deltas across chat responses and anthropic formats", () =>
    {
        AssertEqual(
            ChatSseParser.TryExtractDelta("""{"choices":[{"delta":{"content":"chat"}}]}""") ?? string.Empty,
            "chat");
        AssertEqual(
            ChatSseParser.TryExtractDelta("""{"type":"response.output_text.delta","delta":"responses"}""") ?? string.Empty,
            "responses");
        AssertEqual(
            ChatSseParser.TryExtractDelta("""{"type":"response.refusal.delta","delta":"refusal"}""") ?? string.Empty,
            "refusal");
        AssertEqual(
            ChatSseParser.TryExtractDelta("""{"type":"content_block_delta","delta":{"text":"anthropic"}}""") ?? string.Empty,
            "anthropic");
        AssertEqual(
            ChatSseParser.TryExtractDelta("""{"completion":"legacy"}""") ?? string.Empty,
            "legacy");
        }, group: "chat");

        yield return new TestCase("chat sse parser extracts error messages and ignores malformed json", () =>
    {
        AssertEqual(
            ChatSseParser.TryExtractDelta("""{"error":{"message":"bad request"}}""") ?? string.Empty,
            "bad request");
        AssertTrue(
            ChatSseParser.TryExtractDelta("{bad json") is null,
            "Malformed JSON should not escape a Try* parser.");
        }, group: "chat");
    }
}
