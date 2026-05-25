namespace RelayBench.Services;

internal static class CodexOAuthModelCatalog
{
    public const string DefaultModel = "gpt-5.5";

    private static readonly string[] BaseModels =
    [
        DefaultModel,
        "gpt-5.4",
        "gpt-5.4-mini",
        "gpt-5.3-codex",
        "gpt-5.2",
        "codex-auto-review"
    ];

    private static readonly string[] PlusProModels =
    [
        DefaultModel,
        "gpt-5.4",
        "gpt-5.4-mini",
        "gpt-5.3-codex",
        "gpt-5.3-codex-spark",
        "gpt-5.2",
        "codex-auto-review"
    ];

    public static IReadOnlyList<string> GetModels(string? planType)
    {
        var normalized = (planType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "free" or "team" or "business" or "go" => BaseModels,
            _ => PlusProModels
        };
    }
}
