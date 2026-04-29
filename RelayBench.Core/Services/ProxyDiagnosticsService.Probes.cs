using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    private sealed record OutputMetrics(
        int? OutputTokenCount,
        bool OutputTokenCountEstimated,
        int? OutputCharacterCount,
        TimeSpan? GenerationDuration,
        double? OutputTokensPerSecond,
        double? EndToEndTokensPerSecond);

    private sealed record ModelsProbeOutcome(
        ProxyProbeScenarioResult ScenarioResult,
        int ModelCount,
        IReadOnlyList<string> SampleModels,
        IReadOnlyList<ProxyModelCatalogItem> ModelItems);

    private sealed record ProtocolWireProbeOutcome(
        string ProbeModel,
        bool ChatCompletionsSupported,
        bool ResponsesSupported,
        bool AnthropicMessagesSupported,
        string? PreferredWireApi,
        string Summary);

    private sealed record JsonProbeOutcome(
        ProxyProbeScenarioResult ScenarioResult,
        string? Preview);

    private sealed record StreamingProbeOutcome(
        TimeSpan? FirstTokenLatency,
        TimeSpan Duration,
        string Preview,
        string FullText,
        int ChunkCount,
        bool ReceivedDone,
        int? OutputTokenCount,
        bool OutputTokenCountEstimated,
        int OutputCharacterCount,
        TimeSpan? GenerationDuration,
        double? OutputTokensPerSecond,
        double? EndToEndTokensPerSecond,
        double? MaxChunkGapMilliseconds,
        double? AverageChunkGapMilliseconds);

    private sealed record StreamingReadLiveProgress(
        TimeSpan Elapsed,
        TimeSpan? FirstTokenLatency,
        string Preview,
        string FullText,
        int ChunkCount,
        bool ReceivedDone,
        int? OutputTokenCount,
        bool OutputTokenCountEstimated,
        int OutputCharacterCount,
        double? OutputTokensPerSecond,
        double? EndToEndTokensPerSecond);

}
