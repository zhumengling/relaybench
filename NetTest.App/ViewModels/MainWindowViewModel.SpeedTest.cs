using System.Collections.ObjectModel;
using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _selectedSpeedTestProfileKey = "balanced";
    private string _speedTestSummary = "运行测速后，这里会显示延迟、抖动、带宽和 GPT 影响评分。";
    private string _speedTestLatencyDetail = "尚无延迟采样。";
    private string _speedTestTransferDetail = "尚无传输采样。";
    private string _speedTestPacketLossDetail = "尚无丢包估算。";

    public ObservableCollection<SpeedTestProfile> SpeedTestProfiles { get; }

    public string SelectedSpeedTestProfileKey
    {
        get => _selectedSpeedTestProfileKey;
        set
        {
            if (SetProperty(ref _selectedSpeedTestProfileKey, ResolveSpeedTestProfileKey(value)))
            {
                OnPropertyChanged(nameof(SelectedSpeedTestProfileDescription));
            }
        }
    }

    public string SelectedSpeedTestProfileDescription
    {
        get
        {
            var profile = GetSelectedSpeedTestProfile();
            return profile is null
                ? "请选择测速方案。"
                : profile.Description;
        }
    }

    public string SpeedTestSummary
    {
        get => _speedTestSummary;
        private set => SetProperty(ref _speedTestSummary, value);
    }

    public string SpeedTestLatencyDetail
    {
        get => _speedTestLatencyDetail;
        private set => SetProperty(ref _speedTestLatencyDetail, value);
    }

    public string SpeedTestTransferDetail
    {
        get => _speedTestTransferDetail;
        private set => SetProperty(ref _speedTestTransferDetail, value);
    }

    public string SpeedTestPacketLossDetail
    {
        get => _speedTestPacketLossDetail;
        private set => SetProperty(ref _speedTestPacketLossDetail, value);
    }

    private Task RunSpeedTestAsync()
        => ExecuteBusyActionAsync("正在运行 Cloudflare 风格测速...", RunSpeedTestCoreAsync);

    private async Task RunSpeedTestCoreAsync()
    {
        UpdateGlobalTaskProgress("\u51C6\u5907\u4E2D", 10d);
        var progress = new Progress<string>(message =>
        {
            StatusMessage = message;
            UpdateGlobalTaskProgressForSpeedTestMessage(message);
        });
        var result = await _cloudflareSpeedTestService.RunAsync(SelectedSpeedTestProfileKey, progress);
        UpdateGlobalTaskProgress("\u6C47\u603B\u4E2D", 94d);
        ApplySpeedTestResult(result);
        DashboardCards[4].Status = result.Error is null ? result.GptImpactLabel : "需复核";
        DashboardCards[4].Detail = result.Summary;
        StatusMessage = result.Summary;
        AppendHistory("测速", "Cloudflare 风格测速", SpeedTestSummary);
    }

    private void LoadSpeedState(AppStateSnapshot snapshot)
    {
        SelectedSpeedTestProfileKey = ResolveSpeedTestProfileKey(snapshot.SpeedTestProfileKey);
    }

    private void ApplySpeedStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.SpeedTestProfileKey = SelectedSpeedTestProfileKey;
    }

    private void ApplySpeedTestResult(SpeedTestResult result)
    {
        SpeedTestSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"方案：{result.ProfileName}\n" +
            $"端点：{result.EndpointBaseUrl}\n" +
            $"Cloudflare 节点：{result.EdgeColo ?? "--"} / {result.EdgeCity ?? "--"} / {result.EdgeCountry ?? "--"}\n" +
            $"节点 IP：{result.EdgeIp ?? "--"}\n" +
            $"空闲延迟：{FormatNullableMilliseconds(result.IdleLatencyMilliseconds)}\n" +
            $"空闲抖动：{FormatNullableMilliseconds(result.IdleJitterMilliseconds)}\n" +
            $"下载速度：{FormatNullableBandwidth(result.DownloadBitsPerSecond)}\n" +
            $"下载负载延迟：{FormatNullableMilliseconds(result.DownloadLoadedLatencyMilliseconds)}\n" +
            $"上传速度：{FormatNullableBandwidth(result.UploadBitsPerSecond)}\n" +
            $"上传负载延迟：{FormatNullableMilliseconds(result.UploadLoadedLatencyMilliseconds)}\n" +
            $"丢包估算：{FormatPacketLoss(result.PacketLossRatio)}（{result.PacketLossReceived}/{result.PacketLossSent} 次回复）\n" +
            $"GPT 影响评分：{result.GptImpactScore}/100（{result.GptImpactLabel}）\n" +
            $"摘要：{result.Summary}\n" +
            $"错误：{result.Error ?? "无"}";

        SpeedTestLatencyDetail =
            $"空闲延迟采样：\n{FormatSampleList(result.IdleLatencyPointsMilliseconds)}\n\n" +
            $"下载负载延迟采样：\n{FormatSampleList(result.DownloadLoadedLatencyPointsMilliseconds)}\n\n" +
            $"上传负载延迟采样：\n{FormatSampleList(result.UploadLoadedLatencyPointsMilliseconds)}";

        StringBuilder transferBuilder = new();
        transferBuilder.AppendLine("下载测量：");
        AppendTransferMeasurements(transferBuilder, result.DownloadMeasurements);
        transferBuilder.AppendLine();
        transferBuilder.AppendLine("上传测量：");
        AppendTransferMeasurements(transferBuilder, result.UploadMeasurements);
        SpeedTestTransferDetail = transferBuilder.ToString().TrimEnd();

        SpeedTestPacketLossDetail =
            $"针对 speed.cloudflare.com 的 ICMP 丢包估算\n" +
            $"发送：{result.PacketLossSent}\n" +
            $"收到：{result.PacketLossReceived}\n" +
            $"丢包：{FormatPacketLoss(result.PacketLossRatio)}\n\n" +
            "说明：由于 Cloudflare 公共 TURN 丢包路径已弃用，当前桌面版改用 ICMP 回退估算。";

        AppendModuleOutput("测速返回", SpeedTestSummary, SpeedTestLatencyDetail, SpeedTestPacketLossDetail);
        SaveState();
    }

    private SpeedTestProfile? GetSelectedSpeedTestProfile()
        => SpeedTestProfiles.FirstOrDefault(profile => string.Equals(profile.Key, SelectedSpeedTestProfileKey, StringComparison.OrdinalIgnoreCase));

    private string ResolveSpeedTestProfileKey(string? requestedKey)
    {
        var matchedProfile = SpeedTestProfiles.FirstOrDefault(profile => string.Equals(profile.Key, requestedKey, StringComparison.OrdinalIgnoreCase));
        return matchedProfile?.Key ?? _cloudflareSpeedTestService.GetDefaultProfile().Key;
    }

    private static void AppendTransferMeasurements(StringBuilder builder, IReadOnlyList<SpeedTransferMeasurement> measurements)
    {
        if (measurements.Count == 0)
        {
            builder.AppendLine("（无）");
            return;
        }

        foreach (var measurement in measurements)
        {
            builder.AppendLine(
                $"#{measurement.Sequence,2}  大小={FormatBytes(measurement.Bytes),8}  " +
                $"耗时={measurement.DurationMilliseconds,7:F1} ms  " +
                $"速度={FormatNullableBandwidth(measurement.BitsPerSecond),10}  " +
                $"负载延迟采样数={measurement.LoadedLatencyPointsMilliseconds.Count}");
        }
    }

    private static string FormatSampleList(IReadOnlyList<double> values)
        => values.Count == 0
            ? "（无）"
            : string.Join(", ", values.Select(value => $"{value:F1} ms"));

    private static string FormatNullableMilliseconds(double? value)
        => value is null ? "--" : $"{value.Value:F1} ms";

    private static string FormatNullableBandwidth(double? bitsPerSecond)
    {
        if (bitsPerSecond is null)
        {
            return "--";
        }

        return bitsPerSecond.Value switch
        {
            >= 1_000_000_000 => $"{bitsPerSecond.Value / 1_000_000_000d:F2} Gbps",
            >= 1_000_000 => $"{bitsPerSecond.Value / 1_000_000d:F1} Mbps",
            >= 1_000 => $"{bitsPerSecond.Value / 1_000d:F1} Kbps",
            _ => $"{bitsPerSecond.Value:F0} bps"
        };
    }

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            >= 1_000_000_000 => $"{bytes / 1_000_000_000d:F2} GB",
            >= 1_000_000 => $"{bytes / 1_000_000d:F1} MB",
            >= 1_000 => $"{bytes / 1_000d:F1} KB",
            _ => $"{bytes} B"
        };

    private static string FormatPacketLoss(double? ratio)
        => ratio is null ? "--" : $"{ratio.Value * 100:F1}%";
}
