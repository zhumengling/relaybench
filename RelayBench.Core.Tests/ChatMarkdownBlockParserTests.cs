using RelayBench.Core.Models;
using RelayBench.Core.Services;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class ChatMarkdownBlockParserTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("markdown parser returns no blocks for empty content", () =>
    {
        AssertTrue(ChatMarkdownBlockParser.Parse(null).Count == 0, "Null markdown should produce no blocks.");
        AssertTrue(ChatMarkdownBlockParser.Parse(string.Empty).Count == 0, "Empty markdown should produce no blocks.");
        }, group: "chat");

        yield return new TestCase("markdown parser separates text and fenced code blocks", () =>
    {
        var blocks = ChatMarkdownBlockParser.Parse(
            """
            Before
            ```csharp
            Console.WriteLine(1);
            ```
            After
            """);

        AssertTrue(blocks.Count == 3, $"Expected 3 blocks, got {blocks.Count}.");
        AssertBlock(blocks[0], ChatContentBlockKind.Text, "Before", null, true);
        AssertBlock(blocks[1], ChatContentBlockKind.Code, "Console.WriteLine(1);", "csharp", true);
        AssertBlock(blocks[2], ChatContentBlockKind.Text, "After", null, true);
        }, group: "chat");

        yield return new TestCase("markdown parser marks unclosed code blocks", () =>
    {
        var blocks = ChatMarkdownBlockParser.Parse(
            """
            Start
            ```
            unfinished
            """);

        AssertTrue(blocks.Count == 2, $"Expected 2 blocks, got {blocks.Count}.");
        AssertBlock(blocks[0], ChatContentBlockKind.Text, "Start", null, true);
        AssertBlock(blocks[1], ChatContentBlockKind.Code, "unfinished", "text", false);
        }, group: "chat");

        yield return new TestCase("markdown parser normalizes language info to first token", () =>
    {
        var blocks = ChatMarkdownBlockParser.Parse(
            """
            ```python linenums
            print(1)
            ```
            """);

        AssertTrue(blocks.Count == 1, $"Expected 1 block, got {blocks.Count}.");
        AssertBlock(blocks[0], ChatContentBlockKind.Code, "print(1)", "python", true);
        }, group: "chat");
    }

    private static void AssertBlock(
        ChatContentBlock block,
        ChatContentBlockKind kind,
        string content,
        string? language,
        bool isClosed)
    {
        AssertTrue(block.Kind == kind, $"Expected block kind {kind}, got {block.Kind}.");
        AssertEqual(block.Content, content);
        AssertTrue(
            string.Equals(block.Language, language, StringComparison.Ordinal),
            $"Expected language {language ?? "<null>"}, got {block.Language ?? "<null>"}.");
        AssertTrue(block.IsClosed == isClosed, $"Expected IsClosed={isClosed}, got {block.IsClosed}.");
    }
}
