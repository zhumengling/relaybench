using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyResponseForwarderService
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailers",
        "transfer-encoding",
        "upgrade",
        "host",
        "content-length"
    };

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
        string responseModel,
        string logModel,
        bool normalizeToChatCompletions,
        bool preserveToolStreamEvents,
        bool preferJsonStreamExtraction,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        AddCorsHeaders(context.Response);
        CopyResponseHeaders(upstreamResponse, context.Response);

        var contentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        if (normalizeToChatCompletions &&
            statusCode >= 200 &&
            statusCode < 300)
        {
            if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                ClearTransformedResponseHeaders(context.Response);
                if (preserveToolStreamEvents)
                {
                    context.Response.ContentType = contentType;
                    await CopyDirectResponseWithTokenTelemetryAsync(
                        upstreamResponse,
                        context.Response.OutputStream,
                        statusCode,
                        contentType,
                        streamRequested: true,
                        config,
                        cancellationToken);
                    context.Response.OutputStream.Close();
                    return;
                }

                await CopyNormalizedChatStreamAsync(
                    context,
                    upstreamResponse,
                    responseModel,
                    wireApi,
                    preferJsonStreamExtraction,
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

    private async Task CopyDirectResponseWithTokenTelemetryAsync(
        HttpResponseMessage upstreamResponse,
        Stream outputStream,
        int statusCode,
        string contentType,
        bool streamRequested,
        TransparentProxyServerConfig config,
        CancellationToken cancellationToken)
    {
        if (statusCode >= 200 &&
            statusCode < 300 &&
            IsEventStreamContentType(contentType))
        {
            await CopySseResponseWithTokenTelemetryAsync(
                upstreamResponse,
                outputStream,
                cancellationToken);
            return;
        }

        if (ShouldCaptureResponseBodyForTokenTelemetry(statusCode, contentType))
        {
            await CopyResponseBodyAndCaptureTokenTelemetryAsync(
                upstreamResponse,
                outputStream,
                Math.Max(256 * 1024, config.CacheMaxBytes),
                cancellationToken);
            return;
        }

        if (statusCode >= 200 && statusCode < 300 && streamRequested)
        {
            await CopySseResponseWithTokenTelemetryAsync(
                upstreamResponse,
                outputStream,
                cancellationToken);
            return;
        }

        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(outputStream, cancellationToken);
    }

    private async Task CopyResponseBodyAndCaptureTokenTelemetryAsync(
        HttpResponseMessage upstreamResponse,
        Stream outputStream,
        int maxTelemetryBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream telemetryBody = new();
        var canCaptureTelemetry = true;
        var buffer = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await outputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (!canCaptureTelemetry)
            {
                continue;
            }

            if (telemetryBody.Length + read > maxTelemetryBytes)
            {
                canCaptureTelemetry = false;
                telemetryBody.SetLength(0);
                continue;
            }

            telemetryBody.Write(buffer, 0, read);
        }

        if (canCaptureTelemetry && telemetryBody.Length > 0)
        {
            TrackResponseBodyTokens(telemetryBody.ToArray());
        }
    }

    private async Task CopySseResponseWithTokenTelemetryAsync(
        HttpResponseMessage upstreamResponse,
        Stream outputStream,
        CancellationToken cancellationToken)
    {
        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[8192];
        var charBuffer = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        StringBuilder lineBuilder = new();
        var tokenTracker = _tokenTelemetry.CreateStreamTracker();

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await outputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await outputStream.FlushAsync(cancellationToken);

            var charCount = decoder.GetChars(buffer, 0, read, charBuffer, 0, flush: false);
            TrackSseTokenTelemetry(charBuffer.AsSpan(0, charCount), lineBuilder, tokenTracker);
        }

        var finalCharCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuffer, 0, flush: true);
        TrackSseTokenTelemetry(charBuffer.AsSpan(0, finalCharCount), lineBuilder, tokenTracker);
        if (lineBuilder.Length > 0)
        {
            TrackSseTokenTelemetryLine(lineBuilder.ToString(), tokenTracker);
        }
    }

    private void TrackSseTokenTelemetry(
        ReadOnlySpan<char> text,
        StringBuilder lineBuilder,
        TransparentProxyTokenStreamTracker tokenTracker)
    {
        foreach (var character in text)
        {
            if (character == '\n')
            {
                TrackSseTokenTelemetryLine(lineBuilder.ToString(), tokenTracker);
                lineBuilder.Clear();
                continue;
            }

            if (character != '\r')
            {
                lineBuilder.Append(character);
            }
        }
    }

    private void TrackSseTokenTelemetryLine(string line, TransparentProxyTokenStreamTracker tokenTracker)
    {
        if (!ChatSseParser.TryReadDataLine(line, out var data))
        {
            return;
        }

        if (tokenTracker.TrackSseData(data))
        {
            _publishMetrics();
        }
    }

    private async Task CopyNormalizedChatJsonAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        int statusCode,
        string cacheKey,
        TransparentProxyServerConfig config,
        string responseModel,
        string logModel,
        string wireApi,
        CancellationToken cancellationToken)
    {
        var upstreamBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var upstreamText = Encoding.UTF8.GetString(upstreamBytes);
        TrackPromptCacheTokens(upstreamText);
        var normalized = _responseNormalizer.TryBuildNormalizedChatJson(
            upstreamBytes,
            responseModel,
            wireApi);
        if (normalized is null)
        {
            context.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
            context.Response.ContentLength64 = upstreamBytes.LongLength;
            await context.Response.OutputStream.WriteAsync(upstreamBytes, cancellationToken);
            TrackResponseBodyTokens(upstreamBytes);
            context.Response.OutputStream.Close();
            return;
        }

        var normalizedBytes = normalized.Body;
        const string normalizedContentType = "application/json; charset=utf-8";
        context.Response.ContentType = normalizedContentType;
        context.Response.ContentLength64 = normalizedBytes.LongLength;
        await context.Response.OutputStream.WriteAsync(normalizedBytes, cancellationToken);
        TrackResponseBodyTokens(normalizedBytes, includePromptCache: false);
        if (config.EnableCache &&
            !string.IsNullOrWhiteSpace(cacheKey) &&
            statusCode >= 200 &&
            statusCode < 300 &&
            normalizedBytes.Length <= config.CacheMaxBytes)
        {
            _responseCache.StoreResponse(
                cacheKey,
                statusCode,
                normalizedContentType,
                normalizedBytes,
                NormalizeLogModel(logModel),
                config.CacheMaxBytes);
        }

        context.Response.OutputStream.Close();
    }

    private async Task CopyNormalizedChatStreamAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        string responseModel,
        string wireApi,
        bool preferJsonStreamExtraction,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;
        var model = string.IsNullOrWhiteSpace(responseModel) ? "relaybench-proxy" : responseModel.Trim();
        var streamId = $"chatcmpl-relaybench-{Guid.NewGuid():N}";
        var wroteDone = false;
        var wroteTerminalChunk = false;
        StringBuilder assistantText = new();

        async Task WriteBufferedJsonChunkIfNeededAsync()
        {
            if (!preferJsonStreamExtraction || assistantText.Length == 0)
            {
                return;
            }

            var original = assistantText.ToString();
            var extracted = TryExtractFirstJsonObject(original);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return;
            }

            assistantText.Clear();
            assistantText.Append(extracted);
            var chunk = _responseNormalizer.BuildOpenAiChatCompletionChunk(extracted, model, wireApi, streamId);
            await WriteSseDataAsync(context.Response.OutputStream, chunk, cancellationToken);
        }

        async Task WriteTerminalChunkAsync()
        {
            if (wroteTerminalChunk)
            {
                return;
            }

            await WriteBufferedJsonChunkIfNeededAsync();
            var terminalChunk = _responseNormalizer.BuildOpenAiChatCompletionTerminalChunk(
                model,
                wireApi,
                streamId,
                assistantText.ToString());
            await WriteSseDataAsync(context.Response.OutputStream, terminalChunk, cancellationToken);
            wroteTerminalChunk = true;
        }

        await foreach (var sseEvent in _sseFramer.ReadEventsAsync(upstreamResponse.Content, cancellationToken))
        {
            var data = sseEvent.Data;
            if (ChatSseParser.IsDone(data))
            {
                await WriteTerminalChunkAsync();
                await WriteSseDataAsync(context.Response.OutputStream, "[DONE]", cancellationToken);
                wroteDone = true;
                break;
            }

            var delta = ChatSseParser.TryExtractDelta(data);
            if (string.IsNullOrEmpty(delta))
            {
                TrackPromptCacheTokens(data);
                continue;
            }

            var chunk = _responseNormalizer.BuildOpenAiChatCompletionChunk(delta, model, wireApi, streamId);
            assistantText.Append(delta);
            if (!preferJsonStreamExtraction)
            {
                await WriteSseDataAsync(context.Response.OutputStream, chunk, cancellationToken);
            }

            TrackOutputTextTokens(delta);
            TrackPromptCacheTokens(data);
        }

        await WriteTerminalChunkAsync();
        if (!wroteDone)
        {
            await WriteSseDataAsync(context.Response.OutputStream, "[DONE]", cancellationToken);
        }

        context.Response.OutputStream.Close();
    }

    private void TrackOutputTextTokens(string? text)
    {
        if (_tokenTelemetry.TrackOutputText(text))
        {
            _publishMetrics();
        }
    }

    private void TrackPromptCacheTokens(string? json)
    {
        if (_tokenTelemetry.TrackPromptCache(json))
        {
            _publishMetrics();
        }
    }

    private static bool IsEventStreamContentType(string contentType)
        => contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldCaptureResponseBodyForTokenTelemetry(int statusCode, string contentType)
    {
        if (statusCode < 200 || statusCode >= 300)
        {
            return false;
        }

        return contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
                continue;
            }

            if (character != '}')
            {
                continue;
            }

            depth--;
            if (depth != 0)
            {
                continue;
            }

            var candidate = text[start..(index + 1)].Trim();
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static async Task WriteSseDataAsync(Stream outputStream, string data, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await outputStream.WriteAsync(bytes, cancellationToken);
        await outputStream.FlushAsync(cancellationToken);
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstreamResponse, HttpListenerResponse response)
    {
        foreach (var header in upstreamResponse.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                TrySetResponseHeader(response, header.Key, header.Value);
            }
        }

        foreach (var header in upstreamResponse.Content.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key) &&
                !string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                TrySetResponseHeader(response, header.Key, header.Value);
            }
        }
    }

    private static void ClearTransformedResponseHeaders(HttpListenerResponse response)
    {
        foreach (var headerName in new[] { "Content-Encoding", "Content-MD5", "Content-Range" })
        {
            try
            {
                response.Headers.Remove(headerName);
            }
            catch
            {
            }
        }
    }

    private static void TrySetResponseHeader(HttpListenerResponse response, string name, IEnumerable<string> values)
    {
        try
        {
            response.Headers[name] = string.Join(",", values);
        }
        catch
        {
            // Some framework-managed headers cannot be set directly; keep proxying.
        }
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Headers"] = "authorization, content-type, x-api-key, anthropic-version, anthropic-beta, openai-beta, idempotency-key, session_id";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
        response.Headers["X-RelayBench-Proxy"] = "transparent";
    }

    private static string NormalizeLogModel(string? modelName)
        => string.IsNullOrWhiteSpace(modelName) ? "-" : modelName.Trim();
}
