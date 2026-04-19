using NetTest.App.Infrastructure;
using NetTest.App.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static string TryBuildRelayDisplayName(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return baseUrl;
    }

    private static string NormalizeArtifactContent(string? value)
        => string.IsNullOrWhiteSpace(value) ? "（空）" : value.Trim();

    private sealed record RelayRecommendationSnapshot(
        string Source,
        string? Name,
        string? BaseUrl,
        int? Score,
        string Summary);

    private sealed record TrendSummarySnapshot(
        string Target,
        int SampleCount,
        string Summary,
        double? EarlierStability,
        double? LaterStability,
        double? EarlierChatLatency,
        double? LaterChatLatency,
        double? EarlierTtft,
        double? LaterTtft);

    private sealed record UnlockSemanticSnapshot(
        string Summary,
        int? ReadyCount,
        int? AuthRequiredCount,
        int? RegionRestrictedCount,
        int? ReviewRequiredCount,
        int? TotalCount);

    private sealed record NatReviewSnapshot(
        string Summary,
        string? NatType,
        string? Confidence,
        string? CoverageSummary,
        string? ReviewRecommendation);
}
