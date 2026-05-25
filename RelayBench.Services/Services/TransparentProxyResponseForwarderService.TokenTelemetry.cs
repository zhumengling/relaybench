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
    private async Task CopyDirectResponseWithTokenTelemetryAsync(
        HttpResponseMessage upstreamResponse,
        Stream outputStream,
        int statusCode,
        string contentType,
        bool streamRequested,
        TransparentProxyServerConfig config,
        string responseClientWireApi,
        string wireApi,
        CancellationToken cancellationToken)
    {
        if (statusCode >= 200 &&
            statusCode < 300 &&
            IsEventStreamContentType(contentType))
        {
            await CopySseResponseWithTokenTelemetryAsync(
                upstreamResponse,
                outputStream,
                responseClientWireApi,
                wireApi,
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
                responseClientWireApi,
                wireApi,
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
        string responseClientWireApi,
        string wireApi,
        CancellationToken cancellationToken)
    {
        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[8192];
        var charBuffer = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        StringBuilder lineBuilder = new();
        var tokenTracker = _tokenTelemetry.CreateStreamTracker();

        try
        {
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
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsResponsesStreamClient(responseClientWireApi, wireApi))
        {
            await WriteResponsesStreamErrorAsync(outputStream, 502, ex.Message, cancellationToken);
            return;
        }

        var finalCharCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuffer, 0, flush: true);
        TrackSseTokenTelemetry(charBuffer.AsSpan(0, finalCharCount), lineBuilder, tokenTracker);
        if (lineBuilder.Length > 0)
        {
            TrackSseTokenTelemetryLine(lineBuilder.ToString(), tokenTracker);
        }
    }

    private static bool IsResponsesStreamClient(string responseClientWireApi, string wireApi)
        => string.Equals(responseClientWireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal) ||
           string.Equals(wireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal);

    private static async Task WriteResponsesStreamErrorAsync(
        Stream outputStream,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        var chunk = Encoding.UTF8.GetString(TransparentProxyResponsesStreamError.BuildChunk(statusCode, message));
        var bytes = Encoding.UTF8.GetBytes($"\nevent: error\ndata: {chunk}\n\n");
        await outputStream.WriteAsync(bytes, cancellationToken);
        await outputStream.FlushAsync(cancellationToken);
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
}
