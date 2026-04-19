using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class CloudflareSpeedTestService
{
    private static async Task<LatencySeriesResult> MeasureLatencySeriesAsync(int probeCount, CancellationToken cancellationToken)
    {
        List<double> points = new(probeCount);
        EdgeMetadata? metadata = null;

        for (var index = 0; index < probeCount; index++)
        {
            var measurement = await MeasureLatencyProbeAsync(cancellationToken);
            metadata ??= measurement.Metadata;
            points.Add(measurement.LatencyMilliseconds);
        }

        return new LatencySeriesResult(
            points,
            Percentile(points, LatencyPercentile),
            CalculateJitter(points),
            metadata);
    }

    private static async Task<LatencyProbeResult> MeasureLatencyProbeAsync(CancellationToken cancellationToken)
    {
        var url = $"{DownloadEndpoint}?bytes=0&seed={Guid.NewGuid():N}";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        var stopwatch = Stopwatch.StartNew();
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        stopwatch.Stop();
        response.EnsureSuccessStatusCode();

        var latency = Math.Max(0.01, stopwatch.Elapsed.TotalMilliseconds - EstimatedServerTimeMilliseconds);
        var metadata = EdgeMetadata.FromResponse(response);
        return new LatencyProbeResult(latency, metadata);
    }

    private static async Task<BandwidthSeriesResult> MeasureBandwidthSeriesAsync(
        bool isDownload,
        IReadOnlyList<long> plan,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        List<SpeedTransferMeasurement> measurements = new(plan.Count);
        List<double> loadedLatencies = [];
        EdgeMetadata? metadata = null;

        for (var index = 0; index < plan.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = plan[index];
            progress?.Report(
                $"{(isDownload ? "下载" : "上传")}第 {index + 1}/{plan.Count} 轮 " +
                $"（{FormatBytes(bytes)}）...");

            TransferMeasurementResult measurement;
            try
            {
                measurement = await MeasureTransferAsync(isDownload, bytes, cancellationToken);
            }
            catch (HttpRequestException ex) when (measurements.Count > 0 && IsSoftTransferLimitStatus(ex.StatusCode))
            {
                var statusLabel = ex.StatusCode is null ? "未知" : ((int)ex.StatusCode).ToString();
                progress?.Report(
                    $"{(isDownload ? "下载" : "上传")} {FormatBytes(bytes)} 被服务端拒绝（HTTP {statusLabel}），" +
                    $"已保留前 {measurements.Count} 轮结果并停止更大档位。");
                break;
            }

            metadata ??= measurement.Metadata;
            measurements.Add(new SpeedTransferMeasurement(
                measurements.Count + 1,
                bytes,
                measurement.DurationMilliseconds,
                measurement.BitsPerSecond,
                measurement.LoadedLatencyPoints));

            if (measurement.DurationMilliseconds >= LoadedRequestMinDurationMilliseconds)
            {
                loadedLatencies.AddRange(measurement.LoadedLatencyPoints);
            }

            if (measurement.DurationMilliseconds >= FinishRequestDurationMilliseconds)
            {
                break;
            }
        }

        var reducedBandwidth = Percentile(
            measurements
                .Where(item => item.DurationMilliseconds >= BandwidthMinRequestDurationMilliseconds)
                .Select(item => item.BitsPerSecond)
                .ToList(),
            BandwidthPercentile);

        return new BandwidthSeriesResult(
            measurements,
            reducedBandwidth,
            loadedLatencies,
            Percentile(loadedLatencies, LatencyPercentile),
            CalculateJitter(loadedLatencies),
            metadata);
    }

    private static async Task<TransferMeasurementResult> MeasureTransferAsync(
        bool isDownload,
        long bytes,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource loadedLatencyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loadedLatencyTask = CaptureLoadedLatenciesAsync(loadedLatencyCts.Token);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await SendTransferRequestAsync(isDownload, bytes, cancellationToken);
            var metadata = EdgeMetadata.FromResponse(response);

            if (isDownload)
            {
                await DrainContentAsync(response.Content, cancellationToken);
            }
            else
            {
                await response.Content.LoadIntoBufferAsync();
            }

            stopwatch.Stop();

            var loadedLatencies = await CompleteLoadedLatencyCaptureAsync(loadedLatencyCts, loadedLatencyTask);

            var bitsPerSecond = stopwatch.Elapsed.TotalMilliseconds <= 0
                ? 0
                : (bytes * 8d * (1d + EstimatedHeaderFraction)) / stopwatch.Elapsed.TotalSeconds;

            return new TransferMeasurementResult(
                stopwatch.Elapsed.TotalMilliseconds,
                bitsPerSecond,
                loadedLatencies,
                metadata);
        }
        catch
        {
            await CompleteLoadedLatencyCaptureAsync(loadedLatencyCts, loadedLatencyTask);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> SendTransferRequestAsync(
        bool isDownload,
        long bytes,
        CancellationToken cancellationToken)
    {
        if (isDownload)
        {
            var url = $"{DownloadEndpoint}?bytes={bytes}&seed={Guid.NewGuid():N}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            return response;
        }

        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"{UploadEndpoint}?seed={Guid.NewGuid():N}")
        {
            Content = new GeneratedBytesContent(bytes)
        };
        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var uploadResponse = await HttpClient.SendAsync(uploadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        uploadResponse.EnsureSuccessStatusCode();
        return uploadResponse;
    }

    private static async Task<IReadOnlyList<double>> CaptureLoadedLatenciesAsync(CancellationToken cancellationToken)
    {
        List<double> values = [];
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(LoadedLatencyThrottleMilliseconds));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var measurement = await MeasureLatencyProbeAsync(cancellationToken);
                values.Add(measurement.LatencyMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the parent transfer finishes.
        }

        return values;
    }

    private static async Task<IReadOnlyList<double>> CompleteLoadedLatencyCaptureAsync(
        CancellationTokenSource loadedLatencyCts,
        Task<IReadOnlyList<double>> loadedLatencyTask)
    {
        loadedLatencyCts.Cancel();
        try
        {
            return await loadedLatencyTask;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<double>();
        }
    }

    private static bool IsSoftTransferLimitStatus(HttpStatusCode? statusCode)
        => statusCode is HttpStatusCode.Forbidden
            or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.TooManyRequests;

    private static async Task DrainContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
        }
    }

    private static async Task<(int Sent, int Received)> MeasurePacketLossAsync(int probeCount)
    {
        var sent = 0;
        var received = 0;
        using Ping ping = new();

        for (var index = 0; index < probeCount; index++)
        {
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(PacketLossHost, 1_000);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                }
            }
            catch
            {
                // Count exceptions as missed packets in this fallback estimator.
            }
        }

        return (sent, received);
    }

}
