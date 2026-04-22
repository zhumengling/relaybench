using NetTest.App.Infrastructure;
using NetTest.App.Services;
using NetTest.Core.Models;
using System.Text.RegularExpressions;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly Regex RouteLiveHopPreviewRegex = new(
        @"^#\s*(?<hop>\d+)\s+(?<address>\S+)\s{2,}trace=(?<trace>.+?)(?:\s{2,}.+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private PortScanProfile? GetSelectedPortScanProfile()
        => PortScanProfiles.FirstOrDefault(profile => string.Equals(profile.Key, SelectedPortScanProfileKey, StringComparison.OrdinalIgnoreCase));

    private SelectionOption? GetSelectedRouteResolverOption()
        => RouteResolverOptions.FirstOrDefault(option => string.Equals(option.Key, SelectedRouteResolverKey, StringComparison.OrdinalIgnoreCase));

    private string ResolvePortScanProfileKey(string? requestedKey)
    {
        var matchedProfile = PortScanProfiles.FirstOrDefault(profile => string.Equals(profile.Key, requestedKey, StringComparison.OrdinalIgnoreCase));
        return matchedProfile?.Key ?? _portScanDiagnosticsService.GetDefaultProfile().Key;
    }

    private string ResolvePortScanProtocolFilterKey(string? requestedKey)
    {
        var matchedOption = PortScanProtocolFilterOptions.FirstOrDefault(option => string.Equals(option.Key, requestedKey, StringComparison.OrdinalIgnoreCase));
        return matchedOption?.Key ?? "all";
    }

    private string ResolveRouteResolverKey(string? requestedKey)
    {
        var matchedOption = RouteResolverOptions.FirstOrDefault(option => string.Equals(option.Key, requestedKey, StringComparison.OrdinalIgnoreCase));
        return matchedOption?.Key ?? "auto";
    }

    private bool CanStopRouteContinuous()
        => _isRouteContinuousExecutionActive &&
           _routeContinuousCancellationSource is { IsCancellationRequested: false };

    private void RefreshRouteContinuousCommandStates()
        => StopRouteContinuousCommand?.RaiseCanExecuteChanged();

    private static string FormatTraceSamples(IReadOnlyList<long?> values)
        => string.Join("/", values.Select(FormatLongMilliseconds));

    private static string FormatLoss(double? value)
        => value?.ToString("F1") + "%" ?? "--";

    private static string FormatLongMilliseconds(long? value)
        => value?.ToString() + " ms" ?? "*";

    private static string FormatAverageMilliseconds(double? value)
        => value?.ToString("F1") + " ms" ?? "--";

    private void PrepareRouteLiveExecution(
        bool isContinuousMode = false,
        int currentRound = 1,
        int plannedDurationSeconds = 0,
        DateTimeOffset? continuousEndsAt = null)
    {
        var shouldResetLiveBuffers = !isContinuousMode || currentRound <= 1;
        if (shouldResetLiveBuffers)
        {
            _routeLiveHopPreviewLines.Clear();
            _routeLiveStartedAt = DateTimeOffset.Now;
            _routeLiveRawLineCount = 0;
            _routeLiveEngineHint = "等待内置 ICMP MTR / tracert";
        }

        _isRouteLiveExecutionActive = true;
        _routeLiveStartedAt ??= DateTimeOffset.Now;
        _routeLiveLastProgressMessage = isContinuousMode && currentRound > 1
            ? $"第 {currentRound} 轮准备启动路由探测"
            : "准备启动路由探测";
        _isRouteContinuousExecutionActive = isContinuousMode;
        _routeContinuousCurrentRound = isContinuousMode ? Math.Max(1, currentRound) : 0;
        _routeContinuousPlannedDurationSeconds = isContinuousMode ? Math.Max(0, plannedDurationSeconds) : 0;
        _routeContinuousEndsAt = isContinuousMode ? continuousEndsAt : null;
        RefreshRouteContinuousCommandStates();
        RefreshRouteLiveSummary();
        UpdateGlobalTaskProgress(
            isContinuousMode ? $"\u7B2C {Math.Max(1, currentRound)} \u8F6E" : "\u51C6\u5907\u4E2D",
            isContinuousMode ? 10d : 8d);
        if (!isContinuousMode)
        {
            RouteHopSummary = "正在等待逐跳反馈...";
            RouteRawOutput = "正在等待原始输出...";
            RouteMapImage = null;
            RouteGeoSummary = "路由执行中，结束后汇总 hop 地理定位。";
        }
        else
        {
            if (shouldResetLiveBuffers)
            {
                RouteHopSummary = "持续运行中，等待第一轮 hop 刷新...";
                RouteRawOutput = "持续运行中，等待第一轮原始输出...";
            }
            else
            {
                RouteRawOutput = IsRouteRawOutputPlaceholder(RouteRawOutput)
                    ? "持续运行中，等待第一轮原始输出..."
                    : RouteRawOutput + Environment.NewLine + $"--- 第 {_routeContinuousCurrentRound} 轮开始刷新 ---";
            }
        }

        RouteMapSummary = isContinuousMode
            ? "持续运行中，保留上一轮结果并在本轮结束后刷新地理路径图。"
            : "路由执行中，结束后生成地理路径图。";
    }

    private void HandleRouteProgressMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        UpdateGlobalTaskProgressForRouteMessage(message);

        if (!_isRouteLiveExecutionActive)
        {
            StatusMessage = message;
            return;
        }

        if (message.StartsWith(RouteDiagnosticsProgressTokens.RawLinePrefix, StringComparison.Ordinal))
        {
            AppendRouteLiveRawLine(message[RouteDiagnosticsProgressTokens.RawLinePrefix.Length..]);
            return;
        }

        if (message.StartsWith(RouteDiagnosticsProgressTokens.HopPreviewPrefix, StringComparison.Ordinal))
        {
            var preview = message[RouteDiagnosticsProgressTokens.HopPreviewPrefix.Length..].Trim();
            UpdateRouteLiveHopPreview(preview);
            _routeLiveLastProgressMessage = $"实时 hop：{preview}";
            RefreshRouteLiveSummary();
            StatusMessage = $"路由实时更新：{preview}";
            return;
        }

        UpdateRouteLiveEngineHint(message);
        _routeLiveLastProgressMessage = message.Trim();
        RefreshRouteLiveSummary();
        StatusMessage = message;
    }

    private void AppendRouteLiveRawLine(string? rawLine)
    {
        var normalized = rawLine?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var existing = IsRouteRawOutputPlaceholder(RouteRawOutput)
            ? string.Empty
            : RouteRawOutput;
        var combined = string.IsNullOrWhiteSpace(existing)
            ? normalized
            : existing + Environment.NewLine + normalized;

        if (combined.Length > MaxRouteLiveRawCharacters)
        {
            combined = combined[^MaxRouteLiveRawCharacters..];
            var lineBreakIndex = combined.IndexOf(Environment.NewLine, StringComparison.Ordinal);
            if (lineBreakIndex >= 0 && lineBreakIndex < combined.Length - Environment.NewLine.Length)
            {
                combined = combined[(lineBreakIndex + Environment.NewLine.Length)..];
            }
        }

        _routeLiveRawLineCount++;
        RouteRawOutput = combined;
        RefreshRouteLiveSummary();
    }

    private void UpdateRouteLiveHopPreview(string preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return;
        }

        UpdateGlobalTaskProgressForRouteHopPreview(preview);

        var hopNumber = TryParseRouteLiveHopNumber(preview);
        if (hopNumber.HasValue)
        {
            _routeLiveHopPreviewLines[hopNumber.Value] = preview;
        }
        else
        {
            _routeLiveHopPreviewLines[_routeLiveHopPreviewLines.Count + 1_000] = preview;
        }

        RouteHopSummary = _isRouteContinuousExecutionActive
            ? BuildContinuousRouteHopSummary(useLivePreviewOverlay: true)
            : string.Join(
                Environment.NewLine,
                _routeLiveHopPreviewLines
                    .OrderBy(static pair => pair.Key)
                    .Select(static pair => pair.Value));
        RefreshRouteLiveSummary();
    }

    private static int? TryParseRouteLiveHopNumber(string preview)
    {
        if (!preview.StartsWith('#'))
        {
            return null;
        }

        var separatorIndex = preview.IndexOf(' ');
        var token = separatorIndex > 1
            ? preview[1..separatorIndex]
            : preview[1..];

        return int.TryParse(token, out var hopNumber) ? hopNumber : null;
    }

    private void RefreshRouteLiveSummary()
    {
        var startedAt = _routeLiveStartedAt ?? DateTimeOffset.Now;
        var elapsed = DateTimeOffset.Now - startedAt;
        var continuousSummary = _isRouteContinuousExecutionActive
            ? BuildRouteContinuousLiveSummary()
            : string.Empty;

        RouteSummary =
            "执行状态：运行中\n" +
            $"开始时间：{startedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"目标：{RouteTarget}\n" +
            "探测模式：仅内置 ICMP MTR / Windows tracert\n" +
            $"解析器：{SelectedRouteResolverDescription}\n" +
            $"计划最大跳数：{RouteMaxHopsText}\n" +
            $"计划超时：{RouteTimeoutMsText} ms\n" +
            $"计划每跳采样：{RouteSamplesPerHopText}\n" +
            continuousSummary +
            $"当前引擎：{_routeLiveEngineHint}\n" +
            $"已见 hop：{_routeLiveHopPreviewLines.Count}\n" +
            $"原始输出行数：{_routeLiveRawLineCount}\n" +
            $"已运行：{elapsed:mm\\:ss}\n" +
            $"最新进度：{_routeLiveLastProgressMessage}";
    }

    private string BuildRouteContinuousLiveSummary()
    {
        if (!_isRouteContinuousExecutionActive)
        {
            return string.Empty;
        }

        var remaining = _routeContinuousEndsAt.HasValue
            ? _routeContinuousEndsAt.Value - DateTimeOffset.Now
            : TimeSpan.Zero;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return
            $"运行模式：持续运行\n" +
            $"当前轮次：第 {_routeContinuousCurrentRound} 轮\n" +
            $"计划总时长：{_routeContinuousPlannedDurationSeconds} 秒\n" +
            $"轮间隔：{RouteContinuousIntervalMsText} ms\n" +
            $"剩余时间：{remaining:mm\\:ss}\n";
    }

    private void ResetRouteContinuousLiveState()
    {
        _isRouteContinuousExecutionActive = false;
        _routeContinuousCurrentRound = 0;
        _routeContinuousPlannedDurationSeconds = 0;
        _routeContinuousEndsAt = null;
        _routeContinuousCancellationSource?.Dispose();
        _routeContinuousCancellationSource = null;
        RefreshRouteContinuousCommandStates();
    }

    private void BeginContinuousRouteAggregation()
    {
        _routeContinuousHopAggregates.Clear();
    }

    private void AccumulateContinuousRouteResult(RouteDiagnosticsResult result)
    {
        foreach (var hop in result.Hops.OrderBy(static hop => hop.HopNumber))
        {
            if (!_routeContinuousHopAggregates.TryGetValue(hop.HopNumber, out var aggregate))
            {
                aggregate = new RouteContinuousHopAggregate(hop.HopNumber);
                _routeContinuousHopAggregates[hop.HopNumber] = aggregate;
            }

            aggregate.Observe(hop);
        }
    }

    private string BuildContinuousRouteHopSummary(bool useLivePreviewOverlay = false)
    {
        if (_routeContinuousHopAggregates.Count == 0)
        {
            return _routeLiveHopPreviewLines.Count == 0
                ? "持续运行中，等待累计 hop 统计..."
                : string.Join(
                    Environment.NewLine,
                    _routeLiveHopPreviewLines
                        .OrderBy(static pair => pair.Key)
                        .Select(static pair => pair.Value));
        }

        var livePreviewLookup = useLivePreviewOverlay
            ? _routeLiveHopPreviewLines.Values
                .Select(TryParseRouteLiveHopPreview)
                .Where(static preview => preview is not null)
                .Cast<RouteLiveHopPreview>()
                .GroupBy(static preview => preview.HopNumber)
                .ToDictionary(static group => group.Key, static group => group.Last())
            : new Dictionary<int, RouteLiveHopPreview>();

        var hopNumbers = _routeContinuousHopAggregates.Keys
            .Union(livePreviewLookup.Keys)
            .OrderBy(static hopNumber => hopNumber);

        return string.Join(
            Environment.NewLine,
            hopNumbers.Select(hopNumber =>
            {
                if (!_routeContinuousHopAggregates.TryGetValue(hopNumber, out var aggregate))
                {
                    return livePreviewLookup.TryGetValue(hopNumber, out var preview)
                        ? preview.RawLine
                        : $"#{hopNumber,2} *  持续运行中，等待该跳累计数据...";
                }

                var metadataHop = aggregate.LastHop;
                livePreviewLookup.TryGetValue(hopNumber, out var livePreview);
                var displayAddress = livePreview is { Address: not "*" } ? livePreview.Address : aggregate.DisplayAddress;
                var traceSamples = livePreview?.TraceSamplesText ?? FormatTraceSamples(aggregate.LastTraceRoundTripTimes);

                return
                    $"#{aggregate.HopNumber,2} {displayAddress}  " +
                    $"最近trace={traceSamples}  " +
                    $"轮次={aggregate.RoundCount}  " +
                    $"发送={aggregate.TotalSentProbes} 接收={aggregate.TotalReceivedResponses}  " +
                    $"丢包={FormatLoss(aggregate.LossPercent)}  " +
                    $"最佳={FormatLongMilliseconds(aggregate.BestRoundTripTime)}  " +
                    $"平均={FormatAverageMilliseconds(aggregate.AverageRoundTripTime)}  " +
                    $"最差={FormatLongMilliseconds(aggregate.WorstRoundTripTime)}" +
                    (metadataHop is null ? string.Empty : FormatHopMetadata(metadataHop));
            }));
    }

    private void MarkContinuousRoundCompleted(RouteDiagnosticsResult result, RouteMapRenderResult mapResult, int intervalMilliseconds)
    {
        if (!_isRouteContinuousExecutionActive)
        {
            return;
        }

        var remaining = _routeContinuousEndsAt.HasValue
            ? _routeContinuousEndsAt.Value - DateTimeOffset.Now
            : TimeSpan.Zero;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        AccumulateContinuousRouteResult(result);
        RouteHopSummary = BuildContinuousRouteHopSummary(useLivePreviewOverlay: true);

        var highestLossHop = _routeContinuousHopAggregates.Values
            .Where(static aggregate => aggregate.LossPercent.HasValue)
            .OrderByDescending(static aggregate => aggregate.LossPercent)
            .ThenByDescending(static aggregate => aggregate.HopNumber)
            .FirstOrDefault();
        var slowestHop = _routeContinuousHopAggregates.Values
            .Where(static aggregate => aggregate.AverageRoundTripTime.HasValue)
            .OrderByDescending(static aggregate => aggregate.AverageRoundTripTime)
            .ThenByDescending(static aggregate => aggregate.HopNumber)
            .FirstOrDefault();

        var cumulativeLossSummary = highestLossHop?.LossPercent is null
            ? "累计丢包：暂无可用数据。"
            : $"累计最高丢包：第 {highestLossHop.HopNumber} 跳 {highestLossHop.DisplayAddress}，丢包率 {highestLossHop.LossPercent:F1}%。";
        var cumulativeLatencySummary = slowestHop?.AverageRoundTripTime is null
            ? "累计平均延迟：暂无可用数据。"
            : $"累计最高平均延迟：第 {slowestHop.HopNumber} 跳 {slowestHop.DisplayAddress}，平均延迟 {slowestHop.AverageRoundTripTime:F1} ms。";

        RouteSummary =
            $"执行状态：持续运行中\n" +
            $"当前轮次：第 {_routeContinuousCurrentRound} 轮已完成\n" +
            $"剩余时间：{remaining:mm\\:ss}\n" +
            $"轮间隔：{intervalMilliseconds} ms\n" +
            $"累计 hop：{_routeContinuousHopAggregates.Count} 个\n" +
            $"最后一轮是否到达目标：{(result.TraceCompleted ? "是" : "否")}\n" +
            $"最后一轮摘要：{result.Summary}\n" +
            $"{cumulativeLossSummary}\n" +
            $"{cumulativeLatencySummary}";

        RouteMapSummary =
            $"持续运行中：第 {_routeContinuousCurrentRound} 轮已完成，剩余 {remaining:mm\\:ss}。\n" +
            $"{RouteMapSummary}";
    }

    private sealed class RouteContinuousHopAggregate
    {
        private double _weightedLatencySum;
        private int _weightedLatencyCount;

        public RouteContinuousHopAggregate(int hopNumber)
        {
            HopNumber = hopNumber;
            LastTraceRoundTripTimes = [];
        }

        public int HopNumber { get; }

        public int RoundCount { get; private set; }

        public int TotalSentProbes { get; private set; }

        public int TotalReceivedResponses { get; private set; }

        public long? BestRoundTripTime { get; private set; }

        public long? WorstRoundTripTime { get; private set; }

        public IReadOnlyList<long?> LastTraceRoundTripTimes { get; private set; }

        public RouteHopResult? LastHop { get; private set; }

        public string DisplayAddress => string.IsNullOrWhiteSpace(LastHop?.Address) ? "未解析" : LastHop.Address!;

        public double? LossPercent
            => TotalSentProbes == 0 ? null : (TotalSentProbes - TotalReceivedResponses) * 100d / TotalSentProbes;

        public double? AverageRoundTripTime
            => _weightedLatencyCount == 0 ? null : _weightedLatencySum / _weightedLatencyCount;

        public void Observe(RouteHopResult hop)
        {
            RoundCount++;
            TotalSentProbes += hop.SentProbes;
            TotalReceivedResponses += hop.ReceivedResponses;
            LastTraceRoundTripTimes = hop.TraceRoundTripTimes.ToArray();

            if (hop.BestRoundTripTime.HasValue)
            {
                BestRoundTripTime = !BestRoundTripTime.HasValue
                    ? hop.BestRoundTripTime
                    : Math.Min(BestRoundTripTime.Value, hop.BestRoundTripTime.Value);
            }

            if (hop.WorstRoundTripTime.HasValue)
            {
                WorstRoundTripTime = !WorstRoundTripTime.HasValue
                    ? hop.WorstRoundTripTime
                    : Math.Max(WorstRoundTripTime.Value, hop.WorstRoundTripTime.Value);
            }

            if (hop.AverageRoundTripTime.HasValue && hop.ReceivedResponses > 0)
            {
                _weightedLatencySum += hop.AverageRoundTripTime.Value * hop.ReceivedResponses;
                _weightedLatencyCount += hop.ReceivedResponses;
            }

            LastHop = MergeHop(LastHop, hop);
        }

        private static RouteHopResult MergeHop(RouteHopResult? previous, RouteHopResult current)
        {
            if (previous is null)
            {
                return current;
            }

            return current with
            {
                Address = !string.IsNullOrWhiteSpace(current.Address) ? current.Address : previous.Address,
                Hostname = !string.IsNullOrWhiteSpace(current.Hostname) ? current.Hostname : previous.Hostname,
                AutonomousSystem = !string.IsNullOrWhiteSpace(current.AutonomousSystem) ? current.AutonomousSystem : previous.AutonomousSystem,
                Country = !string.IsNullOrWhiteSpace(current.Country) ? current.Country : previous.Country,
                Region = !string.IsNullOrWhiteSpace(current.Region) ? current.Region : previous.Region,
                City = !string.IsNullOrWhiteSpace(current.City) ? current.City : previous.City,
                District = !string.IsNullOrWhiteSpace(current.District) ? current.District : previous.District,
                Organization = !string.IsNullOrWhiteSpace(current.Organization) ? current.Organization : previous.Organization,
                Latitude = current.Latitude ?? previous.Latitude,
                Longitude = current.Longitude ?? previous.Longitude
            };
        }
    }

    private static RouteLiveHopPreview? TryParseRouteLiveHopPreview(string preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return null;
        }

        var match = RouteLiveHopPreviewRegex.Match(preview.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["hop"].Value, out var hopNumber))
        {
            return null;
        }

        return new RouteLiveHopPreview(
            hopNumber,
            match.Groups["address"].Value,
            match.Groups["trace"].Value.Trim(),
            preview.Trim());
    }

    private sealed record RouteLiveHopPreview(
        int HopNumber,
        string Address,
        string TraceSamplesText,
        string RawLine);

    private static bool IsRouteRawOutputPlaceholder(string? value)
        => string.Equals(value, "正在等待原始输出...", StringComparison.Ordinal) ||
           string.Equals(value, "暂无原始追踪输出。", StringComparison.Ordinal) ||
           string.Equals(value, "持续运行中，等待第一轮原始输出...", StringComparison.Ordinal);

    private void UpdateRouteLiveEngineHint(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (message.Contains("内置 ICMP MTR", StringComparison.OrdinalIgnoreCase))
        {
            _routeLiveEngineHint = "内置 ICMP MTR";
            return;
        }

        if (message.Contains("Windows tracert", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("tracert", StringComparison.OrdinalIgnoreCase))
        {
            _routeLiveEngineHint = "Windows tracert + Ping";
        }
    }

    private static string FormatHopMetadata(RouteHopResult hop)
    {
        if (!hop.HasTraceMetadata)
        {
            return string.Empty;
        }

        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(hop.NetworkLabel) && hop.NetworkLabel != "--")
        {
            sections.Add($"归属={hop.NetworkLabel}");
        }

        if (!string.IsNullOrWhiteSpace(hop.LocationLabel) && hop.LocationLabel != "--")
        {
            sections.Add($"位置={hop.LocationLabel}");
        }

        if (hop.HasCoordinates)
        {
            sections.Add($"坐标={hop.Latitude:F4}, {hop.Longitude:F4}");
        }

        return sections.Count == 0
            ? string.Empty
            : "\n    " + string.Join("  ", sections);
    }

    private void BeginPortScanLiveExecution(string? executionTarget = null)
    {
        _portScanCurrentExecutionTarget = string.IsNullOrWhiteSpace(executionTarget)
            ? (string.IsNullOrWhiteSpace(PortScanTarget) ? string.Empty : PortScanTarget.Trim())
            : executionTarget.Trim();
        _lastPortScanResult = null;
        _portScanLiveFindingKeys.Clear();
        PortScanFindings.Clear();
        FilteredPortScanFindings.Clear();
        PortScanProgressValue = 0d;
        PortScanProgressMaximum = 1d;
        PortScanProgressSummary = "准备开始扫描...";
        PortScanSummary =
            "执行状态：运行中\n" +
            $"目标：{GetCurrentPortScanExecutionTarget()}\n" +
            $"模板：{GetSelectedPortScanProfile()?.DisplayName ?? SelectedPortScanProfileKey}\n" +
            $"端口：{(string.IsNullOrWhiteSpace(PortScanCustomPortsText) ? "使用模板默认端口" : PortScanCustomPortsText.Trim())}";
        PortScanDetail = "等待解析目标与实时结果...";
        PortScanRawOutput = "准备启动扫描...";
        PortScanFilterSummary = "当前显示 0 / 0 条结果。";
        RefreshPortScanExportSummary();
        RefreshPortScanExportCommands();
        DashboardCards[6].Status = "扫描中";
        DashboardCards[6].Detail = $"{GetCurrentPortScanExecutionTarget()} / {GetSelectedPortScanProfile()?.DisplayName ?? SelectedPortScanProfileKey}";
        UpdateGlobalTaskProgress("\u51C6\u5907\u4E2D", 10d);
    }

    private void HandlePortScanProgressUpdate(PortScanProgressUpdate update)
    {
        var total = Math.Max(update.TotalEndpointCount, 1);
        var currentEndpoint = string.IsNullOrWhiteSpace(update.CurrentEndpoint) ? "--" : update.CurrentEndpoint;

        PortScanProgressMaximum = total;
        PortScanProgressValue = Math.Min(update.CompletedEndpointCount, total);
        PortScanProgressSummary = $"进度 {update.CompletedEndpointCount}/{total}，开放 {update.OpenEndpointCount}，当前 {currentEndpoint}";
        UpdateGlobalTaskProgressForPortScanUpdate(update);
        PortScanSummary =
            "执行状态：运行中\n" +
            $"目标：{GetCurrentPortScanExecutionTarget()}\n" +
            $"模板：{GetSelectedPortScanProfile()?.DisplayName ?? SelectedPortScanProfileKey}\n" +
            $"进度：{update.CompletedEndpointCount}/{total}\n" +
            $"开放端点：{update.OpenEndpointCount}\n" +
            $"当前：{currentEndpoint}";

        if (update.Finding is not null)
        {
            AddPortScanFindingIfNeeded(update.Finding);
        }

        if (!string.IsNullOrWhiteSpace(update.Message))
        {
            AppendPortScanLog(update.Timestamp, update.Message);
            DashboardCards[6].Detail = update.Message;
            StatusMessage = update.Message;
        }
        else if (update.Finding is not null)
        {
            var fallbackMessage = $"发现开放端点：{update.Finding.Endpoint}/{update.Finding.Protocol}";
            AppendPortScanLog(update.Timestamp, fallbackMessage);
            DashboardCards[6].Detail = fallbackMessage;
            StatusMessage = fallbackMessage;
        }

        PortScanDetail = BuildLivePortScanDetail();
    }

    private void AddPortScanFindingIfNeeded(PortScanFinding finding)
    {
        var key = BuildPortScanFindingKey(finding);
        if (!_portScanLiveFindingKeys.Add(key))
        {
            return;
        }

        PortScanFindings.Add(finding);
        SortPortScanFindings();
        RefreshFilteredPortScanFindings();
        RefreshPortScanExportSummary();
        RefreshPortScanExportCommands();
    }

    private static string BuildPortScanFindingKey(PortScanFinding finding)
        => $"{finding.Address.ToLowerInvariant()}|{finding.Port}|{finding.Protocol.ToLowerInvariant()}";

    private void SortPortScanFindings()
    {
        if (PortScanFindings.Count <= 1)
        {
            return;
        }

        var ordered = PortScanFindings
            .OrderBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Port)
            .ThenBy(item => item.Protocol, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            if (ReferenceEquals(PortScanFindings[index], ordered[index]))
            {
                continue;
            }

            var currentIndex = PortScanFindings.IndexOf(ordered[index]);
            if (currentIndex >= 0)
            {
                PortScanFindings.Move(currentIndex, index);
            }
        }
    }

    private string BuildLivePortScanDetail()
    {
        if (PortScanFindings.Count == 0)
        {
            return "实时结果：暂无开放端点。";
        }

        var preview = PortScanFindings
            .Take(12)
            .Select(FormatPortScanFinding);

        var detail = string.Join(Environment.NewLine + Environment.NewLine, preview);
        if (PortScanFindings.Count > 12)
        {
            detail += Environment.NewLine + Environment.NewLine + $"... 其余 {PortScanFindings.Count - 12} 条结果请查看下方表格。";
        }

        return detail;
    }

    private void AppendPortScanLog(DateTimeOffset timestamp, string? message)
    {
        var normalized = message?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var line = $"[{timestamp:HH:mm:ss}] {normalized}";
        var existing = string.Equals(PortScanRawOutput, "准备启动扫描...", StringComparison.Ordinal)
            ? string.Empty
            : PortScanRawOutput;
        var combined = string.IsNullOrWhiteSpace(existing)
            ? line
            : existing + Environment.NewLine + line;

        if (combined.Length > MaxPortScanLogCharacters)
        {
            combined = combined[^MaxPortScanLogCharacters..];
            var lineBreakIndex = combined.IndexOf(Environment.NewLine, StringComparison.Ordinal);
            if (lineBreakIndex >= 0 && lineBreakIndex < combined.Length - Environment.NewLine.Length)
            {
                combined = combined[(lineBreakIndex + Environment.NewLine.Length)..];
            }
        }

        PortScanRawOutput = combined;
    }

    private string GetCurrentPortScanExecutionTarget()
        => string.IsNullOrWhiteSpace(_portScanCurrentExecutionTarget)
            ? (string.IsNullOrWhiteSpace(PortScanTarget) ? "--" : PortScanTarget.Trim())
            : _portScanCurrentExecutionTarget;

    private IReadOnlyList<string> GetPendingPortScanBatchTargets()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> targets = [];

        var normalizedText = (PortScanBatchTargetsText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        foreach (var candidate in normalizedText.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var target = candidate.Trim();
            if (string.IsNullOrWhiteSpace(target) || !seen.Add(target))
            {
                continue;
            }

            targets.Add(target);
        }

        return targets;
    }

    private int GetPortScanBatchConcurrency()
        => ParseBoundedInt(PortScanBatchConcurrencyText, fallback: 3, min: 1, max: 8);

    private bool CanExportPortScanResults()
        => !IsBusy && (_lastPortScanResult is not null || _lastPortScanBatchResults.Count > 0 || PortScanFindings.Count > 0 || PortScanBatchRows.Count > 0);

    private void RefreshPortScanExportCommands()
    {
        ExportPortScanCsvCommand?.RaiseCanExecuteChanged();
        ExportPortScanExcelCommand?.RaiseCanExecuteChanged();
    }

    private void RefreshPortScanBatchSummary()
    {
        var pendingTargets = GetPendingPortScanBatchTargets();
        if (PortScanBatchRows.Count == 0)
        {
            PortScanBatchSummary = pendingTargets.Count == 0
                ? "可在这里粘贴多个目标，逐行执行批量端口扫描。"
                : $"已载入 {pendingTargets.Count} 个目标，等待开始批量扫描；批量并发 {GetPortScanBatchConcurrency()}。";
            return;
        }

        var completed = PortScanBatchRows.Count(row => row.Status is not "待运行" and not "扫描中");
        var running = PortScanBatchRows.Count(row => row.Status == "扫描中");
        var reviewOrFailed = PortScanBatchRows.Count(row => row.Status is "失败" or "需复核" or "异常");
        var openEndpoints = PortScanBatchRows.Sum(row => row.OpenEndpointCount);
        var openPorts = PortScanBatchRows.Sum(row => row.OpenPortCount);
        var selectedTarget = SelectedPortScanBatchRow?.Target;

        PortScanBatchSummary =
            $"批量目标 {PortScanBatchRows.Count} 个，完成 {completed} 个，运行中 {running} 个，批量并发 {GetPortScanBatchConcurrency()}，发现开放端点 {openEndpoints} 个，覆盖开放端口 {openPorts} 个，异常/复核 {reviewOrFailed} 个{(string.IsNullOrWhiteSpace(selectedTarget) ? string.Empty : $"；当前查看 {selectedTarget}")}。";
    }

    private void RefreshPortScanExportSummary()
    {
        var currentCount = _lastPortScanResult?.Findings.Count ?? PortScanFindings.Count;
        var batchTargetCount = PortScanBatchRows.Count;
        var batchFindingCount = _lastPortScanBatchResults.Sum(static result => result.Findings.Count);

        PortScanExportSummary = currentCount == 0 && batchTargetCount == 0 && batchFindingCount == 0
            ? "暂无可导出结果。"
            : $"可导出当前 {currentCount} 条结果、批量 {batchTargetCount} 个目标（明细 {batchFindingCount} 条）。";
    }

    private void RefreshFilteredPortScanFindings()
    {
        var keyword = PortScanSearchText?.Trim() ?? string.Empty;
        var protocolFilter = ResolvePortScanProtocolFilterKey(SelectedPortScanProtocolFilterKey);

        var filtered = PortScanFindings
            .Where(finding => MatchesPortScanFilter(finding, keyword, protocolFilter))
            .OrderBy(finding => finding.Address, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Port)
            .ThenBy(finding => finding.Protocol, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        FilteredPortScanFindings.Clear();
        foreach (var finding in filtered)
        {
            FilteredPortScanFindings.Add(finding);
        }

        var protocolText = protocolFilter switch
        {
            "tcp" => "仅 TCP",
            "udp" => "仅 UDP",
            _ => "全部协议"
        };

        PortScanFilterSummary = string.IsNullOrWhiteSpace(keyword)
            ? $"当前显示 {FilteredPortScanFindings.Count} / {PortScanFindings.Count} 条结果；协议筛选：{protocolText}。"
            : $"当前显示 {FilteredPortScanFindings.Count} / {PortScanFindings.Count} 条结果；协议筛选：{protocolText}；关键词：{keyword}。";
    }

    private static bool MatchesPortScanFilter(PortScanFinding finding, string keyword, string protocolFilter)
    {
        if (!string.Equals(protocolFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(finding.Protocol, protocolFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        return (finding.Endpoint?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (finding.Address?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (finding.ServiceHint?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (finding.Banner?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (finding.TlsSummary?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (finding.HttpSummary?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (finding.ProbeNotes?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private PortScanExportSnapshot BuildPortScanExportSnapshot()
    {
        var currentResult = _lastPortScanResult;
        var currentTarget = currentResult?.Target;
        if (string.IsNullOrWhiteSpace(currentTarget))
        {
            currentTarget = !string.IsNullOrWhiteSpace(PortScanTarget)
                ? PortScanTarget.Trim()
                : PortScanBatchRows.FirstOrDefault()?.Target;
        }

        currentTarget = string.IsNullOrWhiteSpace(currentTarget) ? "port-scan" : currentTarget.Trim();

        var currentFindings = (currentResult?.Findings ?? PortScanFindings.ToArray())
            .Select(finding => BuildPortScanExportFindingRow("current", currentTarget, finding))
            .ToArray();

        var batchResults = _lastPortScanBatchResults
            .Where(result => currentResult is null || !EqualityComparer<PortScanResult>.Default.Equals(result, currentResult))
            .ToArray();

        var batchRows = PortScanBatchRows
            .Select(static row => new PortScanExportBatchRow(
                row.Target,
                row.Status,
                row.OpenEndpointCount,
                row.OpenPortCount,
                row.ResolvedAddresses,
                row.Summary,
                row.Error,
                row.CheckedAt))
            .ToArray();

        var batchFindings = batchResults
            .SelectMany(result => result.Findings.Select(finding => BuildPortScanExportFindingRow("batch", result.Target, finding)))
            .ToArray();

        var profile = GetSelectedPortScanProfile();
        var selectedProtocolText = PortScanProtocolFilterOptions
            .FirstOrDefault(option => string.Equals(option.Key, SelectedPortScanProtocolFilterKey, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? "全部协议";

        var summaryRows = new List<PortScanExportSummaryRow>
        {
            new("Target", currentTarget),
            new("Profile", profile is null ? SelectedPortScanProfileKey : $"{profile.DisplayName} ({profile.Key})"),
            new("Current Findings", currentFindings.Length.ToString()),
            new("Filtered View Count", FilteredPortScanFindings.Count.ToString()),
            new("Batch Targets", batchRows.Length.ToString()),
            new("Batch Findings", batchFindings.Length.ToString()),
            new("Batch Concurrency", GetPortScanBatchConcurrency().ToString()),
            new("Search Filter", string.IsNullOrWhiteSpace(PortScanSearchText) ? "(none)" : PortScanSearchText.Trim()),
            new("Protocol Filter", selectedProtocolText),
            new("Batch Summary", PortScanBatchSummary),
            new("Export Summary", PortScanExportSummary)
        };

        return new PortScanExportSnapshot(
            SuggestedName: currentTarget,
            ExportedAt: DateTimeOffset.Now,
            TargetLabel: currentTarget,
            SummaryRows: summaryRows,
            CurrentFindings: currentFindings,
            BatchRows: batchRows,
            BatchFindings: batchFindings);
    }

    private static PortScanExportFindingRow BuildPortScanExportFindingRow(string scope, string target, PortScanFinding finding)
        => new(
            scope,
            target,
            finding.Endpoint,
            finding.Address,
            finding.Port,
            finding.Protocol,
            finding.ConnectLatencyMilliseconds,
            finding.ServiceHint,
            finding.Banner,
            finding.TlsSummary,
            finding.ApplicationSummary,
            finding.ProbeNotes);

    private void SetSelectedPortScanBatchRowFromCode(PortScanBatchRowViewModel? row)
    {
        _portScanBatchSelectionChangeFromCode = true;
        try
        {
            SelectedPortScanBatchRow = row;
        }
        finally
        {
            _portScanBatchSelectionChangeFromCode = false;
        }
    }

    private void ShowSelectedPortScanBatchRowDetails(PortScanBatchRowViewModel? row)
    {
        RefreshPortScanBatchSummary();
        if (row is null)
        {
            return;
        }

        if (_portScanBatchResultLookup.TryGetValue(row.Target, out var result))
        {
            DisplayPortScanResult(result, rememberAsCurrent: true, persistState: false, appendModuleOutput: false);
            DashboardCards[6].Detail = $"当前查看批量目标：{row.Target}";
            return;
        }

        _portScanCurrentExecutionTarget = row.Target;
        PortScanDetail =
            $"目标：{row.Target}\n" +
            $"状态：{row.Status}\n" +
            $"摘要：{(string.IsNullOrWhiteSpace(row.Summary) ? "无" : row.Summary)}\n" +
            $"错误：{(string.IsNullOrWhiteSpace(row.Error) ? "无" : row.Error)}\n\n" +
            "该目标尚未生成可展示的结构化结果，完成后可点击查看详情。";
        DashboardCards[6].Detail = $"当前查看批量目标：{row.Target}（{row.Status}）";
    }
}
