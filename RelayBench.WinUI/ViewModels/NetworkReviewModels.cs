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

public sealed record NetworkReviewRouteNode(string HopLabel, string Title, string Address, string Detail, string Latency);

public sealed record NetworkReviewUnlockRow(
    string Name,
    string Status,
    string Latency,
    string Provider = "",
    string EndpointMeta = "",
    string Summary = "",
    string SemanticCategory = "",
    string SemanticVerdict = "",
    string Evidence = "",
    string TraceDetail = "",
    string Url = "",
    string Method = "",
    string FinalUrl = "",
    string ResponseContentType = "",
    string Error = "");

public sealed record NetworkReviewRecentCheck(string Name, string Time);

/// <summary>
/// Accumulated per-hop statistics for MTR continuous mode.
/// </summary>
public sealed record MtrHopStatistic(
    int HopNumber,
    string Address,
    string? Hostname,
    int TotalSent,
    int TotalReceived,
    double LossPercent,
    double BestRtt,
    double AverageRtt,
    double WorstRtt,
    int Rounds)
{
    public string LossText => $"{LossPercent:F1}%";
    public string BestRttText => $"{BestRtt:F1} ms";
    public string AverageRttText => $"{AverageRtt:F1} ms";
    public string WorstRttText => $"{WorstRtt:F1} ms";
    public string DisplayAddress => Hostname ?? Address;
}
