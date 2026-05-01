using RelayBench.App.Services;
using RelayBench.App.ViewModels;
using RelayBench.Core.Models;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class ModelChatWorkflowTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("chat message copy text keeps markdown content", () =>
        {
            var message = new ChatMessageViewModel(new ChatMessage(
                "m1",
                "assistant",
                "Here is code:\n\n```csharp\nConsole.WriteLine(1);\n```",
                new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
                Array.Empty<ChatAttachment>(),
                null,
                null));

            AssertEqual(message.CopyText, "Here is code:\n\n```csharp\nConsole.WriteLine(1);\n```");
            AssertTrue(message.CanCopy, "Assistant markdown messages should be copyable.");
        }, group: "chat");

        yield return new TestCase("multi model answer copy text groups answers by model", () =>
        {
            var message = ChatMessageViewModel.CreateMultiModelAnswer(["model-a", "model-b"]);
            message.ModelAnswers[0].AppendDelta("answer a");
            message.ModelAnswers[1].AppendDelta("answer b");

            AssertContains(message.CopyText, "## 1. model-a");
            AssertContains(message.CopyText, "answer a");
            AssertContains(message.CopyText, "## 2. model-b");
            AssertContains(message.CopyText, "answer b");
        }, group: "chat");

        yield return new TestCase("chat message bubble width follows content length", () =>
        {
            var shortMessage = new ChatMessageViewModel(new ChatMessage(
                "u1",
                "user",
                "Hi",
                new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
                Array.Empty<ChatAttachment>(),
                null,
                null));

            var mediumMessage = new ChatMessageViewModel(new ChatMessage(
                "a1",
                "assistant",
                "A concise answer still needs a little more room than a tiny greeting.",
                new DateTimeOffset(2026, 5, 1, 10, 0, 1, TimeSpan.Zero),
                Array.Empty<ChatAttachment>(),
                null,
                null));

            var longMessage = new ChatMessageViewModel(new ChatMessage(
                "a2",
                "assistant",
                new string('x', 5000),
                new DateTimeOffset(2026, 5, 1, 10, 0, 2, TimeSpan.Zero),
                Array.Empty<ChatAttachment>(),
                null,
                null));

            AssertTrue(shortMessage.BubbleWidth < 220, $"Short messages should render as compact bubbles, got {shortMessage.BubbleWidth}.");
            AssertTrue(mediumMessage.BubbleWidth > shortMessage.BubbleWidth, "Longer content should get a wider bubble.");
            AssertTrue(Math.Abs(longMessage.BubbleWidth - 920d) < 0.001d, $"Very long messages should clamp to 920px, got {longMessage.BubbleWidth}.");
        }, group: "chat");

        yield return new TestCase("user chat bubbles reserve right alignment gutter", () =>
        {
            var userMessage = new ChatMessageViewModel(new ChatMessage(
                "u1",
                "user",
                "Move me left a little.",
                new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
                Array.Empty<ChatAttachment>(),
                null,
                null));

            var assistantMessage = new ChatMessageViewModel(new ChatMessage(
                "a1",
                "assistant",
                "Assistant bubbles stay on the shared left axis.",
                new DateTimeOffset(2026, 5, 1, 10, 0, 1, TimeSpan.Zero),
                Array.Empty<ChatAttachment>(),
                null,
                null));

            AssertEqual(userMessage.BubbleHorizontalAlignment, "Right");
            AssertEqual(userMessage.BubbleOuterMargin, "0,0,24,0");
            AssertEqual(assistantMessage.BubbleHorizontalAlignment, "Left");
            AssertEqual(assistantMessage.BubbleOuterMargin, "0");
        }, group: "chat");

        yield return new TestCase("model chat export service writes markdown and text conversations", () =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"relaybench-chat-export-{Guid.NewGuid():N}");
            try
            {
                var service = new ModelChatExportService(tempRoot);
                var messages = new[]
                {
                    new ChatMessage(
                        "u1",
                        "user",
                        "帮我检查接口",
                        new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
                        Array.Empty<ChatAttachment>(),
                        null,
                        null),
                    new ChatMessage(
                        "a1",
                        "assistant",
                        "可以，先看状态码。",
                        new DateTimeOffset(2026, 5, 1, 10, 0, 5, TimeSpan.Zero),
                        Array.Empty<ChatAttachment>(),
                        new ChatMessageMetrics(TimeSpan.FromMilliseconds(850), TimeSpan.FromMilliseconds(220), 8, 9.4, "OpenAI Chat Completions"),
                        null)
                };

                var markdownPath = service.ExportMarkdown("接口排查", messages);
                var textPath = service.ExportText("接口排查", messages);

                AssertTrue(File.Exists(markdownPath), "Markdown export file should be created.");
                AssertTrue(File.Exists(textPath), "Text export file should be created.");
                AssertContains(File.ReadAllText(markdownPath), "# 接口排查");
                AssertContains(File.ReadAllText(markdownPath), "## 用户");
                AssertContains(File.ReadAllText(markdownPath), "OpenAI Chat Completions");
                AssertContains(File.ReadAllText(textPath), "[助手]");
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }, group: "chat");
    }
}
