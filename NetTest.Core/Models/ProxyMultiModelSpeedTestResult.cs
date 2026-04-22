namespace NetTest.Core.Models;

public sealed record ProxyMultiModelSpeedTestResult(
    string Model,
    bool Success,
    int? StatusCode,
    double? OutputTokensPerSecond,
    bool OutputTokenCountEstimated,
    string Summary,
    string? Preview,
    string? Error);
