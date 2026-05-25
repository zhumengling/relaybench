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
    private async Task RunSplitRoutingAsync()
    {
        IsSplitRoutingRunning = true;
        SplitRoutingError = string.Empty;
        SplitRoutingExitChecks.Clear();
        SplitRoutingDnsViews.Clear();
        try
        {
            var result = await _splitRoutingService.RunAsync(null);
            SplitRoutingSummary = result.Summary;
            SplitRoutingMultiExit = result.MultiExitSuspected;
            SplitRoutingDnsSplit = result.DnsSplitSuspected;
            DnsLeakStatus = result.DnsSplitSuspected
                ? "\u7591\u4F3C DNS \u5206\u6D41"
                : "\u672A\u68C0\u6D4B\u5230 DNS \u6CC4\u6F0F";
            HasSplitRoutingResult = true;
            foreach (var check in result.ExitChecks)
                SplitRoutingExitChecks.Add(check);
            foreach (var dns in result.DnsViews)
                SplitRoutingDnsViews.Add(dns);
            TouchSnapshot(result.CheckedAt);
            UpdateRecentCheck("DNS \u6CC4\u6F0F", result.CheckedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(result.Error))
                SplitRoutingError = result.Error;
            await RecordNetworkHistoryAsync("Split routing", "default hosts", SplitRoutingSummary, result.MultiExitSuspected || result.DnsSplitSuspected ? 65 : 100, result);
        }
        catch (Exception ex)
        {
            SplitRoutingError = ex.Message;
            HasSplitRoutingResult = false;
        }
        finally
        {
            IsSplitRoutingRunning = false;
        }
    }

    // ========== Phase 17: IP Risk ==========

    [RelayCommand]
    private async Task RunIpRiskReviewAsync()
    {
        IsIpRiskRunning = true;
        IpRiskError = string.Empty;
        IpRiskSources.Clear();
        IpRiskSignals.Clear();
        IpRiskPositiveSignals.Clear();
        try
        {
            var requestedIp = string.IsNullOrWhiteSpace(IpRiskTargetAddress) ? null : IpRiskTargetAddress.Trim();
            var result = await _ipRiskService.RunAsync(requestedIp);
            IpRiskPublicIp = result.PublicIp ?? "--";
            IpRiskVerdict = result.Verdict;
            IpRiskSummary = result.Summary;
            IpRiskCountry = result.Country ?? "--";
            IpRiskOrganization = result.Organization ?? "--";
            PublicIp = IpRiskPublicIp;
            PublicIpCountry = BuildCountryCityText(result.Country, result.City);
            PublicIpAsn = result.Asn ?? PublicIpAsn;
            PublicIpOrganization = result.Organization ?? PublicIpOrganization;
            LastRiskReviewText = result.CheckedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            ApplyRiskSnapshot(result);
            HasIpRiskResult = true;
            foreach (var source in result.Sources)
                IpRiskSources.Add(source);
            foreach (var signal in result.RiskSignals)
                IpRiskSignals.Add(signal);
            foreach (var signal in result.PositiveSignals)
                IpRiskPositiveSignals.Add(signal);
            TouchSnapshot(result.CheckedAt);
            if (!string.IsNullOrEmpty(result.Error))
                IpRiskError = result.Error;
            await RecordNetworkHistoryAsync("IP risk", IpRiskPublicIp, IpRiskSummary, 100 - EstimateRiskScore(result), result);
        }
        catch (Exception ex)
        {
            IpRiskError = ex.Message;
            HasIpRiskResult = false;
        }
        finally
        {
            IsIpRiskRunning = false;
        }
    }

    [RelayCommand]
    private async Task RefreshSnapshotAsync()
    {
        await RunOverviewSnapshotAsync();
    }

    [RelayCommand]
    private void ExportNetworkSnapshot()
    {
        try
        {
            var fileName = $"network-review-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.md";
            var path = Path.Combine(RelayBenchPaths.ExportsDirectory, fileName);
            File.WriteAllText(path, BuildSnapshotMarkdown(), Encoding.UTF8);
            StatusText = $"Exported: {path}";
            TouchSnapshot();
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

}
