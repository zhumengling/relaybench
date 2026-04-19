using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class CloudflareSpeedTestService
{
    private const string EndpointBaseUrl = "https://speed.cloudflare.com";
    private const string DownloadEndpoint = "https://speed.cloudflare.com/__down";
    private const string UploadEndpoint = "https://speed.cloudflare.com/__up";
    private const string PacketLossHost = "speed.cloudflare.com";
    private const double EstimatedServerTimeMilliseconds = 10;
    private const double EstimatedHeaderFraction = 0.005;
    private const int LoadedLatencyThrottleMilliseconds = 400;
    private const int FinishRequestDurationMilliseconds = 1_000;
    private const int LoadedRequestMinDurationMilliseconds = 250;
    private const int BandwidthMinRequestDurationMilliseconds = 10;
    private const double LatencyPercentile = 0.5;
    private const double BandwidthPercentile = 0.9;

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly IReadOnlyList<SpeedTestProfileConfig> Profiles =
    [
        new SpeedTestProfileConfig(
            "quick",
            "快速",
            "较短的一轮测速，用于快速了解延迟和带宽。",
            8,
            10,
            [100_000, 100_000, 100_000, 1_000_000, 1_000_000, 10_000_000],
            [100_000, 100_000, 1_000_000, 1_000_000, 10_000_000]),
        new SpeedTestProfileConfig(
            "balanced",
            "均衡",
            "推荐。采用 Cloudflare 风格的分档大小，同时控制桌面端整体耗时。",
            20,
            20,
            [
                100_000, 100_000, 100_000, 100_000, 100_000,
                100_000, 100_000, 100_000, 100_000,
                1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000,
                10_000_000, 10_000_000, 10_000_000, 10_000_000,
                25_000_000, 25_000_000,
                50_000_000
            ],
            [
                100_000, 100_000, 100_000, 100_000, 100_000, 100_000, 100_000, 100_000,
                1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000,
                10_000_000, 10_000_000, 10_000_000,
                25_000_000, 25_000_000
            ]),
        new SpeedTestProfileConfig(
            "extended",
            "扩展",
            "最接近 Cloudflare 公共默认序列，耗时也更长。",
            20,
            30,
            [
                100_000, 100_000, 100_000, 100_000, 100_000, 100_000, 100_000, 100_000, 100_000,
                1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000,
                10_000_000, 10_000_000, 10_000_000, 10_000_000, 10_000_000, 10_000_000,
                25_000_000, 25_000_000, 25_000_000, 25_000_000,
                50_000_000, 50_000_000, 50_000_000,
                50_000_000, 50_000_000
            ],
            [
                100_000, 100_000, 100_000, 100_000, 100_000, 100_000, 100_000, 100_000,
                1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000,
                10_000_000, 10_000_000, 10_000_000, 10_000_000,
                25_000_000, 25_000_000, 25_000_000, 25_000_000,
                50_000_000, 50_000_000, 50_000_000
            ])
    ];

    public IReadOnlyList<SpeedTestProfile> GetProfiles()
        => Profiles.Select(profile => profile.ToPublicModel()).ToArray();

    public SpeedTestProfile GetDefaultProfile()
        => Profiles[1].ToPublicModel();

    public async Task<SpeedTestResult> RunAsync(
        string profileKey,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profile = Profiles.FirstOrDefault(item => string.Equals(item.Key, profileKey, StringComparison.OrdinalIgnoreCase))
            ?? Profiles[1];

        try
        {
            progress?.Report($"正在测量 {EndpointBaseUrl} 的空闲延迟...");
            var idleLatency = await MeasureLatencySeriesAsync(profile.IdleLatencyProbeCount, cancellationToken);

            progress?.Report("正在测量下载带宽与负载延迟...");
            var downloadSeries = await MeasureBandwidthSeriesAsync(
                isDownload: true,
                profile.DownloadBytesPlan,
                progress,
                cancellationToken);

            progress?.Report("正在测量上传带宽与负载延迟...");
            var uploadSeries = await MeasureBandwidthSeriesAsync(
                isDownload: false,
                profile.UploadBytesPlan,
                progress,
                cancellationToken);

            progress?.Report("正在通过 ICMP 探针估算丢包率...");
            var packetLoss = await MeasurePacketLossAsync(profile.PacketLossProbeCount);

            var edgeMetadata = idleLatency.Metadata ?? downloadSeries.Metadata ?? uploadSeries.Metadata;
            double? packetLossRatio = packetLoss.Sent == 0
                ? null
                : (packetLoss.Sent - packetLoss.Received) / (double)packetLoss.Sent;
            var gptImpactScore = ComputeGptImpactScore(
                idleLatency.ReducedLatency,
                idleLatency.Jitter,
                packetLossRatio,
                downloadSeries.ReducedBandwidth,
                uploadSeries.ReducedBandwidth,
                ComputeLoadedLatencyIncrease(idleLatency.ReducedLatency, downloadSeries.ReducedLoadedLatency, uploadSeries.ReducedLoadedLatency));
            var gptImpactLabel = LabelGptImpact(gptImpactScore);

            var summary =
                $"{profile.DisplayName}方案：空闲延迟 {FormatMs(idleLatency.ReducedLatency)}，抖动 {FormatMs(idleLatency.Jitter)}，" +
                $"下载 {FormatBandwidth(downloadSeries.ReducedBandwidth)}，上传 {FormatBandwidth(uploadSeries.ReducedBandwidth)}，" +
                $"丢包 {FormatRatio(packetLossRatio)}，GPT 影响评分 {gptImpactScore}/100（{gptImpactLabel}）。";

            return new SpeedTestResult(
                DateTimeOffset.Now,
                profile.Key,
                profile.DisplayName,
                EndpointBaseUrl,
                edgeMetadata?.Colo,
                edgeMetadata?.City,
                edgeMetadata?.Country,
                edgeMetadata?.Ip,
                idleLatency.Points,
                idleLatency.ReducedLatency,
                idleLatency.Jitter,
                downloadSeries.Measurements,
                downloadSeries.ReducedBandwidth,
                downloadSeries.LoadedLatencyPoints,
                downloadSeries.ReducedLoadedLatency,
                downloadSeries.LoadedJitter,
                uploadSeries.Measurements,
                uploadSeries.ReducedBandwidth,
                uploadSeries.LoadedLatencyPoints,
                uploadSeries.ReducedLoadedLatency,
                uploadSeries.LoadedJitter,
                packetLoss.Sent,
                packetLoss.Received,
                packetLossRatio,
                gptImpactScore,
                gptImpactLabel,
                summary,
                null);
        }
        catch (Exception ex)
        {
            return new SpeedTestResult(
                DateTimeOffset.Now,
                profile.Key,
                profile.DisplayName,
                EndpointBaseUrl,
                null,
                null,
                null,
                null,
                Array.Empty<double>(),
                null,
                null,
                Array.Empty<SpeedTransferMeasurement>(),
                null,
                Array.Empty<double>(),
                null,
                null,
                Array.Empty<SpeedTransferMeasurement>(),
                null,
                Array.Empty<double>(),
                null,
                null,
                0,
                0,
                null,
                0,
                "失败",
                $"测速失败：{ex.Message}",
                ex.Message);
        }
    }

}
