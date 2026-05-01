using System.Text.Json;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class ChatRequestPayloadBuilderTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("chat completions payload injects system prompt and clamps numeric options", () =>
    {
        using var document = JsonDocument.Parse(ChatRequestPayloadBuilder.BuildChatCompletionsPayload(
            BuildOptions(temperature: 3.5, maxTokens: 300_000, systemPrompt: "  Be concise.  "),
            [BuildUserMessage("Inspect this.", [BuildTextAttachment("notes.cs", "Console.WriteLine(1);"), BuildImageAttachment()])]));
        var root = document.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();

        AssertEqual(root.GetProperty("model").GetString() ?? string.Empty, "gpt-test");
        AssertTrue(root.GetProperty("stream").GetBoolean(), "Chat payload should stream.");
        AssertTrue(root.GetProperty("temperature").GetDouble() == 2d, "Temperature should be clamped to 2.");
        AssertTrue(root.GetProperty("max_tokens").GetInt32() == 200_000, "max_tokens should be clamped to 200000.");
        AssertEqual(messages[0].GetProperty("role").GetString() ?? string.Empty, "system");
        AssertEqual(messages[0].GetProperty("content").GetString() ?? string.Empty, "Be concise.");

        var userContent = messages[1].GetProperty("content").EnumerateArray().ToArray();
        AssertContains(userContent[0].GetProperty("text").GetString(), "Attached text file:");
        AssertContains(userContent[0].GetProperty("text").GetString(), "```csharp");
        AssertContains(userContent[1].GetProperty("image_url").GetProperty("url").GetString(), "data:image/png;base64,");
        }, group: "chat");

        yield return new TestCase("responses payload preserves system prompt and reasoning effort", () =>
    {
        using var document = JsonDocument.Parse(ChatRequestPayloadBuilder.BuildResponsesPayload(
            BuildOptions(maxTokens: 300_000, systemPrompt: "System line.", reasoningEffort: ChatReasoningEffort.High),
            [BuildUserMessage("Hello.")]));
        var root = document.RootElement;
        var input = root.GetProperty("input").GetString() ?? string.Empty;

        AssertEqual(root.GetProperty("model").GetString() ?? string.Empty, "gpt-test");
        AssertTrue(root.GetProperty("stream").GetBoolean(), "Responses payload should stream.");
        AssertTrue(root.GetProperty("max_output_tokens").GetInt32() == 200_000, "max_output_tokens should be clamped.");
        AssertEqual(root.GetProperty("reasoning").GetProperty("effort").GetString() ?? string.Empty, "high");
        AssertContains(input, "[system]");
        AssertContains(input, "System line.");
        AssertContains(input, "[user]");
        AssertContains(input, "Hello.");
        }, group: "chat");

        yield return new TestCase("anthropic messages payload disables thinking and converts image data urls", () =>
    {
        using var document = JsonDocument.Parse(ChatRequestPayloadBuilder.BuildAnthropicMessagesPayload(
            BuildOptions(temperature: 3.5, maxTokens: 64, systemPrompt: "Anthropic system."),
            [BuildUserMessage(string.Empty, [BuildImageAttachment()])]));
        var root = document.RootElement;
        var userMessage = root.GetProperty("messages")[0];
        var contentItems = userMessage.GetProperty("content").EnumerateArray().ToArray();

        AssertEqual(root.GetProperty("model").GetString() ?? string.Empty, "gpt-test");
        AssertTrue(root.GetProperty("temperature").GetDouble() == 1d, "Anthropic temperature should be clamped to 1.");
        AssertTrue(root.GetProperty("max_tokens").GetInt32() == 512, "Anthropic payload should enforce a 512 token minimum.");
        AssertEqual(root.GetProperty("thinking").GetProperty("type").GetString() ?? string.Empty, "disabled");
        AssertEqual(root.GetProperty("system").GetString() ?? string.Empty, "Anthropic system.");
        AssertEqual(contentItems[0].GetProperty("type").GetString() ?? string.Empty, "image");
        AssertEqual(contentItems[0].GetProperty("source").GetProperty("media_type").GetString() ?? string.Empty, "image/png");
        }, group: "chat");
    }

    private static ChatRequestOptions BuildOptions(
        double temperature = 0.4,
        int maxTokens = 128,
        string systemPrompt = "",
        ChatReasoningEffort reasoningEffort = ChatReasoningEffort.Auto)
        => new(
            "https://relay.example.com/v1",
            "sk-test",
            "  gpt-test  ",
            systemPrompt,
            temperature,
            maxTokens,
            false,
            20,
            reasoningEffort,
            false);

    private static ChatMessage BuildUserMessage(string content, IReadOnlyList<ChatAttachment>? attachments = null)
        => new(
            Guid.NewGuid().ToString("N"),
            "user",
            content,
            DateTimeOffset.UnixEpoch,
            attachments ?? Array.Empty<ChatAttachment>(),
            null,
            null);

    private static ChatAttachment BuildTextAttachment(string fileName, string content)
        => new(
            Guid.NewGuid().ToString("N"),
            ChatAttachmentKind.TextFile,
            fileName,
            "text/plain",
            content.Length,
            content);

    private static ChatAttachment BuildImageAttachment()
        => new(
            Guid.NewGuid().ToString("N"),
            ChatAttachmentKind.Image,
            "pixel.png",
            "image/png",
            16,
            "data:image/png;base64,QUJDRA==");
}
