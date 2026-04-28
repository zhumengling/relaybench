using System.Text;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public static class ChatMarkdownBlockParser
{
    public static IReadOnlyList<ChatContentBlock> Parse(string? markdown)
    {
        var content = markdown ?? string.Empty;
        if (content.Length == 0)
        {
            return Array.Empty<ChatContentBlock>();
        }

        List<ChatContentBlock> blocks = [];
        StringBuilder buffer = new();
        var inCodeBlock = false;
        string? codeLanguage = null;

        foreach (var line in SplitLines(content))
        {
            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    blocks.Add(new ChatContentBlock(
                        ChatContentBlockKind.Code,
                        TrimTrailingLineBreak(buffer.ToString()),
                        string.IsNullOrWhiteSpace(codeLanguage) ? "text" : codeLanguage,
                        true));
                    buffer.Clear();
                    inCodeBlock = false;
                    codeLanguage = null;
                }
                else
                {
                    AppendBufferedText(blocks, buffer);
                    codeLanguage = NormalizeLanguage(trimmedStart[3..]);
                    inCodeBlock = true;
                }

                continue;
            }

            buffer.AppendLine(line);
        }

        if (inCodeBlock)
        {
            blocks.Add(new ChatContentBlock(
                ChatContentBlockKind.Code,
                TrimTrailingLineBreak(buffer.ToString()),
                string.IsNullOrWhiteSpace(codeLanguage) ? "text" : codeLanguage,
                false));
        }
        else
        {
            AppendBufferedText(blocks, buffer);
        }

        return blocks;
    }

    private static void AppendBufferedText(ICollection<ChatContentBlock> blocks, StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var text = TrimTrailingLineBreak(buffer.ToString());
        if (text.Length > 0)
        {
            blocks.Add(new ChatContentBlock(ChatContentBlockKind.Text, text, null, true));
        }

        buffer.Clear();
    }

    private static string NormalizeLanguage(string raw)
    {
        var language = raw.Trim();
        if (language.Length == 0)
        {
            return "text";
        }

        var firstWhitespace = language.IndexOfAny([' ', '\t']);
        return firstWhitespace > 0 ? language[..firstWhitespace] : language;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using StringReader reader = new(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }

        if (text.EndsWith('\n'))
        {
            yield return string.Empty;
        }
    }

    private static string TrimTrailingLineBreak(string text)
        => text.TrimEnd('\r', '\n');
}
