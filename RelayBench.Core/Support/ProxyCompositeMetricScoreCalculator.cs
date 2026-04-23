namespace RelayBench.Core.Support;

public static class ProxyCompositeMetricScoreCalculator
{
    public static double CalculateCompositeScore(
        double? chatLatencyMs,
        double? ttftMs,
        double? throughputTokensPerSecond,
        (double Min, double Max) chatLatencyRange,
        (double Min, double Max) ttftRange,
        (double Min, double Max) throughputRange)
    {
        var chatScore = NormalizeLowerBetter(chatLatencyMs, chatLatencyRange);
        var ttftScore = NormalizeLowerBetter(ttftMs, ttftRange);
        var throughputScore = NormalizeHigherBetter(throughputTokensPerSecond, throughputRange);

        return Math.Round((chatScore * 0.35d) + (ttftScore * 0.35d) + (throughputScore * 0.30d), 1);
    }

    public static double NormalizeLowerBetter(double? value, (double Min, double Max) range)
    {
        if (!value.HasValue)
        {
            return 0d;
        }

        var min = Math.Min(range.Min, range.Max);
        var max = Math.Max(range.Min, range.Max);
        if (max <= min)
        {
            return 100d;
        }

        var ratio = Math.Clamp((value.Value - min) / (max - min), 0d, 1d);
        return Math.Round((1d - ratio) * 100d, 1);
    }

    public static double NormalizeHigherBetter(double? value, (double Min, double Max) range)
    {
        if (!value.HasValue)
        {
            return 0d;
        }

        var min = Math.Min(range.Min, range.Max);
        var max = Math.Max(range.Min, range.Max);
        if (max <= min)
        {
            return 100d;
        }

        var ratio = Math.Clamp((value.Value - min) / (max - min), 0d, 1d);
        return Math.Round(ratio * 100d, 1);
    }
}
