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
        string CacheIsolationAlternateApiKey);

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

            if (CanRunProxyFromQuickSuite())
            {
                await RunProxyCoreAsync(BuildBasicProxySingleExecutionPlan(), CancellationToken.None);
            }
            else
            {
                DashboardCards[3].Status = "已跳过";
                DashboardCards[3].Detail = "快速套件跳过了中转站检测，因为默认入口或默认密钥未填写。";
            }

            StatusMessage = "快速诊断套件已完成。";
        });
    }

    private Task RunNetworkAsync()
        => ExecuteBusyActionAsync("正在运行基础网络检测...", RunNetworkCoreAsync);

    private Task RunChatGptTraceAsync()
        => ExecuteBusyActionAsync("正在运行官方 API 可用性检测...", RunChatGptTraceCoreAsync);

    private Task RunClientApiDiagnosticsAsync()
        => ExecuteBusyActionAsync("正在运行客户端 API 联通鉴定...", RunClientApiDiagnosticsCoreAsync);

    private Task RunStunAsync()
        => ExecuteBusyActionAsync("正在运行 STUN 绑定检测...", RunStunCoreAsync);

    private Task FetchProxyModelsAsync()
        => ExecuteBusyActionAsync("正在拉取中转站模型列表...", FetchProxyModelsCoreAsync);

    private Task RunBasicProxyAsync()
        => ExecuteProxyBusyActionAsync("正在运行基础中转站诊断...", cancellationToken => RunProxyCoreAsync(BuildBasicProxySingleExecutionPlan(), cancellationToken));

    private Task RunDeepProxyAsync()
        => ExecuteProxyBusyActionAsync("正在运行中转站深度诊断...", cancellationToken => RunProxyCoreAsync(BuildDeepProxySingleExecutionPlan(), cancellationToken));

    private Task RunProxySeriesAsync()
        => ExecuteProxyBusyActionAsync("正在运行中转站稳定性序列...", RunProxySeriesCoreAsync);

    private async Task ExecuteBusyActionAsync(string startMessage, Func<Task> action)
    {
        IsBusy = true;
        StatusMessage = startMessage;

        try
        {
            await action();
            LastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (Exception ex)
        {
            StatusMessage = $"执行失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteProxyBusyActionAsync(string startMessage, Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        StatusMessage = startMessage;
        BeginCurrentProxyOperationCancellation();

        try
        {
            await action(_currentProxyOperationCancellationSource!.Token);
            LastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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
        }
        catch (Exception ex)
        {
            StatusMessage = $"执行失败：{ex.Message}";
        }
        finally
        {
            EndCurrentProxyOperationCancellation();
            IsBusy = false;
        }
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
        var result = await _chatGptTraceService.RunAsync();
        var unlockProgress = new Progress<string>(message => StatusMessage = message);
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
        var progress = new Progress<string>(message => StatusMessage = message);
        var result = await _clientApiDiagnosticsService.RunAsync(progress);
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
        var result = await RunSingleProxyDiagnosticsAsync(BuildProxySettings(), progress, executionPlan, cancellationToken: cancellationToken);

        _proxySingleChartRuns.Add(result);
        ApplyProxyResult(result);
        ShowFinalSingleProxyChart(result);
        DashboardCards[3].Status = result.Verdict ?? "待复核";
        DashboardCards[3].Detail = result.PrimaryIssue ?? result.Summary;
        AppendHistory(
            "中转站",
            executionPlan.Mode == ProxySingleExecutionMode.Deep ? "中转站深度单次诊断" : "中转站基础单次诊断",
            ProxySummary);
    }

    private Task<ProxyDiagnosticsResult> RunSingleProxyDiagnosticsAsync(
        IProgress<ProxyDiagnosticsLiveProgress>? progress,
        ProxySingleExecutionPlan executionPlan,
        bool updateSingleChartPhases = true,
        CancellationToken cancellationToken = default)
        => RunSingleProxyDiagnosticsAsync(BuildProxySettings(), progress, executionPlan, updateSingleChartPhases, cancellationToken);

    private async Task<ProxyDiagnosticsResult> RunSingleProxyDiagnosticsAsync(
        ProxyEndpointSettings settings,
        IProgress<ProxyDiagnosticsLiveProgress>? progress,
        ProxySingleExecutionPlan executionPlan,
        bool updateSingleChartPhases = true,
        CancellationToken cancellationToken = default)
    {
        var streamThroughputSampleCount = executionPlan.Mode == ProxySingleExecutionMode.Basic ? 3 : 1;
        var result = await _proxyDiagnosticsService.RunAsync(
            settings,
            progress,
            cancellationToken,
            streamThroughputSampleCount: streamThroughputSampleCount);

        if (executionPlan.EnableProtocolCompatibilityTest ||
            executionPlan.EnableErrorTransparencyTest ||
            executionPlan.EnableStreamingIntegrityTest ||
            executionPlan.EnableOfficialReferenceIntegrityTest ||
            executionPlan.EnableMultiModalTest ||
            executionPlan.EnableCacheMechanismTest ||
            executionPlan.EnableCacheIsolationTest)
        {
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

        if (executionPlan.EnableLongStreamingTest)
        {
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
            string.Empty);

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
            ProxyCacheIsolationAlternateApiKey);

    private ProxySingleExecutionPlan BuildProxySingleExecutionPlan(ProxySingleExecutionMode mode)
        => mode == ProxySingleExecutionMode.Deep
            ? BuildDeepProxySingleExecutionPlan()
            : BuildBasicProxySingleExecutionPlan();

    private async Task FetchProxyModelsCoreAsync()
    {
        var result = await _proxyDiagnosticsService.FetchModelsAsync(BuildProxySettings());
        ApplyProxyModelCatalogResult(result);
        IsProxyModelPickerOpen = true;
        DashboardCards[3].Status = result.Success ? "模型已拉取" : "模型拉取失败";
        DashboardCards[3].Detail = result.Summary;
        StatusMessage = result.Summary;
        AppendHistory("中转站", "拉取模型列表", ProxyModelCatalogSummary);
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
        StatusMessage = $"中转站稳定性序列完成，健康度 {result.HealthScore}/100（{result.HealthLabel}）。";
        AppendHistory("中转站", "中转站稳定性序列", ProxyStabilitySummary);
    }

    private ProxyEndpointSettings BuildProxySettings()
        => BuildProxySettings(ProxyBaseUrl.Trim(), ProxyApiKey.Trim(), ProxyModel.Trim());

    private ProxyEndpointSettings BuildProxySettings(string baseUrl, string apiKey, string model)
        => new(
            baseUrl.Trim(),
            apiKey.Trim(),
            model.Trim(),
            ProxyIgnoreTlsErrors,
            ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 120));

    private bool CanRunProxyFromQuickSuite()
        => !string.IsNullOrWhiteSpace(ProxyBaseUrl) &&
           !string.IsNullOrWhiteSpace(ProxyApiKey) &&
           !string.IsNullOrWhiteSpace(ProxyModel);

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
