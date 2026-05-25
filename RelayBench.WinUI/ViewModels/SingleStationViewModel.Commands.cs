using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Core.Support;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class SingleStationViewModel
{
    private void NotifyAdvancedExecutionSummaryChanged()
    {
        OnPropertyChanged(nameof(AdvancedExecutionSummary));
    }

    private void UpdateKpiLabels(TestMode mode)
    {
        switch (mode)
        {
            case TestMode.Quick:
                Kpi1Label = "聊天延迟";
                Kpi1Value = ChatLatency;
                Kpi2Label = "状态码";
                Kpi2Value = ChatStatusCode;
                Kpi3Label = "模型";
                Kpi3Value = string.IsNullOrWhiteSpace(Model) ? "0" : Model;
                Kpi4Label = "TTFT";
                Kpi4Value = StreamTtft;
                break;
            case TestMode.Stability:
                Kpi1Label = "P50";
                Kpi1Value = StabilityP50;
                Kpi2Label = "P95";
                Kpi2Value = StabilityP95;
                Kpi3Label = "P99";
                Kpi3Value = StabilityP99;
                Kpi4Label = "健康度";
                Kpi4Value = StabilityHealthScore;
                break;
            case TestMode.Deep:
                Kpi1Label = "通过";
                Kpi1Value = DeepPassCount;
                Kpi2Label = "失败";
                Kpi2Value = DeepFailCount;
                Kpi3Label = "场景总数";
                Kpi3Value = DeepScenarios.Count > 0 ? $"{DeepScenarios.Count}" : "0";
                Kpi4Label = "结论";
                Kpi4Value = VerdictDisplay;
                break;
            case TestMode.Concurrency:
                Kpi1Label = "峰值吞吐";
                Kpi1Value = ConcurrencyPeakThroughput;
                Kpi2Label = "实用上限";
                Kpi2Value = ConcurrencyPracticalLimit;
                Kpi3Label = "最高错误率";
                Kpi3Value = ConcurrencyMaxErrorRate;
                Kpi4Label = "已测档位";
                Kpi4Value = $"{ConcurrencyLevels.Count}";
                break;
        }
    }

    /// <summary>
    /// Loads persisted endpoint values from the shared endpoint store (real API key).
    /// Falls back to endpoint history for BaseUrl/Model if shared store is empty.
    /// </summary>
    private void LoadPersistedEndpoint()
    {
        try
        {
            var shared = SharedEndpointStore.Load();
            if (shared is not null && !string.IsNullOrWhiteSpace(shared.BaseUrl))
            {
                BaseUrl = shared.BaseUrl;
                ApiKey = shared.ApiKey;
                Model = shared.Model;
                return;
            }

            // Fallback: load from history (no real API key available)
            var items = _historyStore.LoadAsync().GetAwaiter().GetResult();
            if (items is { Count: > 0 })
            {
                var latest = items[0];
                BaseUrl = latest.BaseUrl ?? "";
                Model = latest.Model ?? "";
            }
        }
        catch
        {
            // Best-effort: if load fails, fields stay empty and PlaceholderText shows.
        }
    }

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusText = "请填写接口地址和 API 密钥";
            return;
        }

        IsTesting = true;
        StatusText = "正在拉取模型列表...";
        try
        {
            var settings = BuildSettings();
            var result = await _diagnosticsService.FetchModelsAsync(settings);
            ModelCount = result.Success ? $"{result.ModelCount}" : "失败";
            StatusText = result.Summary;

            AvailableModels.Clear();
            if (result.Success && result.Models is { Count: > 0 })
            {
                var cachedModels = await _modelCacheService
                    .ListModelsAsync(settings.BaseUrl, settings.ApiKey)
                    .ConfigureAwait(true);
                var shouldProbeProtocols = GlobalEndpointProtocolProbeCoordinator.ShouldProbe(result.Models, cachedModels);

                foreach (var model in result.Models)
                {
                    AddAvailableModelIfMissing(model);
                }

                // Auto-select first model if none is set
                if (string.IsNullOrWhiteSpace(Model) && AvailableModels.Count > 0)
                    Model = AvailableModels[0];

                // Save fetched models to endpoint history
                await _modelCacheService.SaveCatalogAsync(settings, result);
                await GlobalEndpointProtocolProbeCoordinator.Instance.RecordEndpointAsync(BaseUrl, ApiKey, Model, result.Models);
                await LoadEndpointHistoryAsync();
                await RefreshModelProtocolCacheAsync();

                if (shouldProbeProtocols)
                {
                    ModelProtocolCacheSummary = $"模型列表首次加入或已变化，正在真实探测 {result.Models.Count} 个模型的三种协议...";
                    _ = RunBackgroundProtocolDetectionAsync(result.Models.ToList());
                }
                else
                {
            ModelProtocolCacheSummary = $"模型列表未变化，沿用长期协议缓存（{ModelProtocolCacheRows.Count} 个模型）";
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task FetchOfficialReferenceModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(OfficialReferenceBaseUrl) || string.IsNullOrWhiteSpace(OfficialReferenceApiKey))
        {
            StatusText = "请填写官方基础 URL 和官方 API 密钥";
            return;
        }

        var baseUrl = OfficialReferenceBaseUrl.Trim();
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) &&
                parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            {
                StatusText = "请输入有效的官方 HTTP 或 HTTPS 地址";
                return;
            }

            baseUrl = ProtocolPrefix + baseUrl;
        }

        if (!IsValidEndpointUrl(baseUrl))
        {
            StatusText = "请输入有效的官方 HTTP 或 HTTPS 地址";
            return;
        }

        IsTesting = true;
        StatusText = "正在拉取官方模型列表...";
        try
        {
            var settings = new ProxyEndpointSettings(
                baseUrl,
                OfficialReferenceApiKey.Trim(),
                string.IsNullOrWhiteSpace(OfficialReferenceModel) ? string.Empty : OfficialReferenceModel.Trim(),
                IgnoreTlsErrors,
                Math.Clamp(TimeoutSeconds, 5, 120));

            var result = await _diagnosticsService.FetchModelsAsync(settings);
            OfficialReferenceModels.Clear();

            if (result.Success && result.Models is { Count: > 0 })
            {
                foreach (var model in result.Models
                             .Where(static model => !string.IsNullOrWhiteSpace(model))
                             .Select(static model => model.Trim())
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(static model => model, StringComparer.OrdinalIgnoreCase))
                {
                    OfficialReferenceModels.Add(model);
                }

                if (string.IsNullOrWhiteSpace(OfficialReferenceModel) && OfficialReferenceModels.Count > 0)
                {
                    OfficialReferenceModel = OfficialReferenceModels[0];
                }
            }

            StatusText = result.Success
                ? $"官方模型列表已拉取：{OfficialReferenceModels.Count} 个模型"
                : $"官方模型拉取失败：{result.Error ?? result.Summary}";
        }
        catch (Exception ex)
        {
            StatusText = $"官方模型拉取失败: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task FetchProxyModelsAsync()
        => await FetchModelsAsync();

    [RelayCommand]
    private async Task FetchProxyCapabilityModelsAsync(string? capabilityKey)
    {
        await FetchModelsAsync();

        if (AvailableModels.Count == 0)
        {
            return;
        }

        var selected = string.IsNullOrWhiteSpace(Model) ? AvailableModels[0] : Model.Trim();
        switch ((capabilityKey ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "embeddings":
                if (string.IsNullOrWhiteSpace(CapabilityEmbeddingsModel))
                {
                    CapabilityEmbeddingsModel = selected;
                }
                break;
            case "images":
                if (string.IsNullOrWhiteSpace(CapabilityImagesModel))
                {
                    CapabilityImagesModel = selected;
                }
                break;
            case "audio-transcription":
                if (string.IsNullOrWhiteSpace(CapabilityAudioTranscriptionModel))
                {
                    CapabilityAudioTranscriptionModel = selected;
                }
                break;
            case "audio-speech":
                if (string.IsNullOrWhiteSpace(CapabilityAudioSpeechModel))
                {
                    CapabilityAudioSpeechModel = selected;
                }
                break;
            case "moderation":
                if (string.IsNullOrWhiteSpace(CapabilityModerationModel))
                {
                    CapabilityModerationModel = selected;
                }
                break;
        }
    }

    /// <summary>
    /// Validates whether the given URL is a well-formed HTTP or HTTPS endpoint.
    /// </summary>
    public static bool IsValidEndpointUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && IsPlausibleEndpointHost(uri.Host);
    }

    private static bool IsPlausibleEndpointHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.Contains('.', StringComparison.Ordinal)
            || System.Net.IPAddress.TryParse(host, out _);
    }

    [RelayCommand]
    private async Task StartTestAsync()
    {
        // Build full URL with protocol prefix for validation
        var fullUrl = BaseUrl.Trim();
        if (!fullUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !fullUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Reject URLs with non-HTTP schemes (e.g., ftp://, ws://)
            if (Uri.TryCreate(fullUrl, UriKind.Absolute, out var parsed) &&
                parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            {
                StatusText = "请输入有效的 HTTP 或 HTTPS 接口地址";
                return;
            }

            fullUrl = ProtocolPrefix + fullUrl;
        }

        if (!IsValidEndpointUrl(fullUrl))
        {
            StatusText = "请输入有效的 HTTP 或 HTTPS 接口地址";
            return;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusText = "请填写 API 密钥";
            return;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            StatusText = "请选择或输入模型";
            return;
        }

        _cts = new CancellationTokenSource();
        IsTesting = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var completed = false;

        // Record endpoint usage for history
        await RecordEndpointUsageAsync();

        GlobalProgressService.Report(5, $"正在启动 {GetModeDisplayName(SelectedTestMode)} 测试...");

        try
        {
            switch (SelectedTestMode)
            {
                case TestMode.Quick:
                    GlobalProgressService.Report(10, "正在运行快速模式...");
                    await RunQuickModeAsync(_cts.Token);
                    break;
                case TestMode.Stability:
                    GlobalProgressService.Report(10, "正在运行稳定性模式...");
                    await RunStabilityModeAsync(_cts.Token);
                    break;
                case TestMode.Deep:
                    GlobalProgressService.Report(10, "正在运行深度模式...");
                    await RunDeepModeAsync(_cts.Token);
                    break;
                case TestMode.Concurrency:
                    GlobalProgressService.Report(10, "正在运行并发模式...");
                    await RunConcurrencyModeAsync(_cts.Token);
                    break;
            }

            completed = true;
            GlobalProgressService.Complete();
        }
        catch (OperationCanceledException)
        {
            GlobalProgressService.Complete();
            // Partial results are already displayed by the individual mode methods.
            // Only update status if no mode-specific cancellation message was set.
            if (!IsCancellationStatus(StatusText))
            {
                StatusText = "测试已取消";
            }
        }
        catch (Exception ex)
        {
            GlobalProgressService.Complete();
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            sw.Stop();
            if (completed)
            {
                await RecordRunHistoryAsync(sw.Elapsed);
            }

            IsTesting = false;
            _cts = null;
            UpdateKpiLabels(SelectedTestMode);
        }
    }

    [RelayCommand]
    private void StopTest()
    {
        _cts?.Cancel();
        IsTesting = false;
        // StatusText will be updated by the mode-specific cancellation handler
        // with details about partial results. Only set a generic message as fallback.
        if (!IsCancellationStatus(StatusText))
        {
            StatusText = "测试已由用户取消";
        }
    }

    [RelayCommand]
    private async Task RunProxyAsync()
    {
        if (!IsTesting)
        {
            SelectedTestMode = TestMode.Quick;
        }

        await StartTestAsync();
    }

    [RelayCommand]
    private async Task RunProxyDeepAsync()
    {
        if (!IsTesting)
        {
            SelectedTestMode = TestMode.Deep;
        }

        await StartTestAsync();
    }

    [RelayCommand]
    private async Task RunProxySeriesAsync()
    {
        if (!IsTesting)
        {
            SelectedTestMode = TestMode.Stability;
        }

        await StartTestAsync();
    }

    [RelayCommand]
    private async Task RunSelectedSingleStationModeAsync()
        => await StartTestAsync();

    [RelayCommand]
    private void StopCurrentProxyTest()
        => StopTest();

    [RelayCommand]
    private void ToggleProxyCapabilityConfig()
    {
        IsProxyCapabilityConfigOpen = !IsProxyCapabilityConfigOpen;
        StatusText = IsProxyCapabilityConfigOpen
            ? "深度能力配置已展开"
            : "深度能力配置已收起";
    }

    [RelayCommand]
    private void OpenProxySingleChart()
    {
        IsProxyChartExpanded = true;
        StatusText = HasQuickResults || HasStabilityResults || HasDeepResults
            ? "单站图表已在当前页面展示。"
            : "当前还没有单站图表，请先运行测试。";
    }

    [RelayCommand]
    private void OpenProxyConcurrencyChart()
    {
        SelectedTestMode = TestMode.Concurrency;
        IsProxyChartExpanded = true;
        StatusText = HasConcurrencyResults
            ? "并发图表已在当前页面展示。"
            : "当前还没有并发图表，请先运行并发测试。";
    }

    [RelayCommand]
    private void OpenProxyTrendChart()
    {
        IsProxyChartExpanded = true;
        StatusText = HasQuickResults || HasStabilityResults || HasConcurrencyResults
            ? "趋势图表已在当前页面展示。"
            : "当前还没有趋势图表，请先运行测试。";
    }

    [RelayCommand]
    private void CloseProxyTrendChart()
    {
        IsProxyChartExpanded = false;
        StatusText = "趋势图表已收起。";
    }

    [RelayCommand]
    private async Task RetryProxyChartAsync()
        => await StartTestAsync();

    [RelayCommand]
    private void ToggleProxyChartView()
    {
        IsProxyChartExpanded = !IsProxyChartExpanded;
        StatusText = IsProxyChartExpanded ? "图表区域已展开" : "图表区域已切回紧凑视图";
    }

    [RelayCommand]
    private void ToggleProxyChartImageOnlyMode()
    {
        IsProxyChartImageOnlyMode = !IsProxyChartImageOnlyMode;
        StatusText = IsProxyChartImageOnlyMode ? "图表已切换为图像优先视图" : "图表已切换为指标+图表视图";
    }

    [RelayCommand]
    private void OpenProxyEndpointHistory()
        => ProxyEndpointHistoryOpenRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void CloseProxyEndpointHistory()
    {
        StatusText = "端点历史已关闭。";
    }

    [RelayCommand]
    private void ApplyProxyEndpointHistoryItem(EndpointHistoryItem? entry)
        => ApplyHistoryEntry(entry);

    [RelayCommand]
    private async Task ClearProxyEndpointHistoryAsync()
        => await ClearEndpointHistoryAsync();

    [RelayCommand]
    private void OpenProxyMultiModelPicker()
        => ProxyMultiModelPickerOpenRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void CloseProxyMultiModelPicker()
    {
        StatusText = "多模型选择器已关闭。";
    }

    [RelayCommand]
    private void CloseProxyModelPicker()
        => CloseProxyMultiModelPicker();

    [RelayCommand]
    private void ConfirmProxyMultiModelPicker(IEnumerable<string>? models)
    {
        SetMultiModelBenchmarkModels(models ?? []);
        StatusText = "多模型选择已应用。";
    }

    [RelayCommand]
    private void ClearProxyMultiModelSelection()
    {
        SetMultiModelBenchmarkModels([]);
        StatusText = "多模型测速选择已清空。";
    }

    /// <summary>
    /// Cancels the currently running test and displays partial results collected so far.
    /// This is an alias for StopTest that provides clearer semantics for cancellation.
    /// </summary>
    [RelayCommand]
    private void CancelTest()
    {
        StopTest();
    }

    [RelayCommand]
    private void CopyRawResponse()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(RawResponseJson);
            Clipboard.SetContent(package);
            StatusText = "\u539F\u59CB\u54CD\u5E94\u5DF2\u590D\u5236";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportRawResponse()
    {
        try
        {
            var exportRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RelayBench",
                "WinUI",
                "exports");
            Directory.CreateDirectory(exportRoot);

            var path = Path.Combine(exportRoot, $"single-station-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, RawResponseJson, System.Text.Encoding.UTF8);
            StatusText = $"\u5DF2\u5BFC\u51FA: {path}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleRawResponseExpanded()
    {
        IsRawResponseExpanded = !IsRawResponseExpanded;
        StatusText = IsRawResponseExpanded
            ? "\u54CD\u5E94\u67E5\u770B\u5668\u5DF2\u5C55\u5F00"
            : "\u54CD\u5E94\u67E5\u770B\u5668\u5DF2\u6536\u8D77";
    }

    private async Task RecordRunHistoryAsync(TimeSpan elapsed)
    {
        try
        {
            var score = EstimateHistoryScore();
            await RunHistoryRecorder.RecordAsync(
                "单站测试",
                BaseUrl.Trim(),
                $"{GetModeDisplayName(SelectedTestMode)}: {VerdictDisplay} - {StatusText}",
                score,
                (int)elapsed.TotalMilliseconds,
                RawResponseJson);
        }
        catch
        {
            // History should never block the active diagnostics workflow.
        }
    }

    private double? EstimateHistoryScore()
    {
        if (SelectedTestMode == TestMode.Stability &&
            StabilityHealthScore.Split('/').FirstOrDefault() is { } healthText &&
            double.TryParse(healthText, out var healthScore))
        {
            return healthScore;
        }

        if (string.Equals(Verdict, "Pass", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (string.Equals(Verdict, "Fail", StringComparison.OrdinalIgnoreCase))
        {
            return 35;
        }

        return null;
    }

}
