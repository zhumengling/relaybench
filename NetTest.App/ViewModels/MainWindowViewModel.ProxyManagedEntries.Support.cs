using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static string BuildManagedEntryPlacementAdvice(int rank, int total, bool alreadyManaged)
    {
        if (total <= 1)
        {
            return alreadyManaged
                ? "目前只有这一个可比入口，建议继续补更多 URL 再观察。"
                : "当前只有一个可比入口；如果准备长期使用，建议把这个 URL 加入入口组持续跟踪。";
        }

        if (rank == 1)
        {
            return alreadyManaged
                ? "当前 URL 暂列最优，可优先使用。"
                : "从当前参照结果看，这个 URL 暂列最优；如果准备长期使用，建议把它加入入口组持续跟踪。";
        }

        if (rank <= Math.Max(2, (int)Math.Ceiling(total / 2d)))
        {
            return "当前 URL 处于中上游，可继续用；若你只想挑最稳的，优先试 TOP 1。";
        }

        return "当前 URL 在已观测入口中靠后，建议优先试上面的更稳 URL。";
    }

    private static string BuildManagedEntryTopLine(ManagedEntryAssessment item, int rank)
    {
        var currentMark = item.IsCurrentUrl ? "（当前）" : string.Empty;
        return $"TOP {rank}  {item.DisplayName}{currentMark}  |  {item.StabilityLabel}  |  普通延迟 {FormatMillisecondsValue(item.ChatLatencyMs)}  |  TTFT {FormatMillisecondsValue(item.TtftMs)}  |  依据 {item.SourceSummary}";
    }

    private const int ManagedEntryCapabilityTotal = 5;

    private sealed record ManagedEntryReference(
        string DisplayName,
        string BaseUrl,
        string NormalizedBaseUrl);

    private sealed record ManagedEntryAssessment(
        string DisplayName,
        string BaseUrl,
        string NormalizedBaseUrl,
        bool IsCurrentUrl,
        bool FromManagedList,
        double StabilityScore,
        double? ChatLatencyMs,
        double? TtftMs,
        string StabilityLabel,
        string SourceSummary);
}
