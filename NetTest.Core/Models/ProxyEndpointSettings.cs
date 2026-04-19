namespace NetTest.Core.Models;

public sealed record ProxyEndpointSettings(
    string BaseUrl,
    string ApiKey,
    string Model,
    bool IgnoreTlsErrors,
    int TimeoutSeconds);
