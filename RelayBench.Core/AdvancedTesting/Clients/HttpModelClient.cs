using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.Services;

namespace RelayBench.Core.AdvancedTesting.Clients;

public sealed class HttpModelClient : IModelClient
{
    private readonly HttpClient _httpClient;
    private readonly AdvancedEndpoint _endpoint;

    public HttpModelClient(AdvancedEndpoint endpoint)
    {
        _endpoint = endpoint;
        var handler = new HttpClientHandler();
        if (endpoint.IgnoreTlsErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                static (_, _, _, errors) => errors == SslPolicyErrors.None || true;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(endpoint.TimeoutSeconds, 5, 300))
        };
    }

    public Task<AdvancedModelExchange> GetAsync(string relativePath, CancellationToken cancellationToken)
        => SendAsync(
            HttpMethod.Get,
            relativePath,
            requestBody: null,
            stream: false,
            extraHeaders: null,
            cancellationToken);

    public Task<AdvancedModelExchange> PostJsonAsync(
        string relativePath,
        string requestBody,
        CancellationToken cancellationToken)
    {
        var prepared = AdvancedWireRequestBuilder.PreparePostJson(
            relativePath,
            requestBody,
            _endpoint.PreferredWireApi,
            stream: false);
        return SendAsync(
            HttpMethod.Post,
            prepared.RelativePath,
            prepared.RequestBody,
            stream: false,
            prepared.ExtraHeaders,
            cancellationToken);
    }

    public Task<AdvancedModelExchange> PostJsonStreamAsync(
        string relativePath,
        string requestBody,
        CancellationToken cancellationToken)
    {
        var prepared = AdvancedWireRequestBuilder.PreparePostJson(
            relativePath,
            requestBody,
            _endpoint.PreferredWireApi,
            stream: true);
        return SendAsync(
            HttpMethod.Post,
            prepared.RelativePath,
            prepared.RequestBody,
            stream: true,
            prepared.ExtraHeaders,
            cancellationToken);
    }

    public void Dispose()
        => _httpClient.Dispose();

    private async Task<AdvancedModelExchange> SendAsync(
        HttpMethod method,
        string relativePath,
        string? requestBody,
        bool stream,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        var requestUrl = BuildUrl(relativePath);
        using var request = new HttpRequestMessage(method, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey.Trim());
        request.Headers.Accept.Add(stream
            ? new MediaTypeWithQualityHeaderValue("text/event-stream")
            : new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("RelayBenchSuite/0.2 AdvancedTestLab");

        if (extraHeaders is not null)
        {
            foreach (var header in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (extraHeaders.ContainsKey("anthropic-version") &&
                !string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", _endpoint.ApiKey.Trim());
            }
        }

        if (requestBody is not null)
        {
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        }

        var requestHeaders = BuildRequestHeaders(request);
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? firstTokenLatency = null;
        var streamLines = new List<string>();

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);

            var responseHeaders = BuildResponseHeaders(response);
            string responseBody;
            if (stream)
            {
                using var streamContent = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(streamContent, Encoding.UTF8);
                StringBuilder builder = new();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (firstTokenLatency is null && line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        firstTokenLatency = stopwatch.Elapsed;
                    }

                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        streamLines.Add(line[5..].Trim());
                    }

                    builder.AppendLine(line);
                }

                responseBody = builder.ToString();
            }
            else
            {
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            return new AdvancedModelExchange(
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                method.Method,
                requestUrl,
                requestHeaders,
                requestBody,
                responseHeaders,
                responseBody,
                stopwatch.Elapsed,
                firstTokenLatency,
                streamLines);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return BuildFailureExchange(
                method,
                requestUrl,
                requestHeaders,
                requestBody,
                $"Request timeout after {_httpClient.Timeout.TotalSeconds:0} seconds.",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return BuildFailureExchange(
                method,
                requestUrl,
                requestHeaders,
                requestBody,
                ex.Message,
                stopwatch.Elapsed);
        }
        catch (AuthenticationException ex)
        {
            stopwatch.Stop();
            return BuildFailureExchange(
                method,
                requestUrl,
                requestHeaders,
                requestBody,
                ex.Message,
                stopwatch.Elapsed);
        }
    }

    private string BuildUrl(string relativePath)
        => EndpointPathBuilder.CombineOpenAiCompatibleUrl(_endpoint.BaseUrl, relativePath);

    private static IReadOnlyDictionary<string, string> BuildRequestHeaders(HttpRequestMessage request)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        return headers;
    }

    private static IReadOnlyDictionary<string, string> BuildResponseHeaders(HttpResponseMessage response)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        return headers;
    }

    private static AdvancedModelExchange BuildFailureExchange(
        HttpMethod method,
        string requestUrl,
        IReadOnlyDictionary<string, string> requestHeaders,
        string? requestBody,
        string responseBody,
        TimeSpan duration)
        => new(
            null,
            string.Empty,
            method.Method,
            requestUrl,
            requestHeaders,
            requestBody,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            responseBody,
            duration,
            null,
            Array.Empty<string>());
}
