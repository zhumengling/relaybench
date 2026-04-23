using System.Text.RegularExpressions;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly Regex NumericFractionRegex = new(
        @"(?<current>\d+)\s*/\s*(?<total>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RouteHopNumberRegex = new(
        @"#\s*(?<hop>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private void UpdateGlobalTaskProgressForClientApiMessage(string? message)
    {
        if (!IsGlobalTaskProgressRunning || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (TryParseFraction(message, out var current, out var total))
        {
            var safeCurrent = Math.Clamp(current, 1, Math.Max(1, total));
            var percent = 12d + ((double)safeCurrent - 1d) / Math.Max(1d, total) * 72d;
            UpdateGlobalTaskProgress($"\u68C0\u67E5 {safeCurrent}/{total}", percent);
            return;
        }

        UpdateGlobalTaskProgress("\u9274\u5B9A\u4E2D", 28d);
    }

    private void UpdateGlobalTaskProgressForRouteMessage(string? message)
    {
        if (!IsGlobalTaskProgressRunning || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (message.StartsWith(RouteDiagnosticsProgressTokens.RawLinePrefix, StringComparison.Ordinal) ||
            message.StartsWith(RouteDiagnosticsProgressTokens.HopPreviewPrefix, StringComparison.Ordinal))
        {
            return;
        }

        if (message.Contains("\u89E3\u6790", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u89E3\u6790\u4E2D", 12d);
            return;
        }

        if (message.Contains("\u5185\u7F6E ICMP MTR", StringComparison.Ordinal) &&
            TryParseFraction(message, out var mtrCurrent, out var mtrTotal))
        {
            var percent = 20d + Math.Clamp((double)mtrCurrent / Math.Max(1, mtrTotal), 0d, 1d) * 48d;
            UpdateGlobalTaskProgress($"\u7B2C {mtrCurrent}/{mtrTotal} \u8DF3", percent);
            return;
        }

        if (message.Contains("tracert", StringComparison.OrdinalIgnoreCase))
        {
            UpdateGlobalTaskProgress("\u63A2\u8DEF\u4E2D", 24d);
            return;
        }

        if (message.Contains("\u91C7\u6837", StringComparison.Ordinal) &&
            TryParseFraction(message, out var sampleCurrent, out var sampleTotal))
        {
            var percent = 28d + Math.Clamp((double)sampleCurrent / Math.Max(1, sampleTotal), 0d, 1d) * 46d;
            UpdateGlobalTaskProgress($"\u91C7\u6837 {sampleCurrent}/{sampleTotal}", percent);
            return;
        }

        if (message.Contains("\u5B9A\u4F4D", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u5B9A\u4F4D\u4E2D", 82d);
            return;
        }

        if (message.Contains("\u5730\u56FE", StringComparison.Ordinal) ||
            message.Contains("\u7ED8\u5236", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u7ED8\u56FE\u4E2D", 90d);
        }
    }

    private void UpdateGlobalTaskProgressForRouteHopPreview(string? preview)
    {
        if (!IsGlobalTaskProgressRunning || string.IsNullOrWhiteSpace(preview))
        {
            return;
        }

        var match = RouteHopNumberRegex.Match(preview);
        if (!match.Success || !int.TryParse(match.Groups["hop"].Value, out var hopNumber))
        {
            return;
        }

        var maxHops = ParseBoundedInt(RouteMaxHopsText, fallback: 20, min: 1, max: 30);
        var percent = 22d + Math.Clamp((double)hopNumber / maxHops, 0d, 1d) * 44d;
        UpdateGlobalTaskProgress($"\u7B2C {hopNumber}/{maxHops} \u8DF3", percent);
    }

    private void UpdateGlobalTaskProgressForContinuousRouteWindow(
        DateTimeOffset startedAt,
        DateTimeOffset endsAt,
        string shortStatus)
    {
        if (!IsGlobalTaskProgressRunning)
        {
            return;
        }

        var total = (endsAt - startedAt).TotalSeconds;
        if (total <= 0d)
        {
            return;
        }

        var elapsed = Math.Clamp((DateTimeOffset.Now - startedAt).TotalSeconds, 0d, total);
        var percent = 8d + elapsed / total * 86d;
        UpdateGlobalTaskProgress(shortStatus, percent);
    }

    private void UpdateGlobalTaskProgressForPortScanUpdate(PortScanProgressUpdate update)
    {
        if (!IsGlobalTaskProgressRunning)
        {
            return;
        }

        var total = Math.Max(update.TotalEndpointCount, 1);
        var percent = 12d + Math.Clamp((double)update.CompletedEndpointCount / total, 0d, 1d) * 82d;
        UpdateGlobalTaskProgress("\u626B\u63CF\u4E2D", percent);
    }

    private void UpdateGlobalTaskProgressForBatchPortScan(int completed, int total)
    {
        if (!IsGlobalTaskProgressRunning)
        {
            return;
        }

        var safeTotal = Math.Max(total, 1);
        var safeCompleted = Math.Clamp(completed, 0, safeTotal);
        var percent = 10d + (double)safeCompleted / safeTotal * 84d;
        UpdateGlobalTaskProgress($"\u7B2C {safeCompleted}/{safeTotal} \u9879", percent);
    }

    private void UpdateGlobalTaskProgressForSplitRoutingMessage(string? message)
    {
        if (!IsGlobalTaskProgressRunning || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (message.Contains("\u516C\u7F51\u51FA\u53E3", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u51FA\u53E3\u4E2D", 24d);
            return;
        }

        if (message.Contains("DNS", StringComparison.OrdinalIgnoreCase))
        {
            UpdateGlobalTaskProgress("DNS \u4E2D", 52d);
            return;
        }

        if (message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase))
        {
            UpdateGlobalTaskProgress("HTTPS \u4E2D", 76d);
            return;
        }

        if (message.Contains("ASN", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("IP", StringComparison.OrdinalIgnoreCase))
        {
            UpdateGlobalTaskProgress("\u5F52\u5C5E\u4E2D", 90d);
        }
    }

    private void UpdateGlobalTaskProgressForIpRiskMessage(string? message)
    {
        if (!IsGlobalTaskProgressRunning || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (message.Contains("\u8BC6\u522B", StringComparison.Ordinal) ||
            message.Contains("\u51FA\u53E3 IP", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u51FA\u53E3\u4E2D", 18d);
            return;
        }

        if (TryParseFraction(message, out var current, out var total))
        {
            var safeTotal = Math.Max(total, 1);
            var safeCurrent = Math.Clamp(current, 1, safeTotal);
            var percent = 28d + (double)safeCurrent / safeTotal * 56d;
            UpdateGlobalTaskProgress($"\u7B2C {safeCurrent}/{safeTotal} \u6E90", percent);
            return;
        }

        if (message.Contains("\u6C47\u603B", StringComparison.Ordinal) ||
            message.Contains("\u603B\u7ED3", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u6C47\u603B\u4E2D", 92d);
        }
    }

    private void UpdateGlobalTaskProgressForSpeedTestMessage(string? message)
    {
        if (!IsGlobalTaskProgressRunning || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (message.Contains("\u7A7A\u95F2\u5EF6\u8FDF", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u5EF6\u8FDF\u4E2D", 18d);
            return;
        }

        if (message.Contains("\u4E0B\u8F7D", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u4E0B\u8F7D\u4E2D", 44d);
            return;
        }

        if (message.Contains("\u4E0A\u4F20", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u4E0A\u4F20\u4E2D", 72d);
            return;
        }

        if (message.Contains("\u4E22\u5305", StringComparison.Ordinal))
        {
            UpdateGlobalTaskProgress("\u4E22\u5305\u4E2D", 90d);
        }
    }

    private static bool TryParseFraction(string text, out int current, out int total)
    {
        current = 0;
        total = 0;

        var match = NumericFractionRegex.Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups["current"].Value, out current) ||
            !int.TryParse(match.Groups["total"].Value, out total) ||
            total <= 0)
        {
            current = 0;
            total = 0;
            return false;
        }

        return true;
    }
}
