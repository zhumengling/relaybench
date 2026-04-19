using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task<IReadOnlyList<ProxyBatchProbeRow>> ProbeBatchEntriesAsync(
        IReadOnlyList<ProxyBatchTargetEntry> entries,
        int timeoutSeconds,
        bool enableLongStreamingTest,
        int longStreamSegmentCount,
        IProgress<string>? progress,
        IProgress<ProxyBatchProbeRow>? rowProgress)
    {
        var concurrency = Math.Clamp(entries.Count, 1, 4);
        using SemaphoreSlim gate = new(concurrency, concurrency);
        var completed = 0;

        var tasks = entries.Select(async entry =>
        {
            await gate.WaitAsync();
            try
            {
                var settings = new ProxyEndpointSettings(
                    entry.BaseUrl,
                    entry.ApiKey,
                    entry.Model,
                    ProxyIgnoreTlsErrors,
                    timeoutSeconds);

                progress?.Report($"正在探测 {entry.Name}：基础兼容性诊断...");
                var result = await _proxyDiagnosticsService.RunAsync(settings);
                if (enableLongStreamingTest)
                {
                    progress?.Report($"正在探测 {entry.Name}：长流稳定简测（{longStreamSegmentCount} 段）...");
                    var longStreamingSettings = string.IsNullOrWhiteSpace(result.EffectiveModel)
                        ? settings
                        : settings with { Model = result.EffectiveModel };
                    var longStreamingResult = await _proxyDiagnosticsService.RunLongStreamingTestAsync(
                        longStreamingSettings,
                        longStreamSegmentCount);
                    result = result with { LongStreamingResult = longStreamingResult };
                }

                var row = new ProxyBatchProbeRow(entry, result, ComputeProxyBatchScore(result));
                var done = Interlocked.Increment(ref completed);
                progress?.Report($"正在探测 {done}/{entries.Count}：{entry.Name}");
                rowProgress?.Report(row);
                return row;
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    private static bool IsFullSuccess(ProxyDiagnosticsResult result)
        => result.ModelsRequestSucceeded && result.ChatRequestSucceeded && result.StreamRequestSucceeded;

    private static int ComputeProxyBatchScore(ProxyDiagnosticsResult result)
    {
        var score = 0;
        var responsesScenario = result.ScenarioResults?.FirstOrDefault(item => item.Scenario == ProxyProbeScenarioKind.Responses);
        var structuredOutputScenario = result.ScenarioResults?.FirstOrDefault(item => item.Scenario == ProxyProbeScenarioKind.StructuredOutput);

        if (result.ModelsRequestSucceeded)
        {
            score += 18;
        }

        if (result.ChatRequestSucceeded)
        {
            score += 24;
        }

        if (result.StreamRequestSucceeded)
        {
            score += 24;
        }

        if (responsesScenario?.Success == true)
        {
            score += 18;
        }

        if (structuredOutputScenario?.Success == true)
        {
            score += 8;
        }

        if ((result.ResolvedAddresses?.Count ?? 0) <= 1 && string.IsNullOrWhiteSpace(result.CdnProvider))
        {
            score += 2;
        }

        if (string.IsNullOrWhiteSpace(result.Error))
        {
            score += 8;
        }

        if (result.PrimaryFailureKind is ProxyFailureKind.AuthRejected or ProxyFailureKind.TlsHandshakeFailure or ProxyFailureKind.Timeout)
        {
            score -= 12;
        }
        else if (result.PrimaryFailureKind is ProxyFailureKind.UnsupportedEndpoint or ProxyFailureKind.SemanticMismatch)
        {
            score -= 6;
        }

        if ((result.ResolvedAddresses?.Count ?? 0) >= 4)
        {
            score -= 2;
        }

        score -= GetLatencyPenalty(result.ChatLatency?.TotalMilliseconds, 900, 1_800, 4_000, 2, 6, 12);
        score -= GetLatencyPenalty(result.StreamFirstTokenLatency?.TotalMilliseconds, 600, 1_500, 3_000, 2, 5, 10);
        return Math.Clamp(score, 0, 100);
    }

    private static int GetLatencyPenalty(
        double? milliseconds,
        double mildThreshold,
        double mediumThreshold,
        double highThreshold,
        int mildPenalty,
        int mediumPenalty,
        int highPenalty)
    {
        if (milliseconds is null)
        {
            return 0;
        }

        if (milliseconds.Value >= highThreshold)
        {
            return highPenalty;
        }

        if (milliseconds.Value >= mediumThreshold)
        {
            return mediumPenalty;
        }

        return milliseconds.Value >= mildThreshold ? mildPenalty : 0;
    }

    private static string BuildProxyBatchCardStatus(IReadOnlyList<ProxyBatchProbeRow> rows)
        => BuildProxyBatchCardStatus(OrderBatchAggregateRows(BuildProxyBatchAggregateRows(new[] { rows })).ToArray());

    private static string BuildProxyBatchCardStatus(IReadOnlyList<ProxyBatchAggregateRow> rows)
    {
        if (rows.Count == 0)
        {
            return "未运行";
        }

        return BuildBatchStabilityLabel(rows[0]) switch
        {
            "稳定" => "入口组稳定",
            "可用" => "需复核",
            _ => "失败"
        };
    }

    private static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "未填写";
        }

        var text = apiKey.Trim();
        if (text.Length <= 10)
        {
            return new string('*', Math.Max(4, text.Length));
        }

        return $"{text[..6]}...{text[^4..]}";
    }
}
