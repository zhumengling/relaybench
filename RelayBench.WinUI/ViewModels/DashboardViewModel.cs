using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using System.Text;
using System.Text.Json;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IHistoryRepository _historyRepository = new HistoryRepository();
    private readonly BasicNetworkDiagnosticsService _networkService = new();
    private readonly CloudflareSpeedTestService _speedTestService = new();
    private readonly WebApiTraceService _apiTraceService = new();
    private readonly StunProbeService _stunService = new();
    private readonly RouteDiagnosticsService _routeService = new();
    private readonly PortScanDiagnosticsService _portScanService = new();
    private readonly SplitRoutingDiagnosticsService _splitRoutingService = new();
    private readonly ExitIpRiskReviewService _ipRiskService = new();

    [ObservableProperty] public partial double OverallScore { get; set; }
    [ObservableProperty] public partial string SuccessRate { get; set; } = "0.0%";
    [ObservableProperty] public partial double SuccessRateValue { get; set; }
    [ObservableProperty] public partial string AvgLatency { get; set; } = "0 ms";
    [ObservableProperty] public partial string Ttft { get; set; } = "0 ms";
    [ObservableProperty] public partial string Throughput { get; set; } = "0 tok/s";
    [ObservableProperty] public partial int RiskDomains { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string PublicIp { get; set; } = "0";
    [ObservableProperty] public partial string HostName { get; set; } = "0";
    [ObservableProperty] public partial string CloudflareColo { get; set; } = "0";
    [ObservableProperty] public partial int AdapterCount { get; set; }

    // Speed test results
    [ObservableProperty] public partial string DownloadSpeed { get; set; } = "0 Mbps";
    [ObservableProperty] public partial string UploadSpeed { get; set; } = "0 Mbps";
    [ObservableProperty] public partial string IdleLatency { get; set; } = "0 ms";
    [ObservableProperty] public partial string PacketLoss { get; set; } = "0%";

    // 今日Window (Today's Window) section
    [ObservableProperty] public partial string TodayTestCount { get; set; } = "0";
    [ObservableProperty] public partial string TodayModelCount { get; set; } = "0";
    [ObservableProperty] public partial string TodayRunDuration { get; set; } = "0s";
    [ObservableProperty] public partial string TodayExceptionCount { get; set; } = "0";
    [ObservableProperty] public partial string TransparentProxyPort { get; set; } = "0";
    [ObservableProperty] public partial double TransparentProxyActivityValue { get; set; }
    [ObservableProperty] public partial string DashboardCacheHitRate { get; set; } = "0.0%";
    [ObservableProperty] public partial double DashboardCacheHitValue { get; set; }
    [ObservableProperty] public partial string RiskDomainObservationText { get; set; } = "0 个风险域名待复核";

    // 图表分析 (Chart Analysis) - Latency trend bar heights
    [ObservableProperty] public partial double LatencyBar0 { get; set; }
    [ObservableProperty] public partial double LatencyBar1 { get; set; }
    [ObservableProperty] public partial double LatencyBar2 { get; set; }
    [ObservableProperty] public partial double LatencyBar3 { get; set; }
    [ObservableProperty] public partial double LatencyBar4 { get; set; }
    [ObservableProperty] public partial double LatencyBar5 { get; set; }
    [ObservableProperty] public partial double LatencyBar6 { get; set; }

    // Success分布 (Success distribution) progress bar values
    [ObservableProperty] public partial double SuccessOkPercent { get; set; }
    [ObservableProperty] public partial double SuccessRetryPercent { get; set; }
    [ObservableProperty] public partial double SuccessFailPercent { get; set; }

    // 吞吐对比 (Throughput comparison) bar heights
    [ObservableProperty] public partial double ThroughputBarA { get; set; }
    [ObservableProperty] public partial double ThroughputBarB { get; set; }
    [ObservableProperty] public partial double ThroughputBarC { get; set; }
    [ObservableProperty] public partial double ThroughputBarD { get; set; }

    public DashboardViewModel()
    {
        ApplyZeroHistoryState();
        LoadHistoricalDashboardState();
    }

    private void ApplyZeroHistoryState()
    {
        OverallScore = 0;
        SuccessRate = "0.0%";
        SuccessRateValue = 0;
        AvgLatency = "0 ms";
        Ttft = "0 ms";
        Throughput = "0 tok/s";
        RiskDomains = 0;
        PublicIp = "0";
        HostName = "0";
        CloudflareColo = "0";
        AdapterCount = 0;
        DownloadSpeed = "0 Mbps";
        UploadSpeed = "0 Mbps";
        IdleLatency = "0 ms";
        PacketLoss = "0%";
        TodayTestCount = "0";
        TodayModelCount = "0";
        TodayRunDuration = "0s";
        TodayExceptionCount = "0";
        TransparentProxyPort = "0";
        TransparentProxyActivityValue = 0;
        DashboardCacheHitRate = "0.0%";
        DashboardCacheHitValue = 0;
        RiskDomainObservationText = "0 个风险域名待复核";
        SetLatencyBars([0, 0, 0, 0, 0, 0, 0]);
        SuccessOkPercent = 0;
        SuccessRetryPercent = 0;
        SuccessFailPercent = 0;
        SetThroughputBars([0, 0, 0, 0]);
    }

    private void LoadHistoricalDashboardState()
    {
        try
        {
            var reports = _historyRepository
                .QueryAsync(new HistoryQuery(Limit: 500))
                .GetAwaiter()
                .GetResult()
                .ToArray();
            if (reports.Length == 0)
            {
                StatusText = "暂无History测试数据，当前显示 0";
                return;
            }

            var fullReports = reports
                .Select(summary => _historyRepository.GetAsync(summary.RunId).GetAwaiter().GetResult())
                .Where(static report => report is not null)
                .Cast<HistoryReport>()
                .OrderBy(static report => report.CreatedAtUtc)
                .ToArray();
            if (fullReports.Length == 0)
            {
                StatusText = "暂无History测试数据，当前显示 0";
                return;
            }

            var today = DateTimeOffset.Now.Date;
            var todayReports = fullReports
                .Where(report => report.CreatedAtUtc.ToLocalTime().Date == today)
                .ToArray();
            var window = todayReports.Length > 0 ? todayReports : fullReports;
            var scored = window
                .Select(static report => Math.Clamp(report.Score ?? 0, 0, 100))
                .ToArray();

            OverallScore = scored.Length == 0 ? 0 : Math.Round(scored.Average(), 1);
            var averagePassRate = ResolveAveragePassRate(window);
            SuccessRate = $"{averagePassRate:F1}%";
            SuccessRateValue = averagePassRate;
            TodayTestCount = todayReports.Length.ToString();
            TodayRunDuration = FormatDuration(TimeSpan.FromMilliseconds(todayReports.Sum(static report => Math.Max(0, report.DurationMs ?? 0))));
            TodayExceptionCount = todayReports.Count(IsExceptionReport).ToString();
            RiskDomains = window.Count(IsExceptionReport);

            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var report in todayReports)
            {
                AddModelName(models, report.PayloadJson);
            }
            TodayModelCount = models.Count.ToString();

            ApplyLatestNetworkAndSpeed(fullReports.Last());
            ApplyLatencyTrend(fullReports);
            ApplySuccessDistribution(window);
            ApplyThroughputTrend(fullReports);
            StatusText = $"已加载 {fullReports.Length} 条真实History测试数据";
        }
        catch (Exception ex)
        {
            ApplyZeroHistoryState();
            StatusText = $"History数据读取失败，当前显示 0: {ex.Message}";
        }
    }

    private static bool IsExceptionReport(HistoryReport report)
    {
        if ((report.Score ?? 100) < 60)
        {
            return true;
        }

        var text = $"{report.Summary} {report.PayloadJson}";
        return text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("错误", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static double ResolveAveragePassRate(IReadOnlyList<HistoryReport> reports)
    {
        var values = reports
            .Select(ReadPassRate)
            .Where(static value => value.HasValue)
            .Select(static value => Math.Clamp(value!.Value, 0, 100))
            .ToArray();
        if (values.Length > 0)
        {
            return values.Average();
        }

        var scored = reports.Select(static report => report.Score ?? 0).ToArray();
        return scored.Length == 0 ? 0 : scored.Count(static score => score >= 60) * 100.0 / scored.Length;
    }

    private static double? ReadPassRate(HistoryReport report)
    {
        if (!HistoryPayloadReader.TryParse(report.PayloadJson, out var document))
        {
            return report.Score.HasValue ? (report.Score.Value >= 60 ? 100 : 0) : null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (HistoryPayloadReader.ReadDouble(root, "SuccessRate") is { } successRate)
            {
                return successRate;
            }

            var scenarios = HistoryPayloadReader.ReadArray(root, "scenarios");
            if (scenarios.Count > 0)
            {
                var passed = scenarios.Count(static scenario =>
                    HistoryPayloadReader.ReadBool(scenario, "Success") == true ||
                    string.Equals(HistoryPayloadReader.ReadString(scenario, "Status"), "Passed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(HistoryPayloadReader.ReadString(scenario, "status"), "Passed", StringComparison.OrdinalIgnoreCase));
                return passed * 100.0 / scenarios.Count;
            }

            var sites = HistoryPayloadReader.ReadArray(root, "sites");
            if (sites.Count > 0)
            {
                var rates = sites
                    .Select(static site => HistoryPayloadReader.ReadDouble(site, "SuccessRate"))
                    .Where(static value => value.HasValue)
                    .Select(static value => value!.Value)
                    .ToArray();
                return rates.Length == 0 ? null : rates.Average();
            }
        }

        return report.Score.HasValue ? (report.Score.Value >= 60 ? 100 : 0) : null;
    }

    private void ApplyLatestNetworkAndSpeed(HistoryReport latest)
    {
        if (!HistoryPayloadReader.TryParse(latest.PayloadJson, out var document))
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            PublicIp = HistoryPayloadReader.FirstString(root, ["PublicIp"], ["publicIp"]) ?? PublicIp;
            DownloadSpeed = HistoryPayloadReader.FirstString(root, ["DownloadSpeed"], ["downloadSpeed"]) ?? DownloadSpeed;
            UploadSpeed = HistoryPayloadReader.FirstString(root, ["UploadSpeed"], ["uploadSpeed"]) ?? UploadSpeed;
            IdleLatency = HistoryPayloadReader.FirstString(root, ["IdleLatency"], ["idleLatency"]) ?? IdleLatency;
            PacketLoss = HistoryPayloadReader.FirstString(root, ["PacketLoss"], ["packetLoss"]) ?? PacketLoss;
            HostName = HistoryPayloadReader.FirstString(root, ["HostName"], ["hostName"]) ?? HostName;
            CloudflareColo = HistoryPayloadReader.FirstString(root, ["CloudflareColo"], ["cloudflareColo"]) ?? CloudflareColo;

            if (HistoryPayloadReader.FirstDouble(root, ["latencies", "chatMs"], ["latencies", "modelsMs"]) is { } latency)
            {
                AvgLatency = $"{latency:F0} ms";
            }
            if (HistoryPayloadReader.ReadDouble(root, "latencies", "ttftMs") is { } ttft)
            {
                Ttft = $"{ttft:F0} ms";
            }
            if (ReadThroughput(root) is { } throughput)
            {
                Throughput = $"{throughput:F1} tok/s";
            }
        }
    }

    private static void AddModelName(HashSet<string> models, string payloadJson)
    {
        if (!HistoryPayloadReader.TryParse(payloadJson, out var document))
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var path in new[] { "requestedModel", "effectiveModel", "Model", "model" })
            {
                var value = HistoryPayloadReader.ReadString(root, path);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    models.Add(value);
                }
            }

            foreach (var site in HistoryPayloadReader.ReadArray(root, "sites"))
            {
                var name = HistoryPayloadReader.FirstString(site, ["Model"], ["model"], ["Name"], ["name"]);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    models.Add(name);
                }
            }
        }
    }

    private void ApplyLatencyTrend(IReadOnlyList<HistoryReport> reports)
    {
        var values = reports
            .Select(ReadLatency)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .TakeLast(7)
            .ToArray();
        SetLatencyBars(ScaleBars(values, 120, 7));
        if (values.Length > 0)
        {
            AvgLatency = $"{values.Average():F0} ms";
        }
    }

    private void ApplyThroughputTrend(IReadOnlyList<HistoryReport> reports)
    {
        var values = reports
            .Select(ReadThroughput)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .TakeLast(4)
            .ToArray();
        SetThroughputBars(ScaleBars(values, 120, 4));
        if (values.Length > 0)
        {
            Throughput = $"{values.Average():F1} tok/s";
        }
    }

    private void ApplySuccessDistribution(IReadOnlyList<HistoryReport> reports)
    {
        if (reports.Count == 0)
        {
            SuccessOkPercent = 0;
            SuccessRetryPercent = 0;
            SuccessFailPercent = 0;
            return;
        }

        var scores = reports.Select(static report => report.Score ?? 0).ToArray();
        SuccessOkPercent = scores.Count(static score => score >= 80) * 100.0 / scores.Length;
        SuccessRetryPercent = scores.Count(static score => score is >= 60 and < 80) * 100.0 / scores.Length;
        SuccessFailPercent = scores.Count(static score => score < 60) * 100.0 / scores.Length;
    }

    partial void OnRiskDomainsChanged(int value)
    {
        RiskDomainObservationText = $"{Math.Max(0, value)} 个风险域名待复核";
    }

    private static double? ReadLatency(HistoryReport report)
    {
        if (!HistoryPayloadReader.TryParse(report.PayloadJson, out var document))
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var direct = HistoryPayloadReader.FirstDouble(root,
                ["latencies", "chatMs"],
                ["latencies", "modelsMs"],
                ["LatencyMs"],
                ["latencyMs"]);
            if (direct.HasValue)
            {
                return direct.Value;
            }

            var sites = HistoryPayloadReader.ReadArray(root, "sites");
            var values = sites
                .Select(static site => HistoryPayloadReader.FirstDouble(site, ["LatencyMs"], ["latencyMs"]))
                .Where(static value => value.HasValue)
                .Select(static value => value!.Value)
                .ToArray();
            return values.Length == 0 ? null : values.Average();
        }
    }

    private static double? ReadThroughput(HistoryReport report)
    {
        if (!HistoryPayloadReader.TryParse(report.PayloadJson, out var document))
        {
            return null;
        }

        using (document)
        {
            return ReadThroughput(document.RootElement);
        }
    }

    private static double? ReadThroughput(JsonElement root)
    {
        var direct = HistoryPayloadReader.FirstDouble(root,
            ["throughput", "MedianOutputTokensPerSecond"],
            ["throughput", "medianOutputTokensPerSecond"],
            ["Throughput"],
            ["throughput"]);
        if (direct.HasValue)
        {
            return direct.Value;
        }

        var sites = HistoryPayloadReader.ReadArray(root, "sites");
        var values = sites
            .Select(static site => HistoryPayloadReader.FirstDouble(site, ["Throughput"], ["throughput"]))
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double[] ScaleBars(IReadOnlyList<double> source, double maxHeight, int count)
    {
        if (source.Count == 0)
        {
            return Enumerable.Repeat(0d, count).ToArray();
        }

        var padded = Enumerable.Repeat(0d, Math.Max(0, count - source.Count))
            .Concat(source.TakeLast(count))
            .ToArray();
        var max = Math.Max(1, padded.Max());
        return padded.Select(value => Math.Clamp(value / max * maxHeight, 0, maxHeight)).ToArray();
    }

    private void SetLatencyBars(IReadOnlyList<double> values)
    {
        LatencyBar0 = values.ElementAtOrDefault(0);
        LatencyBar1 = values.ElementAtOrDefault(1);
        LatencyBar2 = values.ElementAtOrDefault(2);
        LatencyBar3 = values.ElementAtOrDefault(3);
        LatencyBar4 = values.ElementAtOrDefault(4);
        LatencyBar5 = values.ElementAtOrDefault(5);
        LatencyBar6 = values.ElementAtOrDefault(6);
    }

    private void SetThroughputBars(IReadOnlyList<double> values)
    {
        ThroughputBarA = values.ElementAtOrDefault(0);
        ThroughputBarB = values.ElementAtOrDefault(1);
        ThroughputBarC = values.ElementAtOrDefault(2);
        ThroughputBarD = values.ElementAtOrDefault(3);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }

    [RelayCommand]
    private async Task RunNetworkCheckAsync()
    {
        IsBusy = true;
        StatusText = "Running network diagnostics...";
        try
        {
            var result = await _networkService.RunAsync();
            PublicIp = result.PublicIp ?? "0";
            HostName = result.HostName;
            CloudflareColo = result.CloudflareColo ?? "0";
            AdapterCount = result.Adapters.Count;
            StatusText = $"Network check completed. Public IP: {PublicIp}";
        }
        catch (Exception ex)
        {
                StatusText = $"网络检查失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunQuickSuiteAsync()
    {
        IsBusy = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var completed = 0;
        var failed = 0;

        try
        {
            async Task RunStepAsync(string label, double progress, Func<Task> action)
            {
                StatusText = $"快速套件: {label}...";
                GlobalProgressService.Report(progress, label);
                try
                {
                    await action();
                    completed++;
                }
                catch (Exception ex)
                {
                    failed++;
                StatusText = $"{label} 失败：{ex.Message}";
                }
            }

            await RunStepAsync("Network diagnostics", 10, async () =>
            {
                var netResult = await _networkService.RunAsync();
                PublicIp = netResult.PublicIp ?? "0";
                HostName = netResult.HostName;
                CloudflareColo = netResult.CloudflareColo ?? "0";
                AdapterCount = netResult.Adapters.Count;
            });

            await RunStepAsync("Official API trace", 22, async () =>
            {
                var trace = await _apiTraceService.RunAsync();
                if (!string.IsNullOrWhiteSpace(trace.PublicIp))
                    PublicIp = trace.PublicIp;
                if (!string.IsNullOrWhiteSpace(trace.CloudflareColo))
                    CloudflareColo = trace.CloudflareColo;
                if (!trace.IsSupportedRegion)
                    RiskDomains++;
            });

            await RunStepAsync("STUN/NAT", 34, async () =>
            {
                await _stunService.ProbeAsync("stun.cloudflare.com", StunTransportProtocol.Udp);
            });

            await RunStepAsync("Interface review", 46, async () =>
            {
                var netResult = await _networkService.RunAsync();
                AdapterCount = netResult.Adapters.Count;
                HostName = netResult.HostName;
            });

            await RunStepAsync("Speed test", 58, async () =>
            {
                var profile = _speedTestService.GetProfiles().FirstOrDefault()?.Key ?? "quick";
                var speedResult = await _speedTestService.RunAsync(profile);
                if (speedResult.Error is null)
                {
                    DownloadSpeed = speedResult.DownloadBitsPerSecond.HasValue
                        ? $"{speedResult.DownloadBitsPerSecond.Value / 1_000_000:F1} Mbps"
                        : "0 Mbps";
                    UploadSpeed = speedResult.UploadBitsPerSecond.HasValue
                        ? $"{speedResult.UploadBitsPerSecond.Value / 1_000_000:F1} Mbps"
                        : "0 Mbps";
                    IdleLatency = speedResult.IdleLatencyMilliseconds.HasValue
                        ? $"{speedResult.IdleLatencyMilliseconds.Value:F0} ms"
                        : "0 ms";
                    PacketLoss = speedResult.PacketLossRatio.HasValue
                        ? $"{speedResult.PacketLossRatio.Value * 100:F1}%"
                        : "0%";
                    AvgLatency = IdleLatency;

                    if (speedResult.GptImpactScore > 0)
                        OverallScore = speedResult.GptImpactScore;
                }
                else
                {
                    throw new InvalidOperationException(speedResult.Error);
                }
            });

            await RunStepAsync("Route/MTR", 70, async () =>
            {
                var route = await _routeService.RunAsync("chatgpt.com", maxHops: 30, timeoutMilliseconds: 2000, samplesPerHop: 3);
                AvgLatency = route.Hops.LastOrDefault(static hop => hop.AverageRoundTripTime.HasValue)?.AverageRoundTripTime is { } avg
                    ? $"{avg:F0} ms"
                    : AvgLatency;
            });

            await RunStepAsync("Port scan", 82, async () =>
            {
                var profile = _portScanService.GetDefaultProfile().Key;
                await _portScanService.RunAsync("chatgpt.com", profile, null);
            });

            await RunStepAsync("IP and split routing", 94, async () =>
            {
                var split = await _splitRoutingService.RunAsync(null);
                var risk = await _ipRiskService.RunAsync();
                RiskDomains = split.MultiExitSuspected || split.DnsSplitSuspected
                    ? Math.Max(RiskDomains, 1)
                    : RiskDomains;
                RiskDomains += risk.RiskSignals.Count;
                if (!string.IsNullOrWhiteSpace(risk.PublicIp))
                    PublicIp = risk.PublicIp;
            });

            sw.Stop();
            StatusText = failed == 0
                ? $"快速套件 completed in {sw.Elapsed.TotalSeconds:F1}s"
            : $"快速套件完成，包含 {failed} 个警告，用时 {sw.Elapsed.TotalSeconds:F1}s";
            GlobalProgressService.Report(100, "Complete");
            GlobalProgressService.Complete();

            // Record to history
            _ = RunHistoryRecorder.RecordAsync(
                type: "快速套件",
                endpoint: "Dashboard",
            summary: $"{completed}/8 项检查完成，{failed} 项失败。IP：{PublicIp}，下载：{DownloadSpeed}",
                score: failed == 0 ? OverallScore : Math.Max(0, OverallScore - failed * 8),
                durationMs: (int)sw.ElapsedMilliseconds,
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    completed,
                    failed,
                    PublicIp,
                    DownloadSpeed,
                    UploadSpeed,
                    IdleLatency,
                    PacketLoss,
                    RiskDomains
                }));
        }
        catch (Exception ex)
        {
            StatusText = $"快速套件失败：{ex.Message}";
            GlobalProgressService.Complete();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunQuickTestAsync()
    {
        await RunQuickSuiteAsync();
    }

    [RelayCommand]
    private Task ExportReportAsync()
    {
        try
        {
            var exportRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RelayBench",
                "WinUI",
                "exports");
            Directory.CreateDirectory(exportRoot);

            var path = Path.Combine(exportRoot, $"dashboard-{DateTime.Now:yyyyMMdd-HHmmss}.md");
            File.WriteAllText(path, BuildDashboardMarkdown(), Encoding.UTF8);
            StatusText = $"Report exported: {path}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }

    private string BuildDashboardMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RelayBench Dashboard Snapshot");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- Overall score: {OverallScore:F1}");
        sb.AppendLine($"- Success: {SuccessRate}");
        sb.AppendLine($"- Average latency: {AvgLatency}");
        sb.AppendLine($"- TTFT: {Ttft}");
        sb.AppendLine($"- Throughput: {Throughput}");
        sb.AppendLine($"- Risk domains: {RiskDomains}");
        sb.AppendLine();
        sb.AppendLine("## Network");
        sb.AppendLine();
        sb.AppendLine($"- Public IP: {PublicIp}");
        sb.AppendLine($"- Host name: {HostName}");
        sb.AppendLine($"- Cloudflare colo: {CloudflareColo}");
        sb.AppendLine($"- Adapter count: {AdapterCount}");
        sb.AppendLine();
        sb.AppendLine("## Speed Test");
        sb.AppendLine();
        sb.AppendLine($"- Download: {DownloadSpeed}");
        sb.AppendLine($"- Upload: {UploadSpeed}");
        sb.AppendLine($"- Idle latency: {IdleLatency}");
        sb.AppendLine($"- Packet loss: {PacketLoss}");
        sb.AppendLine();
        sb.AppendLine("## Status");
        sb.AppendLine();
        sb.AppendLine(StatusText);
        return sb.ToString();
    }
}
