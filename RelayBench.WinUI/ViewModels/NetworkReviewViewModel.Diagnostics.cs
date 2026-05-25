using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;
using RelayBenchPaths = RelayBench.Services.Infrastructure.RelayBenchPaths;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class NetworkReviewViewModel : ObservableObject
{
    [RelayCommand]
    private async Task RunNetworkDiagnosticsAsync()
    {
        IsBusy = true;
        StatusText = "Running network diagnostics...";
        GlobalProgressService.Report(10, "Network diagnostics");
        try
        {
            var result = await _networkService.RunAsync();
            PublicIp = result.PublicIp ?? "--";
            HostName = result.HostName;
            CloudflareColo = result.CloudflareColo ?? "--";
            AdapterCount = result.Adapters.Count;
            ApplyPrimaryAdapter(result.Adapters.FirstOrDefault());
            await ApplyDnsDiagnosticsAsync(result.Adapters.FirstOrDefault());
            TouchSnapshot(result.CapturedAt);
            StatusText = "Network diagnostics completed";
            await RecordNetworkHistoryAsync("Network diagnostics", PublicIp, StatusText, null, result);
            GlobalProgressService.Complete();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed: {ex.Message}";
            GlobalProgressService.Complete();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunNetworkAsync()
        => await RunOverviewSnapshotAsync();

    private async Task RunOverviewSnapshotAsync()
    {
        StatusText = "正在刷新网络复核总览...";
        GlobalProgressService.Report(4, "网络复核总览");
        await RunNetworkDiagnosticsAsync();
        await RunApiTraceAsync();
        await RunSplitRoutingAsync();
        await RunSpeedTestAsync();
        await RunRouteTraceAsync();
        await RunStunProbeAsync();
        await RunIpRiskReviewAsync();
        StatusText = "网络复核总览刷新完成";
        TouchSnapshot();
        GlobalProgressService.Complete();
    }

    [RelayCommand]
    private async Task RunSpeedTestAsync()
    {
        IsBusy = true;
        StatusText = "Running speed test...";
        try
        {
            var profiles = _speedTestService.GetProfiles();
            var profile = profiles.FirstOrDefault();
            if (profile is null)
            {
                StatusText = "No speed test profile available";
                return;
            }
            var result = await _speedTestService.RunAsync(profile.Key);
            DownloadSpeed = result.DownloadBitsPerSecond.HasValue ? $"{result.DownloadBitsPerSecond.Value / 1_000_000:F1} Mbps" : "--";
            UploadSpeed = result.UploadBitsPerSecond.HasValue ? $"{result.UploadBitsPerSecond.Value / 1_000_000:F1} Mbps" : "--";
            Jitter = result.IdleJitterMilliseconds.HasValue ? $"{result.IdleJitterMilliseconds.Value:F2} ms" : "--";
            SpeedTestLocation = BuildSpeedLocation(result);
            SpeedPeakDownload = DownloadSpeed;
            SpeedPeakUpload = UploadSpeed;
            SpeedJitterMin = Jitter;
            SpeedJitterMax = Jitter;
            TouchSnapshot(result.CheckedAt);
            StatusText = "Speed test completed";
            await RecordNetworkHistoryAsync("Speed test", "Cloudflare", $"{DownloadSpeed} down / {UploadSpeed} up", result.GptImpactScore > 0 ? result.GptImpactScore : null, result);
        }
        catch (Exception ex)
        {
            StatusText = $"速度测试失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ========== Phase 14: API 追踪 ==========

    [RelayCommand]
    private async Task RunApiTraceAsync()
    {
        IsApiTraceRunning = true;
        ApiTraceError = string.Empty;
        try
        {
            var result = await _apiTraceService.RunAsync();
            var unlockResult = await _unlockCatalogService.RunAsync();
            ApiTracePublicIp = result.PublicIp ?? "--";
            ApiTraceLocationCode = result.LocationCode ?? "--";
            ApiTraceLocationName = result.LocationName ?? "--";
            ApiTraceColo = result.CloudflareColo ?? "--";
            ApiTraceIsSupportedRegion = result.IsSupportedRegion;
            ApiTraceSupportSummary = result.SupportSummary;
            ApiTraceRawTrace = result.RawTrace;
            PublicIp = ApiTracePublicIp;
            CloudflareColo = ApiTraceColo;
            PublicIpCountry = string.IsNullOrWhiteSpace(result.LocationName) ? PublicIpCountry : result.LocationName;
            PublicIpFirstSeen = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            HasApiTraceResult = true;
            ApplyUnlockCatalogResult(unlockResult);
            TouchSnapshot();
            if (!string.IsNullOrEmpty(result.Error))
                ApiTraceError = result.Error;
            await RecordNetworkHistoryAsync("API trace", ApiTracePublicIp, ApiTraceSupportSummary, result.IsSupportedRegion ? 100 : 50, new
            {
                result.PublicIp,
                result.LocationCode,
                result.LocationName,
                result.CloudflareColo,
                result.IsSupportedRegion,
                result.SupportSummary,
                result.RawTrace,
                result.Error,
                UnlockCatalog = unlockResult
            });
        }
        catch (Exception ex)
        {
            ApiTraceError = ex.Message;
            HasApiTraceResult = false;
        }
        finally
        {
            IsApiTraceRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunWebApiTraceAsync()
        => await RunApiTraceAsync();

    [RelayCommand]
    private void OpenRawTrace(NetworkReviewUnlockRow? row)
    {
        if (row is null)
        {
            OfficialApiTraceDialogTitle = "API trace";
            OfficialApiTraceDialogContent = string.IsNullOrWhiteSpace(ApiTraceRawTrace)
                ? "No raw trace captured yet."
                : ApiTraceRawTrace;
        }
        else
        {
            OfficialApiTraceDialogTitle = $"{row.Name} raw trace";
            OfficialApiTraceDialogContent = string.IsNullOrWhiteSpace(row.TraceDetail)
                ? "No raw trace captured for this check."
                : row.TraceDetail;
        }

        IsOfficialApiTraceDialogOpen = true;
    }

    [RelayCommand]
    private void OpenProbeTrace(NetworkReviewUnlockRow? row)
        => OpenRawTrace(row);

    [RelayCommand]
    private void CloseOfficialApiTraceDialog()
    {
        IsOfficialApiTraceDialogOpen = false;
    }

    [RelayCommand]
    private void CopyOfficialApiTraceDialogContent()
    {
        if (string.IsNullOrWhiteSpace(OfficialApiTraceDialogContent))
        {
            StatusText = "No trace content to copy";
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(OfficialApiTraceDialogContent);
            Clipboard.SetContent(package);
            StatusText = "Trace content copied";
        }
        catch (Exception ex)
        {
            StatusText = $"复制追踪失败：{ex.Message}";
        }
    }

    // ========== Phase 14: STUN ==========

    [RelayCommand]
    private async Task RunStunProbeAsync()
    {
        IsStunRunning = true;
        StunError = string.Empty;
        StunAttributes.Clear();
        try
        {
            var transport = StunUseTcp ? StunTransportProtocol.Tcp : StunTransportProtocol.Udp;
            var result = await _stunService.ProbeAsync(StunServerHost, transport);
            StunMappedAddress = result.MappedAddress ?? "--";
            StunNatType = result.NatType ?? "--";
            StunNatTypeSummary = result.NatTypeSummary ?? "--";
            StunLocalEndpoint = result.LocalEndpoint ?? "--";
            StunRoundTrip = result.RoundTrip.HasValue ? $"{result.RoundTrip.Value.TotalMilliseconds:F1} ms" : "--";
            StunClassificationConfidence = result.ClassificationConfidence;
            StunCoverageSummary = result.CoverageSummary;
            StunReviewRecommendation = result.ReviewRecommendation;
            HasStunResult = true;
            LastNatCheckText = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            TouchSnapshot();
            UpdateRecentCheck("\u0053\u0054\u0055\u004E \u68C0\u6D4B", LastNatCheckText);
            foreach (var attr in result.Attributes)
                StunAttributes.Add(attr);
            if (!string.IsNullOrEmpty(result.Error))
                StunError = result.Error;
            await RecordNetworkHistoryAsync("STUN", StunServerHost, StunNatTypeSummary, null, result);
        }
        catch (Exception ex)
        {
            StunError = ex.Message;
            HasStunResult = false;
        }
        finally
        {
            IsStunRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunStunAsync()
        => await RunStunProbeAsync();

    // ========== Phase 15: Route Trace ==========

    [RelayCommand]
    private async Task RunRouteTraceAsync()
    {
        IsRouteTraceRunning = true;
        RouteTraceError = string.Empty;
        RouteTraceHops.Clear();
        ResetRouteMap("\u6b63\u5728\u7b49\u5f85\u8def\u7531 hop \u6570\u636e...");
        try
        {
            var result = await _routeService.RunAsync(
                RouteTraceTarget,
                maxHops: 30,
                timeoutMilliseconds: 2000,
                samplesPerHop: 3);
            RouteTraceSummary = result.Summary;
            RouteTraceEngine = result.TraceEngine ?? "--";
            RouteTraceRawOutput = string.IsNullOrWhiteSpace(result.RawTraceOutput)
                ? BuildRouteTerminalText(result.Hops)
                : result.RawTraceOutput;
            RouteProtocol = result.TraceEngine ?? "ICMP / TCP";
            RouteCheckedAtText = result.CheckedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            // Enrich hops with GeoIP data
            var enrichedHops = await EnrichHopsWithGeoIpAsync(result.Hops);
            _geoIpService.FlushCache();

            ApplyRouteSummary(enrichedHops, result.TraceCompleted);
            HasRouteTraceResult = true;
            foreach (var hop in enrichedHops)
                RouteTraceHops.Add(hop);
            RebuildRoutePathNodes(enrichedHops);
            var enrichedResult = result with { Hops = enrichedHops };
            await RenderRouteMapAsync(enrichedResult);
            TouchSnapshot(result.CheckedAt);
            UpdateRecentCheck("\u8DEF\u7531\u8FFD\u8E2A", RouteCheckedAtText);
            if (!string.IsNullOrEmpty(result.Error))
                RouteTraceError = result.Error;
            await RecordNetworkHistoryAsync("Route trace", RouteTraceTarget, RouteTraceSummary, result.TraceCompleted ? 100 : 60, enrichedResult);
        }
        catch (Exception ex)
        {
            RouteTraceError = ex.Message;
            HasRouteTraceResult = false;
            ResetRouteMap("\u8def\u7531\u5730\u56fe\u672a\u751f\u6210\uff1a\u8def\u7531\u8ffd\u8e2a\u5931\u8d25");
        }
        finally
        {
            IsRouteTraceRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunRouteAsync()
        => await RunRouteTraceAsync();

    // ========== Phase 16: Port Scan ==========

}
