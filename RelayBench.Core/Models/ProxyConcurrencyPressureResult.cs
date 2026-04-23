namespace RelayBench.Core.Models;

public sealed record ProxyConcurrencyPressureResult(
    DateTimeOffset TestedAt,
    string BaseUrl,
    string Model,
    IReadOnlyList<ProxyConcurrencyPressureStageResult> Stages,
    int? StableConcurrencyLimit,
    int? RateLimitStartConcurrency,
    int? HighRiskConcurrency,
    string Summary,
    string? Error);
