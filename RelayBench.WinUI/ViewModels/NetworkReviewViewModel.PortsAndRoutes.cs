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
    private async Task DetectPortScanEngineAsync()
    {
        IsPortScanRunning = true;
        PortScanError = string.Empty;
        try
        {
            var result = await _portScanService.DetectAsync();
            PortScanSummary = result.Summary;
            PortScanProfileText = string.IsNullOrWhiteSpace(result.EngineName)
                ? "Built-in port scan engine"
                : $"{result.EngineName} {result.EngineVersion}";
            PortScanRawOutput = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? BuildPortScanRawOutput(result)
                : result.StandardOutput;
            HasPortScanResult = result.IsEngineAvailable;
            TouchSnapshot(result.CheckedAt);
            UpdateRecentCheck("Port scan engine", result.CheckedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(result.Error))
            {
                PortScanError = result.Error;
            }

            StatusText = result.Summary;
            await RecordNetworkHistoryAsync("Port scan engine", result.EngineName, result.Summary, result.IsEngineAvailable ? 100 : 0, result);
        }
        catch (Exception ex)
        {
            PortScanError = ex.Message;
            HasPortScanResult = false;
        }
        finally
        {
            IsPortScanRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunPortScanAsync()
    {
        IsPortScanRunning = true;
        PortScanError = string.Empty;
        PortScanFindings.Clear();
        try
        {
            var profileKey = PortScanSelectedProfileIndex >= 0 && PortScanSelectedProfileIndex < PortScanProfiles.Count
                ? PortScanProfiles[PortScanSelectedProfileIndex].Key
                : _portScanService.GetDefaultProfile().Key;
            var customPorts = string.IsNullOrWhiteSpace(PortScanCustomPorts) ? null : PortScanCustomPorts;
            var result = await _portScanService.RunAsync(PortScanTarget, profileKey, customPorts);
            PortScanSummary = result.Summary;
            PortScanProfileText = $"{result.ProfileName} · {result.EffectivePortsText}";
            PortScanResolvedAddressText = BuildAddressSummary(result.ResolvedAddresses.Concat(result.SystemResolvedAddresses ?? []));
            PortScanRawOutput = BuildPortScanRawOutput(result);
            HasPortScanResult = true;
            foreach (var finding in result.Findings)
                PortScanFindings.Add(finding);
            TouchSnapshot(result.CheckedAt);
            UpdateRecentCheck("\u7AEF\u53E3\u626B\u63CF", result.CheckedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(result.Error))
                PortScanError = result.Error;
            await RecordNetworkHistoryAsync("Port scan", PortScanTarget, PortScanSummary, null, result);
        }
        catch (Exception ex)
        {
            PortScanError = ex.Message;
            HasPortScanResult = false;
        }
        finally
        {
            IsPortScanRunning = false;
        }
    }

    // ========== Phase 15: MTR Continuous Mode ==========

    [RelayCommand]
    private async Task RunMtrContinuousAsync()
    {
        if (IsMtrRunning) return;

        IsMtrRunning = true;
        MtrRoundsCompleted = 0;
        MtrStatusText = "Starting MTR...";
        MtrHopStatistics.Clear();
        _mtrCts = new CancellationTokenSource();

        try
        {
            var ct = _mtrCts.Token;
            while (!ct.IsCancellationRequested)
            {
                var result = await _routeService.RunAsync(
                    RouteTraceTarget,
                    maxHops: 30,
                    timeoutMilliseconds: 2000,
                    samplesPerHop: 3,
                    cancellationToken: ct);

                MtrRoundsCompleted++;
                MtrStatusText = $"MTR round {MtrRoundsCompleted} completed";

                // Accumulate per-hop loss statistics
                foreach (var hop in result.Hops)
                {
                    var existing = MtrHopStatistics.FirstOrDefault(s => s.HopNumber == hop.HopNumber);
                    if (existing is null)
                    {
                        MtrHopStatistics.Add(new MtrHopStatistic(
                            hop.HopNumber,
                            hop.Address ?? "*",
                            hop.Hostname,
                            hop.SentProbes,
                            hop.ReceivedResponses,
                            hop.LossPercent ?? 0,
                            hop.BestRoundTripTime ?? 0,
                            hop.AverageRoundTripTime ?? 0,
                            hop.WorstRoundTripTime ?? 0,
                            1));
                    }
                    else
                    {
                        var totalSent = existing.TotalSent + hop.SentProbes;
                        var totalReceived = existing.TotalReceived + hop.ReceivedResponses;
                        var lossPercent = totalSent > 0 ? (1.0 - (double)totalReceived / totalSent) * 100 : 0;
                        var best = hop.BestRoundTripTime.HasValue
                            ? Math.Min(existing.BestRtt, hop.BestRoundTripTime.Value)
                            : existing.BestRtt;
                        var worst = hop.WorstRoundTripTime.HasValue
                            ? Math.Max(existing.WorstRtt, hop.WorstRoundTripTime.Value)
                            : existing.WorstRtt;
                        var avgRtt = hop.AverageRoundTripTime.HasValue
                            ? (existing.AverageRtt * existing.Rounds + hop.AverageRoundTripTime.Value) / (existing.Rounds + 1)
                            : existing.AverageRtt;

                        var index = MtrHopStatistics.IndexOf(existing);
                        MtrHopStatistics[index] = existing with
                        {
                            Address = hop.Address ?? existing.Address,
                            TotalSent = totalSent,
                            TotalReceived = totalReceived,
                            LossPercent = lossPercent,
                            BestRtt = best,
                            AverageRtt = avgRtt,
                            WorstRtt = worst,
                            Rounds = existing.Rounds + 1
                        };
                    }
                }

                // Brief pause between rounds
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException)
        {
            MtrStatusText = $"MTR 已在 {MtrRoundsCompleted} 轮后停止";
        }
        catch (Exception ex)
        {
            MtrStatusText = $"MTR error: {ex.Message}";
        }
        finally
        {
            IsMtrRunning = false;
            _mtrCts = null;
        }
    }

    [RelayCommand]
    private async Task RunRouteContinuousAsync()
        => await RunMtrContinuousAsync();

    [RelayCommand]
    private void StopMtrContinuous()
    {
        _mtrCts?.Cancel();
    }

    [RelayCommand]
    private void StopRouteContinuous()
        => StopMtrContinuous();

    // ========== Phase 16: Port Scan Batch & Export ==========

    [RelayCommand]
    private async Task RunPortScanBatchAsync()
    {
        if (string.IsNullOrWhiteSpace(PortScanBatchTargets))
        {
            PortScanBatchSummary = "No batch targets specified";
            return;
        }

        IsPortScanBatchRunning = true;
        PortScanError = string.Empty;
        PortScanBatchFindings.Clear();
        PortScanBatchSummary = "Running batch scan...";

        try
        {
            var targets = PortScanBatchTargets
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => !t.StartsWith('#'))
                .ToList();

            var profileKey = PortScanSelectedProfileIndex >= 0 && PortScanSelectedProfileIndex < PortScanProfiles.Count
                ? PortScanProfiles[PortScanSelectedProfileIndex].Key
                : _portScanService.GetDefaultProfile().Key;
            var customPorts = string.IsNullOrWhiteSpace(PortScanCustomPorts) ? null : PortScanCustomPorts;

            var completedCount = 0;
            var totalOpen = 0;
            var results = new List<PortScanResult>();

            foreach (var target in targets)
            {
                PortScanBatchSummary = $"Scanning {completedCount + 1}/{targets.Count}: {target}";
                var result = await _portScanService.RunAsync(target, profileKey, customPorts);
                results.Add(result);

                foreach (var finding in result.Findings)
                    PortScanBatchFindings.Add(finding);

                totalOpen += result.Findings.Count;
                completedCount++;
            }

            PortScanBatchSummary = $"Batch complete: {targets.Count} targets, {totalOpen} open endpoints found";
            HasPortScanResult = true;
            TouchSnapshot();
            var batchPayload = new
            {
                Targets = targets,
                TargetCount = targets.Count,
                ProfileKey = profileKey,
                CustomPorts = customPorts,
                OpenPortCount = totalOpen,
                Findings = PortScanBatchFindings.ToArray(),
                Results = results.Select(static result => new
                {
                    result.Target,
                    result.ProfileKey,
                    result.ProfileName,
                    result.EffectivePortsText,
                    result.ScanExecuted,
                    result.ScanSucceeded,
                    result.ExitCode,
                    result.OpenPortCount,
                    result.OpenEndpointCount,
                    result.AttemptedEndpointCount,
                    result.ResolvedAddresses,
                    result.SystemResolvedAddresses,
                    result.Summary,
                    result.Error,
                    result.StandardOutput,
                    result.StandardError,
                    result.Findings
                }).ToArray()
            };
            await RecordNetworkHistoryAsync("Port scan batch", $"{targets.Count} targets", PortScanBatchSummary, null, batchPayload);
        }
        catch (Exception ex)
        {
            PortScanError = ex.Message;
            PortScanBatchSummary = $"批量扫描失败：{ex.Message}";
        }
        finally
        {
            IsPortScanBatchRunning = false;
        }
    }

    [RelayCommand]
    private void ExportPortScanCsv()
    {
        var findings = PortScanBatchFindings.Count > 0 ? PortScanBatchFindings : PortScanFindings;
        if (findings.Count == 0)
        {
            PortScanExportStatus = "No scan results to export";
            return;
        }

        try
        {
            var exportDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RelayBench", "WinUI", "exports", "port-scan");
            Directory.CreateDirectory(exportDir);

            var fileName = $"port-scan-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.csv";
            var filePath = Path.Combine(exportDir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("Address,Port,Protocol,Latency(ms),Service,Banner,TLS,HTTP,Notes");
            foreach (var f in findings)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(f.Address),
                    f.Port,
                    CsvEscape(f.Protocol),
                    f.ConnectLatencyMilliseconds,
                    CsvEscape(f.ServiceHint),
                    CsvEscape(f.Banner ?? ""),
                    CsvEscape(f.TlsSummary ?? ""),
                    CsvEscape(f.HttpSummary ?? ""),
                    CsvEscape(f.ProbeNotes ?? "")));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            PortScanExportStatus = $"CSV exported: {filePath}";
            StatusText = PortScanExportStatus;
        }
        catch (Exception ex)
        {
            PortScanExportStatus = $"CSV 导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportPortScanExcel()
    {
        var findings = PortScanBatchFindings.Count > 0 ? PortScanBatchFindings : PortScanFindings;
        if (findings.Count == 0)
        {
            PortScanExportStatus = "No scan results to export";
            return;
        }

        try
        {
            var exportDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RelayBench", "WinUI", "exports", "port-scan");
            Directory.CreateDirectory(exportDir);

            var fileName = $"port-scan-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.xlsx";
            var filePath = Path.Combine(exportDir, fileName);

            // Write a simple XML spreadsheet (Excel-compatible SpreadsheetML)
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<?mso-application progid=\"Excel.Sheet\"?>");
            sb.AppendLine("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">");
            sb.AppendLine("<Worksheet ss:Name=\"Port Scan Results\">");
            sb.AppendLine("<Table>");

            // Header row
            sb.AppendLine("<Row>");
            foreach (var header in new[] { "Address", "Port", "Protocol", "Latency(ms)", "Service", "Banner", "TLS", "HTTP", "Notes" })
                sb.AppendLine($"<Cell><Data ss:Type=\"String\">{XmlEscape(header)}</Data></Cell>");
            sb.AppendLine("</Row>");

            // Data rows
            foreach (var f in findings)
            {
                sb.AppendLine("<Row>");
                sb.AppendLine($"<Cell><Data ss:Type=\"String\">{XmlEscape(f.Address)}</Data></Cell>");
                sb.AppendLine($"<Cell><Data ss:Type=\"Number\">{f.Port}</Data></Cell>");
                sb.AppendLine($"<Cell><Data ss:Type=\"String\">{XmlEscape(f.Protocol)}</Data></Cell>");
                sb.AppendLine($"<Cell><Data ss:Type=\"Number\">{f.ConnectLatencyMilliseconds}</Data></Cell>");
                sb.AppendLine($"<Cell><Data ss:Type=\"String\">{XmlEscape(f.ServiceHint)}</Data></Cell>");
                sb.AppendLine($"<Cell><Data ss:Type=\"String\">{XmlEscape(f.Banner ?? "")}</Data></Cell>");
                sb.AppendLine($"<Cell><Data ss:Type=\"String\">{XmlEscape(f.TlsSummary ?? "")}</Data></Cell>");
                sb.AppendLine($"<Cell><Data ss:Type=\"String\">{XmlEscape(f.HttpSummary ?? "")}</Data></Cell>");
                sb.AppendLine($"<Cell><Data ss:Type=\"String\">{XmlEscape(f.ProbeNotes ?? "")}</Data></Cell>");
                sb.AppendLine("</Row>");
            }

            sb.AppendLine("</Table>");
            sb.AppendLine("</Worksheet>");
            sb.AppendLine("</Workbook>");

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            PortScanExportStatus = $"Excel exported: {filePath}";
            StatusText = PortScanExportStatus;
        }
        catch (Exception ex)
        {
            PortScanExportStatus = $"Excel 导出失败：{ex.Message}";
        }
    }

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static string XmlEscape(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    // ========== Phase 17: Split Routing ==========

}
