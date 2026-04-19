namespace NetTest.Core.Support;

public static class TokenCountEstimator
{
    public static int EstimateOutputTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var normalized = text.Trim();
        var cjkCount = 0;
        var latinWordCount = 0;
        var punctuationCount = 0;
        var inLatinWord = false;

        foreach (var character in normalized)
        {
            if (IsCjk(character))
            {
                cjkCount++;
                inLatinWord = false;
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (!inLatinWord)
                {
                    latinWordCount++;
                    inLatinWord = true;
                }

                continue;
            }

            if (!char.IsWhiteSpace(character))
            {
                punctuationCount++;
            }

            inLatinWord = false;
        }

        var estimated = (cjkCount * 0.75d) + (latinWordCount * 1.15d) + (punctuationCount * 0.25d);
        if (estimated <= 0)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(estimated));
    }

    private static bool IsCjk(char character)
        => character is >= '\u4E00' and <= '\u9FFF' ||
           character is >= '\u3400' and <= '\u4DBF' ||
           character is >= '\uF900' and <= '\uFAFF' ||
           character is >= '\u3040' and <= '\u30FF' ||
           character is >= '\uAC00' and <= '\uD7AF';
}
