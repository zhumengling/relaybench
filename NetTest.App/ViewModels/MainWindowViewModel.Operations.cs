using System.Collections.ObjectModel;
using System.Text;
using NetTest.App.Infrastructure;
using NetTest.App.Services;
using NetTest.Core.Models;
using NetTest.Core.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private enum ProxySingleExecutionMode
    {
        Basic,
        Deep
    }

    private sealed record ProxySingleExecutionPlan(
        ProxySingleExecutionMode Mode,
        bool EnableLongStreamingTest,
        bool EnableProtocolCompatibilityTest,
        bool EnableErrorTransparencyTest,
        bool EnableStreamingIntegrityTest,
        bool EnableOfficialReferenceIntegrityTest,
        string OfficialReferenceBaseUrl,
        string OfficialReferenceApiKey,
        string OfficialReferenceModel,
        bool EnableMultiModalTest,
        bool EnableCacheMechanismTest,
        bool EnableCacheIsolationTest,
        string CacheIsolationAlternateApiKey,
        IReadOnlyList<string> MultiModelBenchmarkModels);

    private bool CanRun() => !IsBusy;

    private void HandleNonFatalCommandException(Exception ex)
        => StatusMessage = $"操作失败：{ex.Message}";

    private async Task RunQuickSuiteAsync()
    {
        await ExecuteBusyActionAsync("正在运行快速诊断套件...", async () =>
        {
            await RunNetworkCoreAsync();
            await RunChatGptTraceCoreAsync();
            await RunStunCoreAsync();

            if (CanRunProxyFromQuickSuite(out var quickSkipReason))
            {
                await RunProxyCoreAsync(BuildBasicProxySingleExecutionPlan(), CancellationToken.None);
            }
            else
            {
                DashboardCards[3].Status = "\u5DF2\u8DF3\u8FC7";
                DashboardCards[3].Detail = quickSkipReason;
            }

            StatusMessage = "快速诊断套件已完成。";
        });
    }

    private Task RunNetworkAsync()
        => ExecuteBusyActionAsync(
            "正在运行基础网络检测...",
            RunNetworkCoreAsync,
            "\u57FA\u7840\u7F51\u7EDC\u68C0\u6D4B",
            "\u8BF7\u6C42\u4E2D",
            18d);

    private Task RunChatGptTraceAsync()
        => ExecuteBusyActionAsync(
            "正在运行官方 API 可用性检测...",
            RunChatGptTraceCoreAsync,
            "\u7F51\u9875 API \u68C0\u6D4B",
            "\u8BF7\u6C42\u4E2D",
            18d);

    private Task RunClientApiDiagnosticsAsync()
        => ExecuteBusyActionAsync(
            "正在运行客户端 API 联通鉴定...",
            RunClientApiDiagnosticsCoreAsync,
            "\u5BA2\u6237\u7AEF API \u9274\u5B9A",
            "\u9274\u5B9A\u4E2D",
            16d);

    private Task RunStunAsync()
        => ExecuteBusyActionAsync(
            "正在运行 STUN 绑定检测...",
            RunStunCoreAsync,
            "STUN / NAT \u590D\u6838",
            "\u8BF7\u6C42\u4E2D",
            18d);

    private Task FetchProxyModelsAsync()
        => ExecuteBusyActionAsync(
            "正在拉取接口模型列表...",
            FetchProxyModelsCoreAsync,
            "\u62C9\u53D6\u6A21\u578B\u5217\u8868",
            "\u62C9\u53D6\u4E2D",
            18d);

    private Task RunBasicProxyAsync()
        => ExecuteProxyBusyActionAsync(
            "正在运行基础接口诊断...",
            cancellationToken => RunProxyCoreAsync(BuildBasicProxySingleExecutionPlan(), cancellationToken),
            "\u5355\u7AD9\u5FEB\u901F\u6D4B\u8BD5",
            "\u51C6\u5907\u4E2D",
            6d);

    private Task RunDeepProxyAsync()
        => ExecuteProxyBusyActionAsync(
            "正在运行接口深度诊断...",
            cancellationToken => RunProxyCoreAsync(BuildDeepProxySingleExecutionPlan(), cancellationToken),
            "\u5355\u7AD9\u6DF1\u5EA6\u6D4B\u8BD5",
            "\u51C6\u5907\u4E2D",
            6d);

    private Task RunProxySeriesAsync()
        => ExecuteProxyBusyActionAsync(
            "正在运行接口稳定性序列...",
            RunProxySeriesCoreAsync,
            "\u7A33\u5B9A\u6027\u6D4B\u8BD5",
            "\u51C6\u5907\u4E2D",
            6d);

    private Task ExecuteBusyActionAsync(string startMessage, Func<Task> action)
        => ExecuteBusyActionAsync(startMessage, action, globalTaskTitle: null, globalTaskShortStatus: null, initialPercent: 18d);

    private async Task ExecuteBusyActionAsync(
        string startMessage,
        Func<Task> action,
        string? globalTaskTitle,
        string? globalTaskShortStatus,
        double initialPercent)
    {
        IsBusy = true;
        StatusMessage = startMessage;
        ShowGlobalTaskProgress(
            ResolveGlobalTaskProgressTitle(startMessage, globalTaskTitle),
            globalTaskShortStatus ?? "\u8BF7\u6C42\u4E2D",
            initialPercent);

        try
        {
            await action();
            LastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (IsGlobalTaskProgressVisible && IsGlobalTaskProgressRunning)
            {
                CompleteGlobalTaskProgress();
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "\u5F53\u524D\u4EFB\u52A1\u5DF2\u505C\u6B62\u3002";
            if (IsGlobalTaskProgressVisible && IsGlobalTaskProgressRunning)
            {
                StopGlobalTaskProgress();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"执行失败：{ex.Message}";
            if (IsGlobalTaskProgressVisible && IsGlobalTaskProgressRunning)
            {
                FailGlobalTaskProgress();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ExecuteProxyBusyActionAsync(string startMessage, Func<CancellationToken, Task> action)
        => ExecuteProxyBusyActionAsync(startMessage, action, globalTaskTitle: null, globalTaskShortStatus: null, initialPercent: 6d);

    private async Task ExecuteProxyBusyActionAsync(
        string startMessage,
        Func<CancellationToken, Task> action,
        string? globalTaskTitle,
        string? globalTaskShortStatus,
        double initialPercent)
    {
        IsBusy = true;
        StatusMessage = startMessage;
        BeginCurrentProxyOperationCancellation();
        ShowGlobalTaskProgress(
            ResolveGlobalTaskProgressTitle(startMessage, globalTaskTitle),
            globalTaskShortStatus ?? "\u8BF7\u6C42\u4E2D",
            initialPercent);

        try
        {
            await action(_currentProxyOperationCancellationSource!.Token);
            LastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (IsGlobalTaskProgressVisible && IsGlobalTaskProgressRunning)
            {
                CompleteGlobalTaskProgress();
            }
        }
        catch (OperationCanceledException) when (_currentProxyOperationCancellationSource?.IsCancellationRequested == true)
        {
            var cancelMessage = _proxyCancellationRequestedByUser
                ? "已停止当前测试，可关闭弹窗或重新发起测试。"
                : "当前测试已取消。";
            StatusMessage = cancelMessage;
            ProxyChartDialogStatusSummary = cancelMessage;
            DashboardCards[3].Status = "已停止";
            DashboardCards[3].Detail = "当前测试已手动停止。";
            if (IsGlobalTaskProgressVisible && IsGlobalTaskProgressRunning)
            {
                StopGlobalTaskProgress("\u5DF2\u505C\u6B62");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"执行失败：{ex.Message}";
            if (IsGlobalTaskProgressVisible && IsGlobalTaskProgressRunning)
            {
                FailGlobalTaskProgress();
            }
        }
        finally
        {
            EndCurrentProxyOperationCancellation();
            IsBusy = false;
        }
    }

    private static string ResolveGlobalTaskProgressTitle(string startMessage, string? explicitTitle)
    {
        if (!string.IsNullOrWhiteSpace(explicitTitle))
        {
            return explicitTitle.Trim();
        }

        var title = startMessage.Trim();
        if (title.StartsWith("\u6B63\u5728", StringComparison.Ordinal))
        {
            title = title[2..].TrimStart();
        }

        foreach (var prefix in new[]
                 {
                     "\u6309\u8BBE\u5B9A\u65F6\u957F\u6301\u7EED\u8FD0\u884C",
                     "\u8FD0\u884C",
                     "\u6267\u884C",
                     "\u62C9\u53D6",
                     "\u5BFC\u51FA",
                     "\u68C0\u6D4B"
                 })
        {
            if (title.StartsWith(prefix, StringComparison.Ordinal))
            {
                title = title[prefix.Length..].TrimStart();
                break;
            }
        }

        title = title.TrimEnd('.', '\u3002', '\u2026', '\uFF1F', '?');
        while (title.EndsWith("..", StringComparison.Ordinal))
        {
            title = title[..^2].TrimEnd('.', '\u3002', '\u2026', '\uFF1F', '?');
        }

        return string.IsNullOrWhiteSpace(title)
            ? "\u4EFB\u52A1\u8FDB\u5EA6"
            : title;
    }

    private void BeginCurrentProxyOperationCancellation()
    {
        EndCurrentProxyOperationCancellation();
        _proxyCancellationRequestedByUser = false;
        _currentProxyOperationCancellationSource = new CancellationTokenSource();
        RefreshCurrentProxyTestCommandStates();
    }

    private void EndCurrentProxyOperationCancellation()
    {
        _currentProxyOperationCancellationSource?.Dispose();
        _currentProxyOperationCancellationSource = null;
        _proxyCancellationRequestedByUser = false;
        RefreshCurrentProxyTestCommandStates();
    }

    private bool CanStopCurrentProxyTestAction()
        => CanStopCurrentProxyTest;

    private Task StopCurrentProxyTestAsync()
    {
        if (_currentProxyOperationCancellationSource is { IsCancellationRequested: false } cancellationSource)
        {
            SuppressProxyTrendChartAutoOpen();
            _proxyCancellationRequestedByUser = true;
            cancellationSource.Cancel();
            StatusMessage = "已请求停止当前测试，将在当前请求步骤结束后停止。";
            ProxyChartDialogStatusSummary = StatusMessage;
            DashboardCards[3].Status = "已停止";
            DashboardCards[3].Detail = "当前测试停止请求已发送。";
        }

        RefreshCurrentProxyTestCommandStates();
        return Task.CompletedTask;
    }

    private void RefreshCurrentProxyTestCommandStates()
    {
        StopCurrentProxyTestCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanStopCurrentProxyTest));
    }

    private async Task RunNetworkCoreAsync()
    {
        var result = await _networkDiagnosticsService.RunAsync();
        ApplyNetworkSnapshot(result);
        DashboardCards[0].Status = "完成";
        DashboardCards[0].Detail = result.PublicIp is null
            ? $"主机 {result.HostName}，未能获取公网 IP。"
            : $"主机 {result.HostName}，公网 IP 为 {result.PublicIp}。";
        AppendHistory("网络", "基础网络检测", NetworkSummary);
    }

    private async Task RunChatGptTraceCoreAsync()
    {
        UpdateGlobalTaskProgress("\u8BF7\u6C42\u4E2D", 26d);
        var result = await _chatGptTraceService.RunAsync();
        UpdateGlobalTaskProgress("\u590D\u6838\u4E2D", 56d);
        var unlockProgressPercent = 56d;
        var unlockProgress = new Progress<string>(message =>
        {
            StatusMessage = message;
            unlockProgressPercent = Math.Min(unlockProgressPercent + 8d, 86d);
            UpdateGlobalTaskProgress("\u590D\u6838\u4E2D", unlockProgressPercent);
        });
        var unlockCatalogResult = await _unlockCatalogDiagnosticsService.RunAsync(unlockProgress);
        ApplyChatGptTrace(result);
        ApplyUnlockCatalogResult(unlockCatalogResult);
        DashboardCards[1].Status = result.Error is null ? "完成" : "失败";
        DashboardCards[1].Detail = result.Error is null
            ? $"{result.LocationCode ?? "--"} / {result.CloudflareColo ?? "--"} / {(result.IsSupportedRegion ? "支持" : "需复核")} / 业务就绪 {unlockCatalogResult.SemanticReadyCount}/{unlockCatalogResult.Checks.Count}"
            : result.Error;
        AppendHistory("官方API", "官方 API 可用性检测", $"{ChatGptSummary}\n\n{UnlockCatalogSummary}");
    }

    private async Task RunClientApiDiagnosticsCoreAsync()
    {
        var progress = new Progress<string>(message =>
        {
            StatusMessage = message;
            UpdateGlobalTaskProgressForClientApiMessage(message);
        });
        var result = await _clientApiDiagnosticsService.RunAsync(progress);
        UpdateGlobalTaskProgress("\u6C47\u603B\u4E2D", 92d);
        ApplyClientApiResult(result);
        DashboardCards[1].Status = result.ReachableCount > 0 ? "完成" : "失败";
        DashboardCards[1].Detail = result.Error is null
            ? $"客户端就绪 {result.InstalledCount}/{result.Checks.Count} / 已配置 {result.ConfiguredCount}/{result.Checks.Count} / API 可达 {result.ReachableCount}/{result.Checks.Count}"
            : result.Error;
        AppendHistory("客户端API", "客户端 API 联通鉴定", $"{ClientApiSummary}\n\n{ClientApiDetail}");
    }

    private async Task RunStunCoreAsync()
    {
        var result = await _stunProbeService.ProbeAsync(StunServer.Trim(), GetSelectedStunTransportProtocol());
        ApplyStunResult(result);
        DashboardCards[2].Status = result.Success ? "完成" : "失败";
        DashboardCards[2].Detail = result.Success
            ? $"{(result.TransportProtocol == StunTransportProtocol.Tcp ? "TCP" : "UDP")} / {result.NatType ?? "--"} / {result.ClassificationConfidence} / {result.MappedAddress ?? "--"}"
            : result.Error ?? "STUN 检测失败。";
        AppendHistory("STUN", "STUN 检测", StunSummary);
    }

    private async Task RunProxyCoreAsync(ProxySingleExecutionPlan executionPlan, CancellationToken cancellationToken)
    {
        _currentProxySingleExecutionPlan = executionPlan;
        _lastProxySingleExecutionMode = executionPlan.Mode;
        _proxySingleChartRuns.Clear();
        StartSingleProxyChartLiveSession();
        var progress = new Progress<ProxyDiagnosticsLiveProgress>(UpdateSingleProxyChartLive);
        var result = await RunSingleProxyDiagnosticsAsync(
            BuildProxySettings(),
            progress,
            executionPlan,
            updateSingleChartPhases: true,
            updateGlobalTaskProgressPhases: true,
            cancellationToken: cancellationToken);

        _proxySingleChartRuns.Add(result);
        ApplyProxyResult(result);
        ShowFinalSingleProxyChart(result);
        DashboardCards[3].Status = result.Verdict ?? "待复核";
        DashboardCards[3].Detail = result.PrimaryIssue ?? result.Summary;
        AppendHistory(
            "接口",
            executionPlan.Mode == ProxySingleExecutionMode.Deep ? "接口深度单次诊断" : "接口基础单次诊断",
            ProxySummary);
    }

    private Task<ProxyDiagnosticsResult> RunSingleProxyDiagnosticsAsync(
        IProgress<ProxyDiagnosticsLiveProgress>? progress,
        ProxySingleExecutionPlan executionPlan,
        bool updateSingleChartPhases = true,
        bool updateGlobalTaskProgressPhases = false,
        CancellationToken cancellationToken = default)
        => RunSingleProxyDiagnosticsAsync(
            BuildProxySettings(),
            progress,
            executionPlan,
            updateSingleChartPhases,
            updateGlobalTaskProgressPhases,
            cancellationToken);

    private async Task<ProxyDiagnosticsResult> RunSingleProxyDiagnosticsAsync(
        ProxyEndpointSettings settings,
        IProgress<ProxyDiagnosticsLiveProgress>? progress,
        ProxySingleExecutionPlan executionPlan,
        bool updateSingleChartPhases = true,
        bool updateGlobalTaskProgressPhases = false,
        CancellationToken cancellationToken = default)
    {
        var hasSupplementalScenarios =
            executionPlan.EnableProtocolCompatibilityTest ||
            executionPlan.EnableErrorTransparencyTest ||
            executionPlan.EnableStreamingIntegrityTest ||
            executionPlan.EnableOfficialReferenceIntegrityTest ||
            executionPlan.EnableMultiModalTest ||
            executionPlan.EnableCacheMechanismTest ||
            executionPlan.EnableCacheIsolationTest;
        var hasCapabilityMatrix = executionPlan.Mode == ProxySingleExecutionMode.Deep &&
                                  HasConfiguredProxyCapabilityModels();
        var hasLongStreaming = executionPlan.EnableLongStreamingTest;
        var hasMultiModelBenchmark = executionPlan.MultiModelBenchmarkModels.Count > 0;

        if (updateGlobalTaskProgressPhases)
        {
            UpdateGlobalTaskProgress("\u8BF7\u6C42\u4E2D", 12d);
        }

        var streamThroughputSampleCount = 1;
        var result = await _proxyDiagnosticsService.RunAsync(
            settings,
            progress,
            cancellationToken,
            streamThroughputSampleCount: streamThroughputSampleCount);

        if (updateGlobalTaskProgressPhases)
        {
            UpdateGlobalTaskProgress("\u541E\u5410\u4E2D", 34d);
        }

        StatusMessage = "正在运行独立吞吐测试（3 轮）...";
        if (updateSingleChartPhases)
        {
            ShowSingleProxySupplementalChartPhase(result, "独立吞吐", StatusMessage);
        }

        var throughputSettings = string.IsNullOrWhiteSpace(result.EffectiveModel)
            ? settings
            : settings with { Model = result.EffectiveModel };
        var throughputBaseResult = result;
        var throughputLiveProgress = new Progress<ProxyThroughputBenchmarkLiveProgress>(liveProgress =>
        {
            StatusMessage = liveProgress.Summary;
            ProxyChartDialogStatusSummary = liveProgress.Summary;
            ProxyOverviewThroughput =
                $"\u72EC\u7ACB\u541E\u5410 {FormatTokensPerSecond(liveProgress.CurrentOutputTokensPerSecond ?? liveProgress.LiveMedianOutputTokensPerSecond, liveProgress.CurrentOutputTokenCountEstimated)} / \u7B2C {liveProgress.CurrentSampleIndex}/{liveProgress.RequestedSampleCount} \u8F6E / \u8F93\u51FA {liveProgress.CurrentOutputTokenCount?.ToString() ?? "--"}";
            if (updateSingleChartPhases)
            {
                ShowSingleProxySupplementalChartPhase(
                    BuildLiveThroughputDiagnosticsResult(throughputBaseResult, liveProgress),
                    "\u72EC\u7ACB\u541E\u5410",
                    liveProgress.Summary);
            }
        });
        var throughputBenchmark = await _proxyDiagnosticsService.RunThroughputBenchmarkAsync(
            throughputSettings,
            liveProgress: throughputLiveProgress,
            cancellationToken: cancellationToken);
        result = result with { ThroughputBenchmarkResult = throughputBenchmark };

        if (updateGlobalTaskProgressPhases && !hasSupplementalScenarios && !hasCapabilityMatrix && !hasLongStreaming && !hasMultiModelBenchmark)
        {
            UpdateGlobalTaskProgress("\u6536\u5C3E\u4E2D", 92d);
        }

        if (hasSupplementalScenarios)
        {
            if (updateGlobalTaskProgressPhases)
            {
                UpdateGlobalTaskProgress("\u8865\u6D4B\u4E2D", 54d);
            }

            StatusMessage = BuildSupplementalProxyDiagnosticsStatusMessage(executionPlan);
            if (updateSingleChartPhases)
            {
                ShowSingleProxySupplementalChartPhase(result, "补充探针", StatusMessage);
            }
            result = await _proxyDiagnosticsService.RunSupplementalScenariosAsync(
                settings,
                result,
                executionPlan.EnableProtocolCompatibilityTest,
                executionPlan.EnableErrorTransparencyTest,
                executionPlan.EnableStreamingIntegrityTest,
                executionPlan.EnableOfficialReferenceIntegrityTest,
                executionPlan.OfficialReferenceBaseUrl,
                executionPlan.OfficialReferenceApiKey,
                executionPlan.OfficialReferenceModel,
                executionPlan.EnableMultiModalTest,
                executionPlan.EnableCacheMechanismTest,
                executionPlan.EnableCacheIsolationTest,
                executionPlan.CacheIsolationAlternateApiKey,
                progress,
                cancellationToken);
        }

        if (hasCapabilityMatrix)
        {
            if (updateGlobalTaskProgressPhases)
            {
                UpdateGlobalTaskProgress("\u80FD\u529B\u4E2D", 70d);
            }

            StatusMessage = "\u6B63\u5728\u8FD0\u884C\u975E\u804A\u5929 API \u80FD\u529B\u77E9\u9635...";
            if (updateSingleChartPhases)
            {
                ShowSingleProxySupplementalChartPhase(result, "\u975E\u804A\u5929 API", StatusMessage);
            }

            result = await _proxyDiagnosticsService.RunNonChatCapabilityMatrixAsync(
                settings,
                result,
                progress,
                cancellationToken);
        }

        if (hasLongStreaming)
        {
            if (updateGlobalTaskProgressPhases)
            {
                UpdateGlobalTaskProgress("\u957F\u6D41\u4E2D", 84d);
            }

            StatusMessage = $"正在运行长流稳定简测（{GetProxyLongStreamSegmentCount()} 段）...";
            if (updateSingleChartPhases)
            {
                ShowSingleProxySupplementalChartPhase(result, "长流简测", StatusMessage);
            }
            var longStreamingSettings = string.IsNullOrWhiteSpace(result.EffectiveModel)
                ? settings
                : settings with { Model = result.EffectiveModel };
            var longStreamingResult = await _proxyDiagnosticsService.RunLongStreamingTestAsync(
                longStreamingSettings,
                GetProxyLongStreamSegmentCount(),
                cancellationToken);
            result = result with { LongStreamingResult = longStreamingResult };
        }

        if (hasMultiModelBenchmark)
        {
            if (updateGlobalTaskProgressPhases)
            {
                UpdateGlobalTaskProgress("\u591A\u6A21\u4E2D", 94d);
            }

            StatusMessage = $"\u6B63\u5728\u8FD0\u884C\u591A\u6A21\u578B tok/s \u5BF9\u6BD4\uff08{executionPlan.MultiModelBenchmarkModels.Count} \u4E2A\uff09...";
            if (updateSingleChartPhases)
            {
                ShowSingleProxySupplementalChartPhase(result, "\u591A\u6A21\u578B\u6D4B\u901F", StatusMessage);
            }

            var multiModelResults = await _proxyDiagnosticsService.RunMultiModelSpeedTestAsync(
                settings,
                executionPlan.MultiModelBenchmarkModels,
                cancellationToken);
            result = result with { MultiModelSpeedResults = multiModelResults };
        }

        if (updateGlobalTaskProgressPhases)
        {
            UpdateGlobalTaskProgress("\u6536\u5C3E\u4E2D", 97d);
        }

        return result;
    }

    private string BuildSupplementalProxyDiagnosticsStatusMessage(ProxySingleExecutionPlan executionPlan)
    {
        List<string> sections = [];
        if (executionPlan.EnableProtocolCompatibilityTest)
        {
            sections.Add("协议兼容");
        }

        if (executionPlan.EnableErrorTransparencyTest)
        {
            sections.Add("错误透传");
        }

        if (executionPlan.EnableStreamingIntegrityTest)
        {
            sections.Add("流式完整性");
        }

        if (executionPlan.EnableOfficialReferenceIntegrityTest)
        {
            sections.Add("官方对照");
        }

        if (executionPlan.EnableMultiModalTest)
        {
            sections.Add("多模态");
        }

        if (executionPlan.EnableCacheMechanismTest)
        {
            sections.Add("缓存机制");
        }

        if (executionPlan.EnableCacheIsolationTest)
        {
            sections.Add("缓存隔离");
        }

        return sections.Count == 0
            ? "正在运行补充探针..."
            : $"正在运行{string.Join("、", sections)}补充探针...";
    }

    private ProxySingleExecutionPlan BuildBasicProxySingleExecutionPlan()
        => new(
            ProxySingleExecutionMode.Basic,
            false,
            false,
            false,
            false,
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            false,
            false,
            string.Empty,
            Array.Empty<string>());

    private ProxySingleExecutionPlan BuildDeepProxySingleExecutionPlan()
        => new(
            ProxySingleExecutionMode.Deep,
            false,
            ProxyEnableProtocolCompatibilityTest,
            ProxyEnableErrorTransparencyTest,
            ProxyEnableStreamingIntegrityTest,
            ProxyEnableOfficialReferenceIntegrityTest,
            ProxyOfficialReferenceBaseUrl,
            ProxyOfficialReferenceApiKey,
            ProxyOfficialReferenceModel,
            ProxyEnableMultiModalTest,
            ProxyEnableCacheMechanismTest,
            ProxyEnableCacheIsolationTest,
            ProxyCacheIsolationAlternateApiKey,
            GetSelectedProxyMultiModelNames());

    private ProxySingleExecutionPlan BuildProxySingleExecutionPlan(ProxySingleExecutionMode mode)
        => mode == ProxySingleExecutionMode.Deep
            ? BuildDeepProxySingleExecutionPlan()
            : BuildBasicProxySingleExecutionPlan();

    private async Task FetchProxyModelsCoreAsync()
    {
        var result = await _proxyDiagnosticsService.FetchModelsAsync(BuildProxySettings());
        ApplyProxyModelCatalogResult(result);
        IsProxyModelPickerOpen = true;
        IsProxyMultiModelPickerOpen = false;
        DashboardCards[3].Status = result.Success ? "模型已拉取" : "模型拉取失败";
        DashboardCards[3].Detail = result.Summary;
        StatusMessage = result.Summary;
        AppendHistory("接口", "拉取模型列表", ProxyModelCatalogSummary);
    }

    private Task CloseProxyModelPickerAsync()
    {
        IsProxyModelPickerOpen = false;
        return Task.CompletedTask;
    }

    private async Task RunProxySeriesCoreAsync(CancellationToken cancellationToken)
    {
        var rounds = ParseBoundedInt(ProxySeriesRoundsText, fallback: 5, min: 1, max: 50);
        var delayMilliseconds = ParseBoundedInt(ProxySeriesDelayMsText, fallback: 1200, min: 0, max: 30_000);
        List<ProxyDiagnosticsResult> liveRounds = [];
        _proxyStabilityChartRounds.Clear();
        _proxyChartRequestedRounds = rounds;
        _proxyChartDelayMilliseconds = delayMilliseconds;
        StartProxySeriesChartLiveSession(rounds, delayMilliseconds);
        UpdateGlobalTaskProgress("\u51C6\u5907\u4E2D", 8d);
        var progress = new Progress<string>(message =>
        {
            StatusMessage = message;
            ProxyChartDialogStatusSummary = message;
        });
        var roundProgress = new Progress<ProxyDiagnosticsResult>(round =>
        {
            liveRounds.Add(round);
            _proxyStabilityChartRounds.Add(round);
            UpdateProxySeriesChartLive(liveRounds, rounds, delayMilliseconds);
            UpdateGlobalTaskProgress(liveRounds.Count, rounds, $"\u7B2C {liveRounds.Count}/{rounds} \u8F6E");
        });

        var result = await _proxyDiagnosticsService.RunSeriesAsync(
            BuildProxySettings(),
            rounds,
            delayMilliseconds,
            progress,
            roundProgress,
            cancellationToken);

        ApplyProxyStabilityResult(result);
        ShowFinalProxySeriesChart(result);
        DashboardCards[3].Status = result.HealthLabel;
        DashboardCards[3].Detail = result.Summary;
        StatusMessage = $"接口稳定性序列完成，健康度 {result.HealthScore}/100（{result.HealthLabel}）。";
        AppendHistory("接口", "接口稳定性序列", ProxyStabilitySummary);
    }

    private ProxyEndpointSettings BuildProxySettings()
        => BuildProxySettings(ProxyBaseUrl.Trim(), ProxyApiKey.Trim(), ProxyModel.Trim());

    private ProxyEndpointSettings BuildProxySettings(string baseUrl, string apiKey, string model)
        => new(
            baseUrl.Trim(),
            apiKey.Trim(),
            model.Trim(),
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 120),
            NormalizeNullable(ProxyEmbeddingsModel),
            NormalizeNullable(ProxyImagesModel),
            NormalizeNullable(ProxyAudioTranscriptionModel),
            NormalizeNullable(ProxyAudioSpeechModel),
            NormalizeNullable(ProxyModerationModel));

    private bool CanRunProxyFromQuickSuite(out string skipReason)
    {
        if (string.IsNullOrWhiteSpace(ProxyBaseUrl) || string.IsNullOrWhiteSpace(ProxyApiKey))
        {
            skipReason = "\u5FEB\u901F\u5957\u4EF6\u8DF3\u8FC7\u4E86\u63A5\u53E3\u68C0\u6D4B\uFF0C\u56E0\u4E3A\u8FD8\u6CA1\u586B\u5199\u63A5\u53E3\u5730\u5740\u6216 API Key\u3002";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProxyModel))
        {
            skipReason = "\u5FEB\u901F\u5957\u4EF6\u8DF3\u8FC7\u4E86\u63A5\u53E3\u68C0\u6D4B\uFF0C\u56E0\u4E3A\u8FD8\u6CA1\u9009\u62E9\u804A\u5929\u6A21\u578B\u3002";
            return false;
        }

        if (TryDescribeLikelyNonChatModel(ProxyModel, out var capabilityLabel))
        {
            skipReason =
                $"\u5FEB\u901F\u5957\u4EF6\u8DF3\u8FC7\u4E86\u63A5\u53E3\u68C0\u6D4B\uFF0C\u56E0\u4E3A\u5F53\u524D\u6A21\u578B\u201C{ProxyModel.Trim()}\u201D\u7591\u4F3C\u5C5E\u4E8E {capabilityLabel}\u3002" +
                "\u5FEB\u901F\u6D4B\u8BD5\u53EA\u9002\u5408\u804A\u5929\u6A21\u578B\u3002";
            return false;
        }

        skipReason = string.Empty;
        return true;
    }

    private static int ParseBoundedInt(string text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, out var value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private void SyncSelectedProxyCatalogModel(string? model)
    {
        var match = VisibleProxyCatalogModels.FirstOrDefault(item =>
            string.Equals(item, model?.Trim(), StringComparison.OrdinalIgnoreCase));

        _suppressProxyCatalogSelectionApply = true;
        try
        {
            if (string.Equals(_selectedProxyCatalogModel, match, StringComparison.Ordinal))
            {
                return;
            }

            _selectedProxyCatalogModel = match;
            OnPropertyChanged(nameof(SelectedProxyCatalogModel));
        }
        finally
        {
            _suppressProxyCatalogSelectionApply = false;
        }
    }

    private void RefreshVisibleProxyCatalogModels()
    {
        var keyword = ProxyModelCatalogFilterText?.Trim() ?? string.Empty;
        IEnumerable<string> filtered = string.IsNullOrWhiteSpace(keyword)
            ? ProxyCatalogModels
            : ProxyCatalogModels
                .Where(item => item.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        VisibleProxyCatalogModels.Clear();
        foreach (var model in filtered)
        {
            VisibleProxyCatalogModels.Add(model);
        }
    }

    private void LoadState()
    {
        var snapshot = _appStateStore.Load();
        LoadWorkbenchState(snapshot);
        LoadNetworkReviewState(snapshot);
        LoadExtendedState(snapshot);
        LoadSpeedState(snapshot);
        LoadSplitRoutingState(snapshot);
        LoadProxyAdvancedState(snapshot);
        LoadProxyCapabilityMatrixState(snapshot);
        ProxyBaseUrl = snapshot.ProxyBaseUrl;
        ProxyApiKey = snapshot.ProxyApiKey;
        ProxyModel = snapshot.ProxyModel ?? string.Empty;
        ProxyTimeoutSecondsText = string.IsNullOrWhiteSpace(snapshot.ProxyTimeoutSecondsText) ? "20" : snapshot.ProxyTimeoutSecondsText;
        ProxyIgnoreTlsErrors = snapshot.ProxyIgnoreTlsErrors;
        ProxySeriesRoundsText = string.IsNullOrWhiteSpace(snapshot.ProxySeriesRoundsText) ? "5" : snapshot.ProxySeriesRoundsText;
        ProxySeriesDelayMsText = string.IsNullOrWhiteSpace(snapshot.ProxySeriesDelayMsText) ? "1200" : snapshot.ProxySeriesDelayMsText;
        LoadProxyBatchState(snapshot);

        _historyEntries.Clear();
        _historyEntries.AddRange(snapshot.HistoryEntries ?? []);
        UpdateHistorySummary();
    }

    private void SaveState()
    {
        AppStateSnapshot snapshot = new()
        {
            ProxyBaseUrl = ProxyBaseUrl,
            ProxyApiKey = ProxyApiKey,
            ProxyModel = ProxyModel,
            ProxyModelWasExplicitlySet = !string.IsNullOrWhiteSpace(ProxyModel),
            ProxyTimeoutSecondsText = ProxyTimeoutSecondsText,
            ProxyIgnoreTlsErrors = ProxyIgnoreTlsErrors,
            ProxySeriesRoundsText = ProxySeriesRoundsText,
            ProxySeriesDelayMsText = ProxySeriesDelayMsText,
            HistoryEntries = _historyEntries.Take(30).ToList()
        };

        ApplyProxyBatchStateToSnapshot(snapshot);
        ApplyProxyAdvancedStateToSnapshot(snapshot);
        ApplyProxyCapabilityMatrixStateToSnapshot(snapshot);
        ApplyWorkbenchStateToSnapshot(snapshot);
        ApplyNetworkReviewStateToSnapshot(snapshot);
        ApplyExtendedStateToSnapshot(snapshot);
        ApplySpeedStateToSnapshot(snapshot);
        ApplySplitRoutingStateToSnapshot(snapshot);
        _appStateStore.Save(snapshot);
    }

    private void AppendHistory(string category, string title, string summary)
    {
        _historyEntries.Insert(0, new RunHistoryEntry(DateTimeOffset.Now, category, title, summary));
        while (_historyEntries.Count > 30)
        {
            _historyEntries.RemoveAt(_historyEntries.Count - 1);
        }

        UpdateHistorySummary();
        SaveState();
    }

}
