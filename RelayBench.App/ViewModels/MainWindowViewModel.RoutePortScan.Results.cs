using System.Text;
using RelayBench.App.Services;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void ApplyRouteResult(RouteDiagnosticsResult result)
        => DisplayRouteResult(result, persistState: true, appendModuleOutput: true);

    private void DisplayRouteResult(RouteDiagnosticsResult result, bool persistState, bool appendModuleOutput)
    {
        var resolvedAddressText = result.ResolvedAddresses.Count == 0
            ? "无"
            : string.Join(", ", result.ResolvedAddresses);
        var systemResolvedAddressText = result.SystemResolvedAddresses is { Count: > 0 }
            ? string.Join(", ", result.SystemResolvedAddresses)
            : "无";
        var traceTargetText = string.IsNullOrWhiteSpace(result.TraceTarget)
            ? result.Target
            : result.TraceTarget;
        var resolutionSummaryText = string.IsNullOrWhiteSpace(result.ResolutionSummary)
            ? "无"
            : result.ResolutionSummary;
        var traceEngineText = string.IsNullOrWhiteSpace(result.TraceEngine)
            ? "Windows tracert + Ping"
            : result.TraceEngine;
        var traceEngineSummaryText = string.IsNullOrWhiteSpace(result.TraceEngineSummary)
            ? "无"
            : result.TraceEngineSummary;
        var routeResolverText = SelectedRouteResolverDescription;
        var hopMetadataCount = result.Hops.Count(static hop => hop.HasTraceMetadata);

        RouteSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"目标：{result.Target}\n" +
            "探测模式：仅内置 ICMP MTR / Windows tracert\n" +
            $"解析器：{routeResolverText}\n" +
            $"实际探测：{traceTargetText}\n" +
            $"解析地址：{resolvedAddressText}\n" +
            $"系统解析：{systemResolvedAddressText}\n" +
            $"解析说明：{resolutionSummaryText}\n" +
            $"探测引擎：{traceEngineText}\n" +
            $"引擎说明：{traceEngineSummaryText}\n" +
            $"最大跳数：{result.MaxHops}\n" +
            $"单次探测超时：{result.TimeoutMilliseconds} ms\n" +
            $"每跳采样次数：{result.SamplesPerHop}\n" +
            $"带元信息 hop 数：{hopMetadataCount}\n" +
            $"是否到达目标：{(result.TraceCompleted ? "是" : "否")}\n" +
            $"可响应 hop 数：{result.ResponsiveHopCount}\n" +
            $"摘要：{result.Summary}\n" +
            $"错误：{result.Error ?? "无"}";

        RouteHopSummary = result.Hops.Count == 0
            ? "没有采集到 hop 数据。"
            : string.Join(
                "\n",
                result.Hops.Select(hop =>
                    $"#{hop.HopNumber,2} {hop.Address ?? "未解析"}  " +
                    $"trace={FormatTraceSamples(hop.TraceRoundTripTimes)}  " +
                    $"发送={hop.SentProbes} 接收={hop.ReceivedResponses}  " +
                    $"丢包={FormatLoss(hop.LossPercent)}  " +
                    $"最佳={FormatLongMilliseconds(hop.BestRoundTripTime)}  " +
                    $"平均={FormatAverageMilliseconds(hop.AverageRoundTripTime)}  " +
                    $"最差={FormatLongMilliseconds(hop.WorstRoundTripTime)}" +
                    $"{FormatHopMetadata(hop)}"));

        RouteRawOutput = string.IsNullOrWhiteSpace(result.RawTraceOutput)
            ? "没有捕获到原始追踪输出。"
            : result.RawTraceOutput;

        RouteMapSummary = "正在根据最新路由结果生成地理路径图...";
        RouteGeoSummary = "路由 / MTR 采样完成后，这里会显示 hop 的地理定位摘要。";
        RouteMapImage = null;

        if (appendModuleOutput)
        {
            AppendModuleOutput("路由 / MTR 返回", RouteSummary, RouteHopSummary);
        }

        if (persistState)
        {
            SaveState();
        }
    }

    private void ApplyRouteMapResult(RouteMapRenderResult result)
        => DisplayRouteMapResult(result, persistState: true, appendModuleOutput: true);

    private void DisplayRouteMapResult(RouteMapRenderResult result, bool persistState, bool appendModuleOutput)
    {
        RouteMapSummary =
            $"摘要：{result.Summary}\n" +
            $"错误：{result.Error ?? "无"}";

        RouteGeoSummary = string.IsNullOrWhiteSpace(result.GeoSummary)
            ? "暂无 hop 地理定位结果。"
            : result.GeoSummary;
        RouteMapImage = result.MapImage;
        if (appendModuleOutput)
        {
            AppendModuleOutput("路由地图返回", RouteMapSummary, RouteGeoSummary);
        }

        if (persistState)
        {
            SaveState();
        }
    }

    private void ApplyPortScanResult(PortScanResult result)
        => DisplayPortScanResult(result, rememberAsCurrent: true, persistState: true, appendModuleOutput: true);

    private void DisplayPortScanResult(
        PortScanResult result,
        bool rememberAsCurrent,
        bool persistState,
        bool appendModuleOutput)
    {
        if (rememberAsCurrent)
        {
            _lastPortScanResult = result;
        }

        _portScanCurrentExecutionTarget = string.IsNullOrWhiteSpace(result.Target) ? _portScanCurrentExecutionTarget : result.Target;

        _portScanLiveFindingKeys.Clear();
        PortScanFindings.Clear();
        FilteredPortScanFindings.Clear();
        foreach (var finding in result.Findings)
        {
            PortScanFindings.Add(finding);
            _portScanLiveFindingKeys.Add(BuildPortScanFindingKey(finding));
        }

        SortPortScanFindings();
        RefreshFilteredPortScanFindings();

        var resolvedAddressText = result.ResolvedAddresses.Count == 0
            ? "无"
            : string.Join(", ", result.ResolvedAddresses);
        var systemResolvedAddressText = result.SystemResolvedAddresses is { Count: > 0 }
            ? string.Join(", ", result.SystemResolvedAddresses)
            : "无";
        var profileText = string.IsNullOrWhiteSpace(result.ProfileName)
            ? "--"
            : $"{result.ProfileName} ({result.ProfileKey})";

        PortScanSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"引擎可用：{(result.IsEngineAvailable ? "是" : "否")}\n" +
            $"扫描引擎：{result.EngineName}\n" +
            $"引擎版本：{result.EngineVersion}\n" +
            $"目标：{(string.IsNullOrWhiteSpace(result.Target) ? "--" : result.Target)}\n" +
            $"解析地址：{resolvedAddressText}\n" +
            $"系统解析：{systemResolvedAddressText}\n" +
            $"扫描模板：{profileText}\n" +
            $"自定义端口：{(string.IsNullOrWhiteSpace(result.CustomPortsText) ? "无" : result.CustomPortsText)}\n" +
            $"生效端口：{(string.IsNullOrWhiteSpace(result.EffectivePortsText) ? "--" : result.EffectivePortsText)}\n" +
            $"是否执行扫描：{(result.ScanExecuted ? "是" : "否")}\n" +
            $"尝试端点数：{result.AttemptedEndpointCount}\n" +
            $"开放端口数：{result.OpenPortCount}\n" +
            $"开放端点数：{result.OpenEndpointCount}\n" +
            $"摘要：{result.Summary}\n" +
            $"错误：{result.Error ?? "无"}";

        PortScanProgressMaximum = Math.Max(1d, result.AttemptedEndpointCount);
        PortScanProgressValue = result.ScanExecuted ? result.AttemptedEndpointCount : 0d;
        PortScanProgressSummary = result.ScanExecuted
            ? $"已完成 {result.AttemptedEndpointCount}/{Math.Max(1, result.AttemptedEndpointCount)}，开放端点 {result.OpenEndpointCount} 个。"
            : result.Summary;

        var findingDetail = result.Findings.Count == 0
            ? "未发现开放端点。"
            : string.Join("\n\n", result.Findings.Select(FormatPortScanFinding));

        PortScanDetail =
            $"执行命令：\n{(string.IsNullOrWhiteSpace(result.CommandLine) ? "未执行" : result.CommandLine)}\n\n" +
            $"地址解析：\n最终用于扫描：{resolvedAddressText}\n系统解析：{systemResolvedAddressText}\n\n" +
            $"端口与服务详情：\n{findingDetail}\n\n" +
            $"标准错误：\n{(string.IsNullOrWhiteSpace(result.StandardError) ? "无" : result.StandardError)}";

        PortScanRawOutput = !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput
            : !string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardError
                : "没有捕获到扫描日志输出。";

        RefreshPortScanBatchSummary();
        RefreshPortScanExportSummary();
        RefreshPortScanExportCommands();

        if (appendModuleOutput)
        {
            AppendModuleOutput("端口扫描返回", PortScanSummary, PortScanDetail);
        }

        if (persistState)
        {
            SaveState();
        }
    }

    private static string FormatPortScanFinding(PortScanFinding finding)
    {
        StringBuilder builder = new();
        builder.AppendLine($"{finding.Endpoint}/{finding.Protocol}");
        builder.AppendLine($"  延迟：{finding.ConnectLatencyMilliseconds} ms");
        builder.AppendLine($"  服务提示：{finding.ServiceHint}");
        builder.AppendLine($"  Banner：{FormatOptionalValue(finding.Banner)}");
        builder.AppendLine($"  TLS：{FormatOptionalValue(finding.TlsSummary)}");
        builder.AppendLine($"  应用探测：{FormatOptionalValue(finding.HttpSummary)}");
        builder.Append($"  备注：{FormatOptionalValue(finding.ProbeNotes)}");
        return builder.ToString();
    }

    private static string FormatOptionalValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "无" : value;
}
