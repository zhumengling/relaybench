namespace NetTest.App.Services;

public sealed record ProxySingleCapabilityChartItem(
    int Order,
    string SectionName,
    string SectionHint,
    string Name,
    string StatusText,
    bool IsCompleted,
    bool Success,
    int? StatusCode,
    double? MetricValueMs,
    string MetricText,
    bool ReceivedDone,
    string DetailText,
    string PreviewText);
