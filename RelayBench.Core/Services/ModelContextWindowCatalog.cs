using System.Text.RegularExpressions;

namespace RelayBench.Core.Services;

public static partial class ModelContextWindowCatalog
{
    private const double AutoCompactRatio = 0.9d;

    private static readonly IReadOnlyList<ModelContextRule> Rules =
    [
        new(@"^gpt-5\.5(?:-|$)", 1_050_000),
        new(@"^gpt-5\.4(?:-|$)", 1_050_000),
        new(@"^gpt-5(?:-|$)", 400_000),
        new(@"^gpt-4\.1(?:-|$)", 1_047_576),
        new(@"^gpt-4o(?:-|$)", 128_000),
        new(@"^gpt-4(?:-|$)", 128_000),
        new(@"^o[134](?:-|$)", 200_000),
        new(@"^o4-mini(?:-|$)", 200_000),
        new(@"^o3-mini(?:-|$)", 200_000),
        new(@"^o1-mini(?:-|$)", 128_000),

        new(@"^claude-(?:opus|sonnet|haiku)-4", 200_000),
        new(@"^claude-3\.7", 200_000),
        new(@"^claude-3\.5", 200_000),
        new(@"^claude-3-", 200_000),

        new(@"^gemini-2\.5-pro", 1_048_576),
        new(@"^gemini-2\.5", 1_048_576),
        new(@"^gemini-2\.0", 1_048_576),
        new(@"^gemini-1\.5-pro", 2_097_152),
        new(@"^gemini-1\.5", 1_048_576),

        new(@"^deepseek-v4", 1_000_000),
        new(@"^deepseek-(?:chat|reasoner|v3|r1)", 128_000),

        new(@"^grok-4", 2_000_000),
        new(@"^grok-3", 131_072),
        new(@"^grok-2", 131_072),

        new(@"^mistral-large", 131_072),
        new(@"^mistral-medium", 32_768),
        new(@"^mistral-small", 32_768),
        new(@"^codestral", 32_768),
        new(@"^ministral", 131_072),
        new(@"^pixtral-large", 131_072),
        new(@"^mixtral", 32_000),

        new(@"^command-a", 256_000),
        new(@"^command-r", 128_000),

        new(@"^llama-4-scout", 10_000_000),
        new(@"^llama-4-maverick", 1_000_000),
        new(@"^llama-3\.[13]", 128_000),
        new(@"^llama-3", 8_192),
        new(@"^llama-2", 4_096),

        new(@"^qwen3\.6", 262_144),
        new(@"^qwen3", 32_768),
        new(@"^qwen2\.5", 32_768),
        new(@"^qwq", 32_768)
    ];

    public static int? ResolveContextWindow(string? model, int? discoveredContextWindow = null)
    {
        if (IsValidContextWindow(discoveredContextWindow))
        {
            return discoveredContextWindow;
        }

        var normalized = NormalizeModelName(model);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        foreach (var rule in Rules)
        {
            if (rule.IsMatch(normalized))
            {
                return rule.ContextWindow;
            }
        }

        return null;
    }

    public static int? CalculateAutoCompactTokenLimit(int? contextWindow)
        => contextWindow is >= 1024 and <= 20_000_000
            ? Math.Max(1024, (int)Math.Floor(contextWindow.Value * AutoCompactRatio))
            : null;

    private static bool IsValidContextWindow(int? value)
        => value is >= 1024 and <= 20_000_000;

    private static string NormalizeModelName(string? model)
    {
        var value = (model ?? string.Empty).Trim().ToLowerInvariant();
        var slashIndex = value.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < value.Length - 1)
        {
            value = value[(slashIndex + 1)..];
        }

        return value;
    }

    private sealed partial record ModelContextRule(string Pattern, int ContextWindow)
    {
        public bool IsMatch(string model)
            => Regex.IsMatch(model, Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
