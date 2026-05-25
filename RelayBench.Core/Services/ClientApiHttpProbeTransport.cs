using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class ClientApiHttpProbeTransport : IClientApiProbeTransport
{
    private static readonly HttpClient HttpClient = CreateClient();

    public async Task<ClientApiProbeResponse> ProbeAsync(
        Uri url,
        HttpMethod method,
        string provider,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, url);
        ConfigureRequest(request, provider, method);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            stopwatch.Stop();
            var statusCode = (int)response.StatusCode;
            var evidence = await ReadBodySampleAsync(response, cancellationToken);

            return new ClientApiProbeResponse(
                statusCode,
                stopwatch.Elapsed,
                BuildVerdict(statusCode),
                evidence,
                null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ClientApiProbeResponse(
                null,
                stopwatch.Elapsed,
                "API 不可达",
                null,
                ex.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RelayBench", "1.0"));
        return client;
    }

    private static void ConfigureRequest(HttpRequestMessage request, string provider, HttpMethod method)
    {
        if (!string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase) ||
            method != HttpMethod.Post)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = new StringContent(
            """
            {
              "model": "claude-3-5-sonnet-latest",
              "max_tokens": 8,
              "messages": [
                {
                  "role": "user",
                  "content": "ping"
                }
              ]
            }
            """,
            Encoding.UTF8,
            "application/json");
    }

    private static string BuildVerdict(int statusCode)
        => statusCode switch
        {
            >= 200 and < 300 => "API 可达",
            400 or 405 => "API 可达，但请求格式需匹配客户端",
            401 or 403 => "API 可达，需鉴权",
            407 => "API 可达，但代理要求认证",
            408 => "API 已连接，但请求超时",
            429 => "API 可达，但被限流",
            >= 500 and < 600 => "API 可达，但服务端异常",
            _ => "已收到响应，需复核"
        };

    private static async Task<string?> ReadBodySampleAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var normalized = content.Trim();
            return normalized.Length <= 240 ? normalized : normalized[..240] + "…";
        }
        catch
        {
            return null;
        }
    }
}
