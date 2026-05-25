using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.Services;

internal sealed partial class TransparentProxyResponseForwarderService
{
    private readonly TransparentProxyResponseCacheService _responseCache;
    private readonly TransparentProxyResponseNormalizationService _responseNormalizer;
    private readonly TransparentProxySseFramer _sseFramer;
    private readonly TransparentProxyTokenTelemetryService _tokenTelemetry;
    private readonly Action _publishMetrics;

    public TransparentProxyResponseForwarderService(
        TransparentProxyResponseCacheService responseCache,
        TransparentProxyResponseNormalizationService responseNormalizer,
        TransparentProxySseFramer sseFramer,
        TransparentProxyTokenTelemetryService tokenTelemetry,
        Action publishMetrics)
    {
        _responseCache = responseCache;
        _responseNormalizer = responseNormalizer;
        _sseFramer = sseFramer;
        _tokenTelemetry = tokenTelemetry;
        _publishMetrics = publishMetrics;
    }

    public async Task CopyResponseToClientAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        int statusCode,
        string cacheKey,
        bool streamRequested,
        TransparentProxyServerConfig config,
        string wireApi,
        string responseClientWireApi,
        string responseModel,
        string logModel,
        bool normalizeToChatCompletions,
        bool preserveToolStreamEvents,
        bool preferJsonStreamExtraction,
        IReadOnlyDictionary<string, string>? toolNameAliases,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        AddCorsHeaders(context.Response);
        CopyResponseHeaders(upstreamResponse, context.Response);

        var contentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var normalizeToAnthropicMessages = statusCode >= 200 &&
                                           statusCode < 300 &&
                                           string.Equals(responseClientWireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal) &&
                                           !string.Equals(wireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal);
        if (normalizeToAnthropicMessages)
        {
            ClearTransformedResponseHeaders(context.Response);
            if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                if (streamRequested)
                {
                    await CopyNormalizedAnthropicStreamAsync(
                        context,
                        upstreamResponse,
                        responseModel,
                        wireApi,
                        cancellationToken);
                }
                else
                {
                    await CopyNormalizedAnthropicEventStreamAsJsonAsync(
                        context,
                        upstreamResponse,
                        statusCode,
                        cacheKey,
                        config,
                        responseModel,
                        logModel,
                        wireApi,
                        cancellationToken);
                }

                return;
            }

            if (streamRequested)
            {
                await CopyNormalizedAnthropicJsonAsStreamAsync(
                    context,
                    upstreamResponse,
                    responseModel,
                    wireApi,
                    cancellationToken);
                return;
            }

            await CopyNormalizedAnthropicJsonAsync(
                context,
                upstreamResponse,
                statusCode,
                cacheKey,
                config,
                responseModel,
                logModel,
                wireApi,
                cancellationToken);
            return;
        }

        var normalizeToGeminiNative = statusCode >= 200 &&
                                      statusCode < 300 &&
                                      string.Equals(responseClientWireApi, TransparentProxyNativeWireApis.Gemini, StringComparison.Ordinal) &&
                                      !string.Equals(wireApi, TransparentProxyNativeWireApis.Gemini, StringComparison.Ordinal);
        if (normalizeToGeminiNative)
        {
            ClearTransformedResponseHeaders(context.Response);
            if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                if (streamRequested)
                {
                    await CopyNormalizedGeminiStreamAsync(
                        context,
                        upstreamResponse,
                        responseModel,
                        wireApi,
                        toolNameAliases,
                        cancellationToken);
                }
                else
                {
                    await CopyNormalizedGeminiEventStreamAsJsonAsync(
                        context,
                        upstreamResponse,
                        statusCode,
                        cacheKey,
                        config,
                        responseModel,
                        logModel,
                        wireApi,
                        toolNameAliases,
                        cancellationToken);
                }

                return;
            }

            if (streamRequested)
            {
                await CopyNormalizedGeminiJsonAsStreamAsync(
                    context,
                    upstreamResponse,
                    responseModel,
                    wireApi,
                    toolNameAliases,
                    cancellationToken);
                return;
            }

            await CopyNormalizedGeminiJsonAsync(
                context,
                upstreamResponse,
                statusCode,
                cacheKey,
                config,
                responseModel,
                logModel,
                wireApi,
                toolNameAliases,
                cancellationToken);
            return;
        }

        if (normalizeToChatCompletions &&
            statusCode >= 200 &&
            statusCode < 300)
        {
            if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                ClearTransformedResponseHeaders(context.Response);
                if (streamRequested)
                {
                    await CopyNormalizedChatStreamAsync(
                        context,
                        upstreamResponse,
                        responseModel,
                        wireApi,
                        preferJsonStreamExtraction,
                        toolNameAliases,
                        cancellationToken);
                }
                else
                {
                    await CopyNormalizedChatEventStreamAsJsonAsync(
                        context,
                        upstreamResponse,
                        statusCode,
                        cacheKey,
                        config,
                        responseModel,
                        logModel,
                        wireApi,
                        toolNameAliases,
                        cancellationToken);
                }

                return;
            }

            if (streamRequested)
            {
                ClearTransformedResponseHeaders(context.Response);
                await CopyNormalizedChatJsonAsStreamAsync(
                    context,
                    upstreamResponse,
                    responseModel,
                    wireApi,
                    toolNameAliases,
                    cancellationToken);
                return;
            }

            ClearTransformedResponseHeaders(context.Response);
            await CopyNormalizedChatJsonAsync(
                context,
                upstreamResponse,
                statusCode,
                cacheKey,
                config,
                responseModel,
                logModel,
                wireApi,
                toolNameAliases,
                cancellationToken);
            return;
        }

        context.Response.ContentType = contentType;

        var canCache = config.EnableCache &&
                       !streamRequested &&
                       !string.IsNullOrWhiteSpace(cacheKey) &&
                       statusCode >= 200 &&
                       statusCode < 300 &&
                       !contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (canCache)
        {
            var bytes = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            TrackResponseBodyTokens(bytes);
            if (bytes.Length <= config.CacheMaxBytes)
            {
                _responseCache.StoreResponse(
                    cacheKey,
                    statusCode,
                    contentType,
                    bytes,
                    NormalizeLogModel(logModel),
                    config.CacheMaxBytes);
            }
        }
        else
        {
            await CopyDirectResponseWithTokenTelemetryAsync(
                upstreamResponse,
                context.Response.OutputStream,
                statusCode,
                contentType,
                streamRequested,
                config,
                responseClientWireApi,
                wireApi,
                cancellationToken);
        }

        context.Response.OutputStream.Close();
    }

    public void TrackResponseBodyTokens(byte[] body, bool includePromptCache = true)
    {
        if (_tokenTelemetry.TrackResponseBody(body, includePromptCache))
        {
            _publishMetrics();
        }
    }

    public void TrackRequestBodyInputTokens(byte[] body)
    {
        if (_tokenTelemetry.TrackRequestBodyInputTokens(body))
        {
            _publishMetrics();
        }
    }

    public void TrackLocalCacheTokens(byte[] requestBody)
    {
        if (_tokenTelemetry.TrackLocalCacheTokens(requestBody))
        {
            _publishMetrics();
        }
    }


}
