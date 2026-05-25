using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace RelayBench.Services;

/// <summary>
/// Result of a proxy self-test attempt.
/// </summary>
public sealed record SelfTestResult(
    bool Success,
    TimeSpan Latency,
    string? ErrorMessage);

/// <summary>
/// Sends a lightweight request through the local transparent proxy to verify it is
/// correctly forwarding requests.
/// </summary>
public sealed class ProxySelfTestService
{
    private static readonly Uri TestTargetUri = new("http://httpbin.org/status/200");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sends a lightweight HTTP request through the proxy and measures round-trip time.
    /// </summary>
    /// <param name="proxyAddress">Local proxy listen address (e.g., "http://127.0.0.1:8080").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with success/failure, latency, and error details.</returns>
    public async Task<SelfTestResult> RunAsync(string proxyAddress, CancellationToken ct = default)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(proxyAddress),
            UseProxy = true
        };

        using var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client.GetAsync(TestTargetUri, ct).ConfigureAwait(false);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                return new SelfTestResult(true, stopwatch.Elapsed, null);
            }

            return new SelfTestResult(
                false,
                stopwatch.Elapsed,
                $"Unexpected status code: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            stopwatch.Stop();
            return new SelfTestResult(
                false,
                stopwatch.Elapsed,
                "Connection refused: proxy may not be running");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new SelfTestResult(
                false,
                stopwatch.Elapsed,
                "Timeout: proxy did not respond within 10 seconds");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new SelfTestResult(
                false,
                stopwatch.Elapsed,
                $"Protocol error: {ex.Message}");
        }
    }

    private static bool IsConnectionRefused(HttpRequestException ex)
    {
        // SocketError.ConnectionRefused is 10061 on Windows
        if (ex.InnerException is System.Net.Sockets.SocketException socketEx &&
            socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
        {
            return true;
        }

        // Fallback: check message for common connection refused indicators
        return ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase);
    }
}
