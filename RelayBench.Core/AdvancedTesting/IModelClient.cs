using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting;

public interface IModelClient : IDisposable
{
    Task<AdvancedModelExchange> GetAsync(
        string relativePath,
        CancellationToken cancellationToken);

    Task<AdvancedModelExchange> PostJsonAsync(
        string relativePath,
        string requestBody,
        CancellationToken cancellationToken);

    Task<AdvancedModelExchange> PostJsonStreamAsync(
        string relativePath,
        string requestBody,
        CancellationToken cancellationToken);
}

public sealed record AdvancedModelExchange(
    int? StatusCode,
    string ReasonPhrase,
    string RequestMethod,
    string RequestUrl,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? RequestBody,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    string? ResponseBody,
    TimeSpan Duration,
    TimeSpan? FirstTokenLatency,
    IReadOnlyList<string> StreamDataLines)
{
    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;
}
