using System.Collections.ObjectModel;
using System.Text;
using RelayBench.App.Services;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly int[] DefaultProxyConcurrencyStagePlan = [1, 2, 4, 8, 16];
    private const int DefaultProxyConcurrencyStageCycles = 2;

    private ProxyConcurrencyPressureResult? _lastProxyConcurrencyResult;
    private string _proxyConcurrencySummary = "\u5E76\u53D1\u538B\u6D4B\u5C1A\u672A\u8FD0\u884C\u3002";
    private string _proxyConcurrencyDetail = "\u6682\u65E0\u5E76\u53D1\u538B\u6D4B\u7ED3\u679C\u3002";

    public ObservableCollection<ProxyConcurrencyPressureStageResult> ProxyConcurrencyStages { get; } = [];

    public string ProxyConcurrencySummary
    {
        get => _proxyConcurrencySummary;
        private set
        {
            if (SetProperty(ref _proxyConcurrencySummary, value))
            {
                OnPropertyChanged(nameof(SingleStationResultSummary));
            }
        }
    }

    public string ProxyConcurrencyDetail
    {
        get => _proxyConcurrencyDetail;
        private set
        {
            if (SetProperty(ref _proxyConcurrencyDetail, value))
            {
                OnPropertyChanged(nameof(SingleStationResultDetail));
            }
        }
    }

    public bool HasProxyConcurrencyStages
        => ProxyConcurrencyStages.Count > 0;

    public string ProxyConcurrencyPlanSummary
        => $"\u56FA\u5B9A\u6863\u4F4D\uFF1A{string.Join(" / ", DefaultProxyConcurrencyStagePlan)}\uFF1B\u6BCF\u6863 {DefaultProxyConcurrencyStageCycles} \u8F6E\uFF0C\u603B\u8BF7\u6C42\u6570 = \u5E76\u53D1\u6570 \u00D7 {DefaultProxyConcurrencyStageCycles}\u3002";

    public string ProxyConcurrencyThresholdHint
        => "\u5224\u5B9A\u89C4\u5219\uFF1A\u6210\u529F\u7387 \u2265 95% \u4E14 429 = 0 \u3001\u8D85\u65F6 = 0 \u89C6\u4E3A\u7A33\u5B9A\u6863\uFF1B\u6210\u529F\u7387 < 80% \u3001\u51FA\u73B0\u8D85\u65F6\uFF0C\u6216 p95 TTFT \u8D85\u8FC7\u57FA\u7EBF 2 \u500D\u89C6\u4E3A\u9AD8\u98CE\u9669\u3002";

    private Task RunProxyConcurrencyPressureAsync()
        => ExecuteProxyBusyActionAsync(
            "\u6B63\u5728\u8FD0\u884C\u63A5\u53E3\u5E76\u53D1\u538B\u6D4B...",
            RunProxyConcurrencyPressureCoreAsync,
            "\u5E76\u53D1\u538B\u6D4B",
            "\u51C6\u5907\u4E2D",
            6d);

    private async Task RunProxyConcurrencyPressureCoreAsync(CancellationToken cancellationToken)
    {
        var settings = BuildProxySettings();
        SetProxyChartRetryMode(ProxyChartRetryMode.None, ProxyChartRetryButtonText);
        ReplaceProxyConcurrencyStages(Array.Empty<ProxyConcurrencyPressureStageResult>());
        _lastProxyConcurrencyResult = null;
        ResetProxyConcurrencyChartSnapshot();
        ProxyConcurrencySummary =
            $"\u5E76\u53D1\u538B\u6D4B\u5DF2\u542F\u52A8\uFF1A{ProxyConcurrencyPlanSummary}";
        ProxyConcurrencyDetail = BuildProxyConcurrencyDetailText(
            settings.BaseUrl,
            settings.Model,
            ProxyConcurrencyStages,
            "\u6B63\u5728\u7B49\u5F85\u7B2C 1 \u4E2A\u5E76\u53D1\u6863\u4F4D\u5B8C\u6210\u3002",
            null);
        UpdateGlobalTaskProgress("\u51C6\u5907\u4E2D", 8d);

        var stageProgress = new Progress<ProxyConcurrencyPressureStageResult>(stage =>
        {
            ProxyConcurrencyStages.Add(stage);
            OnPropertyChanged(nameof(HasProxyConcurrencyStages));
            ProxyConcurrencySummary =
                $"\u5E76\u53D1\u538B\u6D4B\u8FDB\u884C\u4E2D\uFF1A\u5DF2\u5B8C\u6210 {ProxyConcurrencyStages.Count}/{DefaultProxyConcurrencyStagePlan.Length} \u4E2A\u6863\u4F4D\uFF0C\u6700\u8FD1\u5B8C\u6210 {stage.Concurrency} \u5E76\u53D1\u3002";
            ProxyConcurrencyDetail = BuildProxyConcurrencyDetailText(
                settings.BaseUrl,
                settings.Model,
                ProxyConcurrencyStages,
                $"\u6700\u8FD1\u5B8C\u6210\u6863\u4F4D\uFF1A{stage.Concurrency}\uFF0C{stage.Summary}",
                null);
            RefreshProxyConcurrencyChartSnapshot(settings.BaseUrl, settings.Model, ProxyConcurrencyStages, ProxyConcurrencySummary, null, activate: true);
            AutoOpenProxyTrendChartIfAllowed();
            UpdateGlobalTaskProgress(
                ProxyConcurrencyStages.Count,
                DefaultProxyConcurrencyStagePlan.Length,
                $"\u6863 {ProxyConcurrencyStages.Count}/{DefaultProxyConcurrencyStagePlan.Length}");
            StatusMessage =
                $"\u5DF2\u5B8C\u6210\u5E76\u53D1\u6863\u4F4D {stage.Concurrency}\uFF0C\u6B63\u5728\u7EE7\u7EED\u540E\u7EED\u6863\u4F4D...";
        });

        var result = await _proxyDiagnosticsService.RunConcurrencyPressureAsync(
            settings,
            DefaultProxyConcurrencyStagePlan,
            stageProgress: stageProgress,
            cancellationToken: cancellationToken);

        ApplyProxyConcurrencyResult(result);
        DashboardCards[3].Status = BuildProxyConcurrencyDashboardStatus(result);
        DashboardCards[3].Detail = result.Summary;
        StatusMessage = result.Summary;
        AppendHistory("\u63A5\u53E3", "\u5E76\u53D1\u538B\u6D4B", ProxyConcurrencySummary);
    }

    private void ApplyProxyConcurrencyResult(ProxyConcurrencyPressureResult result)
    {
        _lastProxyConcurrencyResult = result;
        ReplaceProxyConcurrencyStages(result.Stages);
        ProxyConcurrencySummary = BuildProxyConcurrencySummaryText(result);
        ProxyConcurrencyDetail = BuildProxyConcurrencyDetailText(
            result.BaseUrl,
            result.Model,
            result.Stages,
            result.Summary,
            result.Error);

        AppendModuleOutput(
            "\u63A5\u53E3\u5E76\u53D1\u538B\u6D4B",
            ProxyConcurrencySummary,
            ProxyConcurrencyDetail);
        RefreshProxyUnifiedOutput();
        RefreshProxyConcurrencyChartSnapshot(activate: true);
        AutoOpenProxyTrendChartIfAllowed();
    }

    private void ReplaceProxyConcurrencyStages(IEnumerable<ProxyConcurrencyPressureStageResult> stages)
    {
        ProxyConcurrencyStages.Clear();
        foreach (var stage in stages)
        {
            ProxyConcurrencyStages.Add(stage);
        }

        OnPropertyChanged(nameof(HasProxyConcurrencyStages));
    }

    private string BuildProxyConcurrencySummaryText(ProxyConcurrencyPressureResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"\u68C0\u6D4B\u65F6\u95F4\uFF1A{result.TestedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"\u63A5\u53E3\u5730\u5740\uFF1A{result.BaseUrl}");
        builder.AppendLine($"\u6A21\u578B\uFF1A{(string.IsNullOrWhiteSpace(result.Model) ? "--" : result.Model)}");
        builder.AppendLine($"\u56FA\u5B9A\u6863\u4F4D\uFF1A{(result.Stages.Count == 0 ? string.Join(" / ", DefaultProxyConcurrencyStagePlan) : string.Join(" / ", result.Stages.Select(static stage => stage.Concurrency)))}");
        builder.AppendLine($"\u7A33\u5B9A\u5E76\u53D1\u4E0A\u9650\uFF1A{FormatProxyConcurrencyValue(result.StableConcurrencyLimit)}");
        builder.AppendLine($"\u9650\u6D41\u8D77\u70B9\uFF1A{FormatProxyConcurrencyValue(result.RateLimitStartConcurrency)}");
        builder.AppendLine($"\u9AD8\u98CE\u9669\u6863\uFF1A{FormatProxyConcurrencyValue(result.HighRiskConcurrency)}");
        builder.AppendLine($"\u6458\u8981\uFF1A{result.Summary}");
        builder.Append($"\u9519\u8BEF\uFF1A{result.Error ?? "\u65E0"}");
        return builder.ToString();
    }

    private string BuildProxyConcurrencyDetailText(
        string? baseUrl,
        string? model,
        IEnumerable<ProxyConcurrencyPressureStageResult> stages,
        string? overallSummary,
        string? error)
    {
        var materializedStages = stages.ToArray();
        StringBuilder builder = new();
        builder.AppendLine($"\u63A5\u53E3\u5730\u5740\uFF1A{(string.IsNullOrWhiteSpace(baseUrl) ? "--" : baseUrl)}");
        builder.AppendLine($"\u6A21\u578B\uFF1A{(string.IsNullOrWhiteSpace(model) ? "--" : model)}");
        builder.AppendLine($"\u6267\u884C\u8BA1\u5212\uFF1A{ProxyConcurrencyPlanSummary}");
        builder.AppendLine($"\u9608\u503C\u8BF4\u660E\uFF1A{ProxyConcurrencyThresholdHint}");
        builder.AppendLine($"\u603B\u4F53\u7ED3\u8BBA\uFF1A{overallSummary ?? "\u6B63\u5728\u6267\u884C\u5E76\u53D1\u538B\u6D4B..."}");
        builder.AppendLine($"\u9519\u8BEF\uFF1A{error ?? "\u65E0"}");
        builder.AppendLine();
        builder.AppendLine("\u3010\u5206\u6863\u7ED3\u679C\u3011");

        if (materializedStages.Length == 0)
        {
            builder.AppendLine("\u6682\u65E0\u5DF2\u5B8C\u6210\u7684\u5E76\u53D1\u6863\u4F4D\u3002");
        }
        else
        {
            foreach (var stage in materializedStages)
            {
                builder.AppendLine(stage.Summary);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildProxyConcurrencyDashboardStatus(ProxyConcurrencyPressureResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error) && result.Stages.Count == 0)
        {
            return "\u5931\u8D25";
        }

        if (result.StableConcurrencyLimit is int stableConcurrency)
        {
            return $"\u7A33 {stableConcurrency}";
        }

        if (result.RateLimitStartConcurrency is int rateLimitConcurrency)
        {
            return $"\u9650 {rateLimitConcurrency}";
        }

        return "\u5DF2\u538B\u6D4B";
    }

    private static string FormatProxyConcurrencyValue(int? value)
        => value?.ToString() ?? "--";

    private void ResetProxyConcurrencyChartSnapshot()
    {
        _proxyConcurrencyChartSnapshot = null;
        OnPropertyChanged(nameof(ProxyConcurrencyChartImage));
        OnPropertyChanged(nameof(HasProxyConcurrencyChart));
        OnPropertyChanged(nameof(ProxyConcurrencyChartStatusSummary));
    }

    private void RefreshProxyConcurrencyChartSnapshot(bool activate)
    {
        var result = _lastProxyConcurrencyResult;
        if (result is null || result.Stages.Count == 0)
        {
            return;
        }

        RefreshProxyConcurrencyChartSnapshot(
            result.BaseUrl,
            result.Model,
            result.Stages,
            result.Summary,
            result.Error,
            activate,
            result.StableConcurrencyLimit,
            result.RateLimitStartConcurrency,
            result.HighRiskConcurrency);
    }

    private void RefreshProxyConcurrencyChartSnapshot(
        string? baseUrl,
        string? model,
        IEnumerable<ProxyConcurrencyPressureStageResult> stages,
        string? overallSummary,
        string? error,
        bool activate,
        int? stableConcurrencyLimit = null,
        int? rateLimitStartConcurrency = null,
        int? highRiskConcurrency = null)
    {
        var stageArray = stages
            .OrderBy(static item => item.Concurrency)
            .ToArray();

        if (stageArray.Length == 0)
        {
            return;
        }

        var items = BuildProxyConcurrencyChartItems(
            stageArray,
            stableConcurrencyLimit,
            rateLimitStartConcurrency,
            highRiskConcurrency);
        var chartResult = _proxyConcurrencyChartRenderService.Render(
            baseUrl ?? string.Empty,
            model,
            items,
            overallSummary ?? string.Empty,
            stableConcurrencyLimit,
            rateLimitStartConcurrency,
            highRiskConcurrency,
            error,
            ResolvePreferredConcurrencyChartWidth());

        SetProxyChartSnapshot(
            ProxyChartViewMode.ConcurrencyPressure,
            new ProxyChartDialogSnapshot(
                "\u63A5\u53E3\u5E76\u53D1\u538B\u6D4B\u56FE\u8868",
                "\u56FA\u5B9A\u4EE5 1 / 2 / 4 / 8 / 16 \u6863\u5E76\u53D1\u538B\u6D4B\uFF0C\u56FE\u91CC\u4F1A\u540C\u65F6\u5BF9\u6BD4\u6210\u529F\u7387\u3001p50 \u666E\u901A\u5EF6\u8FDF\u3001p95 TTFT \u548C tok/s\u3002",
                BuildProxyConcurrencyChartDialogSummary(
                    baseUrl,
                    model,
                    stageArray,
                    stableConcurrencyLimit,
                    rateLimitStartConcurrency,
                    highRiskConcurrency,
                    overallSummary,
                    error),
                BuildProxyConcurrencyChartCapabilitySummary(stageArray),
                BuildProxyConcurrencyChartCapabilityDetail(stageArray),
                "\u8BFB\u56FE\u8BF4\u660E\uFF1A\u7EFF\u6761\u8868\u793A\u6210\u529F\u7387\uFF1B\u84DD\u6761\u8868\u793A p50 \u666E\u901A\u5EF6\u8FDF\uFF1B\u6A59\u6761\u8868\u793A p95 TTFT\uFF1B\u7D2B\u6761\u8868\u793A tok/s\u3002",
                chartResult.Summary,
                "\u6B63\u5728\u7B49\u5F85\u5E76\u53D1\u538B\u6D4B\u56FE\u8868\u6570\u636E...",
                chartResult.ChartImage,
                chartResult.HitRegions),
            activate);
    }

    private static ProxyConcurrencyChartItem[] BuildProxyConcurrencyChartItems(
        IReadOnlyList<ProxyConcurrencyPressureStageResult> stages,
        int? stableConcurrencyLimit,
        int? rateLimitStartConcurrency,
        int? highRiskConcurrency)
        => stages
            .Select(stage =>
            {
                var successRate = stage.TotalRequests == 0
                    ? 0d
                    : Math.Round(stage.SuccessCount * 100d / stage.TotalRequests, 1);

                return new ProxyConcurrencyChartItem(
                    stage.Concurrency,
                    stage.TotalRequests,
                    stage.SuccessCount,
                    stage.RateLimitedCount,
                    stage.ServerErrorCount,
                    stage.TimeoutCount,
                    successRate,
                    stage.P50ChatLatencyMs,
                    stage.P95TtftMs,
                    stage.AverageTokensPerSecond,
                    BuildProxyConcurrencyVerdict(
                        stage,
                        successRate,
                        stableConcurrencyLimit,
                        rateLimitStartConcurrency,
                        highRiskConcurrency),
                    stage.Summary,
                    stableConcurrencyLimit == stage.Concurrency,
                    rateLimitStartConcurrency == stage.Concurrency,
                    highRiskConcurrency == stage.Concurrency);
            })
            .ToArray();

    private static string BuildProxyConcurrencyVerdict(
        ProxyConcurrencyPressureStageResult stage,
        double successRate,
        int? stableConcurrencyLimit,
        int? rateLimitStartConcurrency,
        int? highRiskConcurrency)
    {
        if (highRiskConcurrency == stage.Concurrency)
        {
            return "\u9AD8\u98CE\u9669";
        }

        if (rateLimitStartConcurrency == stage.Concurrency || stage.RateLimitedCount > 0)
        {
            return "\u9650\u6D41\u9884\u8B66";
        }

        if (stableConcurrencyLimit == stage.Concurrency)
        {
            return "\u7A33\u5B9A\u4E0A\u9650";
        }

        if (successRate >= 95d && stage.TimeoutCount == 0 && stage.ServerErrorCount == 0)
        {
            return "\u7A33\u5B9A";
        }

        if (successRate < 80d || stage.TimeoutCount > 0 || stage.ServerErrorCount > 0)
        {
            return "\u4E0D\u7A33";
        }

        return "\u89C2\u5BDF";
    }

    private static string BuildProxyConcurrencyChartDialogSummary(
        string? baseUrl,
        string? model,
        IReadOnlyList<ProxyConcurrencyPressureStageResult> stages,
        int? stableConcurrencyLimit,
        int? rateLimitStartConcurrency,
        int? highRiskConcurrency,
        string? overallSummary,
        string? error)
        => string.Join(
            "\n",
            $"\u76EE\u6807\uFF1A{(string.IsNullOrWhiteSpace(baseUrl) ? "--" : baseUrl)}",
            $"\u6A21\u578B\uFF1A{(string.IsNullOrWhiteSpace(model) ? "--" : model)}",
            $"\u5DF2\u5B8C\u6210\u6863\u4F4D\uFF1A{stages.Count}",
            $"\u7A33\u5B9A\u5E76\u53D1\u4E0A\u9650\uFF1A{FormatProxyConcurrencyValue(stableConcurrencyLimit)}",
            $"\u9650\u6D41\u8D77\u70B9\uFF1A{FormatProxyConcurrencyValue(rateLimitStartConcurrency)}",
            $"\u9AD8\u98CE\u9669\u6863\uFF1A{FormatProxyConcurrencyValue(highRiskConcurrency)}",
            $"\u7ED3\u8BBA\uFF1A{overallSummary ?? "\u6B63\u5728\u91C7\u96C6\u4E2D"}",
            $"\u9519\u8BEF\uFF1A{error ?? "\u65E0"}");

    private static string BuildProxyConcurrencyChartCapabilitySummary(IReadOnlyList<ProxyConcurrencyPressureStageResult> stages)
        => string.Join(
            "\n",
            stages.Select(stage =>
            {
                var successRate = stage.TotalRequests == 0
                    ? 0d
                    : stage.SuccessCount * 100d / stage.TotalRequests;
                return
                    $"x{stage.Concurrency}\uFF1A{successRate:F1}% / " +
                    $"429 {stage.RateLimitedCount} / 5xx {stage.ServerErrorCount} / TO {stage.TimeoutCount}";
            }));

    private static string BuildProxyConcurrencyChartCapabilityDetail(IReadOnlyList<ProxyConcurrencyPressureStageResult> stages)
        => string.Join(
            "\n",
            stages.Select(stage =>
                $"x{stage.Concurrency}\uFF1A" +
                $"p50 {FormatProxyConcurrencyMilliseconds(stage.P50ChatLatencyMs)} / " +
                $"p95 TTFT {FormatProxyConcurrencyMilliseconds(stage.P95TtftMs)} / " +
                $"tok/s {FormatProxyConcurrencyTokensPerSecond(stage.AverageTokensPerSecond)} / " +
                $"{stage.Summary}"));

    private static string FormatProxyConcurrencyMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:F0} ms" : "--";

    private static string FormatProxyConcurrencyTokensPerSecond(double? value)
        => value.HasValue ? $"{value.Value:F1} tok/s" : "--";
}
