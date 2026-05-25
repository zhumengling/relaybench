namespace RelayBench.Services;

internal readonly record struct TransparentProxyThinkingSuffixResult(
    string ModelName,
    string RawSuffix,
    string Effort,
    long? Budget,
    string? Level,
    bool IncludeThoughts);

internal static class TransparentProxyThinkingSuffix
{
    public static bool TryParse(string model, out TransparentProxyThinkingSuffixResult result)
    {
        result = default;
        var text = (model ?? string.Empty).Trim();
        if (!text.EndsWith(')'))
        {
            return false;
        }

        var openIndex = text.LastIndexOf('(');
        if (openIndex <= 0 || openIndex >= text.Length - 2)
        {
            return false;
        }

        var modelName = text[..openIndex].Trim();
        var rawSuffix = text[(openIndex + 1)..^1].Trim();
        if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(rawSuffix))
        {
            return false;
        }

        if (TryResolveSpecial(rawSuffix, out result, modelName))
        {
            return true;
        }

        if (TryResolveLevel(rawSuffix, out var level))
        {
            result = new TransparentProxyThinkingSuffixResult(
                modelName,
                rawSuffix,
                string.Equals(level, "max", StringComparison.OrdinalIgnoreCase) ? "xhigh" : level,
                Budget: null,
                string.Equals(level, "max", StringComparison.OrdinalIgnoreCase) ? "xhigh" : level,
                IncludeThoughts: true);
            return true;
        }

        if (long.TryParse(rawSuffix, out var budget) && budget >= 0)
        {
            var effort = ConvertBudgetToEffort(budget);
            if (string.IsNullOrWhiteSpace(effort))
            {
                return false;
            }

            result = new TransparentProxyThinkingSuffixResult(
                modelName,
                rawSuffix,
                effort,
                budget,
                Level: null,
                IncludeThoughts: budget > 0);
            return true;
        }

        return false;
    }

    public static string? ConvertBudgetToEffort(long budget)
        => budget switch
        {
            < -1 => null,
            -1 => "auto",
            0 => "none",
            <= 512 => "minimal",
            <= 1024 => "low",
            <= 8192 => "medium",
            <= 24576 => "high",
            _ => "xhigh"
        };

    public static long? ConvertLevelToBudget(string level)
        => level.Trim().ToLowerInvariant() switch
        {
            "none" => 0,
            "auto" => -1,
            "minimal" => 512,
            "low" => 1024,
            "medium" => 8192,
            "high" => 24576,
            "xhigh" => 32768,
            "max" => 128000,
            _ => null
        };

    private static bool TryResolveSpecial(
        string rawSuffix,
        out TransparentProxyThinkingSuffixResult result,
        string modelName)
    {
        result = default;
        var suffix = rawSuffix.Trim().ToLowerInvariant();
        if (suffix == "none")
        {
            result = new TransparentProxyThinkingSuffixResult(
                modelName,
                rawSuffix,
                "none",
                0,
                Level: null,
                IncludeThoughts: false);
            return true;
        }

        if (suffix is "auto" or "-1")
        {
            result = new TransparentProxyThinkingSuffixResult(
                modelName,
                rawSuffix,
                "auto",
                -1,
                Level: null,
                IncludeThoughts: true);
            return true;
        }

        return false;
    }

    private static bool TryResolveLevel(string rawSuffix, out string level)
    {
        level = rawSuffix.Trim().ToLowerInvariant();
        return level is "minimal" or "low" or "medium" or "high" or "xhigh" or "max";
    }
}
