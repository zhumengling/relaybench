namespace RelayBench.Core.Models;

public sealed record ProxyEndpointSettings(
    string BaseUrl,
    string ApiKey,
    string Model,
    bool IgnoreTlsErrors,
    int TimeoutSeconds,
    string? EmbeddingsModel = null,
    string? ImagesModel = null,
    string? AudioTranscriptionModel = null,
    string? AudioSpeechModel = null,
    string? ModerationModel = null);
