using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private static readonly int[] DefaultConcurrencyPressureStages = [1, 2, 4, 8, 16];
    private const int DefaultConcurrencyPressureStageCycles = 2;

    public async Task<ProxyConcurrencyPressureResult> RunConcurrencyPressureAsync(
        ProxyEndpointSettings settings,
        IReadOnlyList<int>? concurrencyStages = null,
        IProgress<ProxyConcurrencyPressureStageResult>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedStages = NormalizeConcurrencyPressureStages(concurrencyStages);

        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return new ProxyConcurrencyPressureResult(
                DateTimeOffset.Now,
                settings.BaseUrl,
                settings.Model,
                Array.Empty<ProxyConcurrencyPressureStageResult>(),
                null,
                null,
                null,
                "\u5E76\u53D1\u538B\u6D4B\u53C2\u6570\u6821\u9A8C\u5931\u8D25\u3002",
                error);
        }

        using var client = CreateClient(baseUri, normalizedSettings);

        var effectiveModel = normalizedSettings.Model;
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            var modelPath = BuildApiPath(baseUri, "models");
            var modelsProbe = await ProbeModelsAsync(client, modelPath, string.Empty, cancellationToken);
            effectiveModel = modelsProbe.SampleModels.FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(effectiveModel))
            {
                return new ProxyConcurrencyPressureResult(
                    DateTimeOffset.Now,
                    baseUri.ToString(),
                    normalizedSettings.Model,
                    Array.Empty<ProxyConcurrencyPressureStageResult>(),
                    null,
                    null,
                    null,
                    "\u5E76\u53D1\u538B\u6D4B\u65E0\u6CD5\u786E\u5B9A\u53EF\u7528\u6A21\u578B\u3002",
                    modelsProbe.ScenarioResult.Error ?? modelsProbe.ScenarioResult.Summary);
            }
        }

        var chatPath = BuildApiPath(baseUri, "chat/completions");
        List<ProxyConcurrencyPressureStageResult> stages = new(normalizedStages.Length);

        foreach (var concurrency in normalizedStages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var totalRequests = Math.Max(concurrency * DefaultConcurrencyPressureStageCycles, concurrency);
            var attempts = await RunConcurrencyPressureStageAsync(
                client,
                chatPath,
                effectiveModel,
                concurrency,
                totalRequests,
                cancellationToken);

            var stageResult = BuildConcurrencyPressureStageResult(concurrency, attempts);
            stages.Add(stageResult);
            stageProgress?.Report(stageResult);
        }

        var stableConcurrencyLimit = ResolveStableConcurrencyLimit(stages);
        var rateLimitStartConcurrency = stages
            .FirstOrDefault(static stage => stage.RateLimitedCount > 0)?
            .Concurrency;
        var highRiskConcurrency = ResolveHighRiskConcurrency(stages);
        var summary = BuildConcurrencyPressureSummary(
            stages,
            stableConcurrencyLimit,
            rateLimitStartConcurrency,
            highRiskConcurrency);

        return new ProxyConcurrencyPressureResult(
            DateTimeOffset.Now,
            baseUri.ToString(),
            effectiveModel,
            stages,
            stableConcurrencyLimit,
            rateLimitStartConcurrency,
            highRiskConcurrency,
            summary,
            null);
    }

    private static int[] NormalizeConcurrencyPressureStages(IReadOnlyList<int>? stages)
    {
        var normalized = (stages ?? DefaultConcurrencyPressureStages)
            .Where(static value => value > 0)
            .Select(static value => Math.Clamp(value, 1, 64))
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();

        return normalized.Length == 0 ? DefaultConcurrencyPressureStages : normalized;
    }

    private static async Task<IReadOnlyList<ConcurrencyPressureAttemptResult>> RunConcurrencyPressureStageAsync(
        HttpClient client,
        string chatPath,
        string model,
        int concurrency,
        int totalRequests,
        CancellationToken cancellationToken)
    {
        using var limiter = new SemaphoreSlim(concurrency, concurrency);
        var tasks = Enumerable.Range(0, totalRequests)
            .Select(async attemptIndex =>
            {
                await limiter.WaitAsync(cancellationToken);
                try
                {
                    return await RunConcurrencyPressureAttemptAsync(
                        client,
                        chatPath,
                        model,
                        concurrency,
                        attemptIndex,
                        cancellationToken);
                }
                finally
                {
                    limiter.Release();
                }
            })
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    private static async Task<ConcurrencyPressureAttemptResult> RunConcurrencyPressureAttemptAsync(
        HttpClient client,
        string chatPath,
        string model,
        int concurrency,
        int attemptIndex,
        CancellationToken cancellationToken)
    {
        var attemptTag = $"{concurrency:D2}-{attemptIndex:D3}-{Guid.NewGuid():N}";
        var chatProbe = await ProbeJsonScenarioAsync(
            client,
            chatPath,
            BuildConcurrencyPressurePayload(model, stream: false, attemptTag),
            ProxyProbeScenarioKind.ChatCompletions,
            "\u5E76\u53D1\u666E\u901A\u5BF9\u8BDD",
            ParseChatPreview,
            cancellationToken);

        if (!chatProbe.ScenarioResult.Success)
        {
            return BuildFailedConcurrencyPressureAttempt(chatProbe.ScenarioResult, null);
        }

        var streamProbe = await ProbeStreamingScenarioAsync(
            client,
            chatPath,
            BuildConcurrencyPressurePayload(model, stream: true, attemptTag),
            ProxyProbeScenarioKind.ChatCompletionsStream,
            "\u5E76\u53D1\u6D41\u5F0F\u5BF9\u8BDD",
            TryParseChatStreamContent,
            static preview => !string.IsNullOrWhiteSpace(preview),
            cancellationToken);

        if (!streamProbe.Success)
        {
            return BuildFailedConcurrencyPressureAttempt(
                streamProbe,
                chatProbe.ScenarioResult.Latency?.TotalMilliseconds);
        }

        return new ConcurrencyPressureAttemptResult(
            true,
            false,
            false,
            false,
            false,
            chatProbe.ScenarioResult.Latency?.TotalMilliseconds,
            streamProbe.FirstTokenLatency?.TotalMilliseconds,
            streamProbe.OutputTokensPerSecond,
            streamProbe.Summary);
    }

    private static ConcurrencyPressureAttemptResult BuildFailedConcurrencyPressureAttempt(
        ProxyProbeScenarioResult scenario,
        double? chatLatencyMs)
    {
        var isRateLimited = scenario.FailureKind is ProxyFailureKind.RateLimited || scenario.StatusCode == 429;
        var isTimeout = scenario.FailureKind is ProxyFailureKind.Timeout;
        var isServerError = scenario.FailureKind is ProxyFailureKind.Http5xx ||
                            scenario.StatusCode is >= 500 and <= 599;
        var isOtherFailure = !isRateLimited && !isTimeout && !isServerError;

        return new ConcurrencyPressureAttemptResult(
            false,
            isRateLimited,
            isServerError,
            isTimeout,
            isOtherFailure,
            chatLatencyMs,
            null,
            null,
            scenario.Summary);
    }

    private static ProxyConcurrencyPressureStageResult BuildConcurrencyPressureStageResult(
        int concurrency,
        IReadOnlyList<ConcurrencyPressureAttemptResult> attempts)
    {
        var totalRequests = attempts.Count;
        var successCount = attempts.Count(static attempt => attempt.Success);
        var rateLimitedCount = attempts.Count(static attempt => attempt.RateLimited);
        var serverErrorCount = attempts.Count(static attempt => attempt.ServerError);
        var timeoutCount = attempts.Count(static attempt => attempt.Timeout);
        var otherFailureCount = attempts.Count(static attempt => attempt.OtherFailure);
        var successRate = totalRequests == 0 ? 0d : successCount * 100d / totalRequests;

        var successfulAttempts = attempts
            .Where(static attempt => attempt.Success)
            .ToArray();

        var p50ChatLatencyMs = Percentile(successfulAttempts.Select(static attempt => attempt.ChatLatencyMs), 0.50);
        var p95ChatLatencyMs = Percentile(successfulAttempts.Select(static attempt => attempt.ChatLatencyMs), 0.95);
        var p50TtftMs = Percentile(successfulAttempts.Select(static attempt => attempt.TtftMs), 0.50);
        var p95TtftMs = Percentile(successfulAttempts.Select(static attempt => attempt.TtftMs), 0.95);
        var averageTokensPerSecond = Average(successfulAttempts.Select(static attempt => attempt.TokensPerSecond));

        var summary =
            $"\u5E76\u53D1 {concurrency}\uFF1A\u6210\u529F {successCount}/{totalRequests}\uFF08{successRate:F1}%\uFF09\uFF0C429 {rateLimitedCount}\uFF0C5xx {serverErrorCount}\uFF0C\u8D85\u65F6 {timeoutCount}" +
            (otherFailureCount > 0 ? $"\uFF0C\u5176\u5B83 {otherFailureCount}" : string.Empty) +
            $"\uFF1Bp50 \u5EF6\u8FDF {FormatConcurrencyMilliseconds(p50ChatLatencyMs)}\uFF0Cp95 TTFT {FormatConcurrencyMilliseconds(p95TtftMs)}\uFF0Ctok/s {FormatConcurrencyTokensPerSecond(averageTokensPerSecond)}\u3002";

        return new ProxyConcurrencyPressureStageResult(
            concurrency,
            totalRequests,
            successCount,
            rateLimitedCount,
            serverErrorCount,
            timeoutCount,
            p50ChatLatencyMs,
            p95ChatLatencyMs,
            p50TtftMs,
            p95TtftMs,
            averageTokensPerSecond,
            summary);
    }

    private static int? ResolveStableConcurrencyLimit(IReadOnlyList<ProxyConcurrencyPressureStageResult> stages)
    {
        int? stableConcurrency = null;

        foreach (var stage in stages)
        {
            var successRate = stage.TotalRequests == 0 ? 0d : (double)stage.SuccessCount / stage.TotalRequests;
            var isStable = successRate >= 0.95d &&
                           stage.RateLimitedCount == 0 &&
                           stage.TimeoutCount == 0;

            if (isStable)
            {
                stableConcurrency = stage.Concurrency;
            }
        }

        return stableConcurrency;
    }

    private static int? ResolveHighRiskConcurrency(IReadOnlyList<ProxyConcurrencyPressureStageResult> stages)
    {
        if (stages.Count == 0)
        {
            return null;
        }

        var baselineP95TtftMs = stages
            .Select(static stage => stage.P95TtftMs ?? stage.P50TtftMs)
            .FirstOrDefault(static value => value.HasValue);

        foreach (var stage in stages)
        {
            var successRate = stage.TotalRequests == 0 ? 0d : (double)stage.SuccessCount / stage.TotalRequests;
            var ttftHighRisk = baselineP95TtftMs.HasValue &&
                               stage.P95TtftMs.HasValue &&
                               stage.P95TtftMs.Value > baselineP95TtftMs.Value * 2d;

            if (successRate < 0.80d || stage.TimeoutCount > 0 || ttftHighRisk)
            {
                return stage.Concurrency;
            }
        }

        return null;
    }

    private static string BuildConcurrencyPressureSummary(
        IReadOnlyList<ProxyConcurrencyPressureStageResult> stages,
        int? stableConcurrencyLimit,
        int? rateLimitStartConcurrency,
        int? highRiskConcurrency)
    {
        if (stages.Count == 0)
        {
            return "\u5E76\u53D1\u538B\u6D4B\u672A\u91C7\u96C6\u5230\u6709\u6548\u5206\u6863\u7ED3\u679C\u3002";
        }

        return
            $"\u5DF2\u5B8C\u6210 {stages.Count} \u4E2A\u5E76\u53D1\u6863\u4F4D\uFF1B" +
            $"\u7A33\u5B9A\u5E76\u53D1\u4E0A\u9650 {FormatConcurrencyValue(stableConcurrencyLimit)}\uFF1B" +
            $"\u9650\u6D41\u8D77\u70B9 {FormatConcurrencyValue(rateLimitStartConcurrency)}\uFF1B" +
            $"\u9AD8\u98CE\u9669\u6863 {FormatConcurrencyValue(highRiskConcurrency)}\u3002";
    }

    private static string BuildConcurrencyPressurePayload(string model, bool stream, string attemptTag)
    {
        var payload = new
        {
            model,
            max_tokens = 96,
            temperature = 0,
            stream,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = $"You are a concurrency pressure probe. Trace={attemptTag}. Reply with plain text only."
                },
                new
                {
                    role = "user",
                    content = "Output the numbers 1 to 60 separated by spaces. Do not add markdown, labels, or extra words."
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var materialized = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();

        return materialized.Length == 0 ? null : materialized.Average();
    }

    private static double? Percentile(IEnumerable<double?> values, double percentile)
    {
        var ordered = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .OrderBy(static value => value)
            .ToArray();

        if (ordered.Length == 0)
        {
            return null;
        }

        if (ordered.Length == 1)
        {
            return ordered[0];
        }

        var rank = Math.Clamp(percentile, 0d, 1d) * (ordered.Length - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);

        if (lowerIndex == upperIndex)
        {
            return ordered[lowerIndex];
        }

        var weight = rank - lowerIndex;
        return ordered[lowerIndex] + (ordered[upperIndex] - ordered[lowerIndex]) * weight;
    }

    private static string FormatConcurrencyMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:F0} ms" : "--";

    private static string FormatConcurrencyTokensPerSecond(double? value)
        => value.HasValue ? $"{value.Value:F1} tok/s" : "--";

    private static string FormatConcurrencyValue(int? value)
        => value?.ToString() ?? "--";

    private sealed record ConcurrencyPressureAttemptResult(
        bool Success,
        bool RateLimited,
        bool ServerError,
        bool Timeout,
        bool OtherFailure,
        double? ChatLatencyMs,
        double? TtftMs,
        double? TokensPerSecond,
        string Summary);
}
