using System.Collections.ObjectModel;
using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isProxyCapabilityConfigOpen;
    private string _proxyEmbeddingsModel = string.Empty;
    private string _proxyImagesModel = string.Empty;
    private string _proxyAudioTranscriptionModel = string.Empty;
    private string _proxyAudioSpeechModel = string.Empty;
    private string _proxyModerationModel = string.Empty;
    private CapabilityMatrixCellViewModel? _selectedProxyCapabilityMatrixCell;
    private string _proxyCapabilityMatrixDetailText =
        "\u6682\u65E0\u975E\u804A\u5929 API \u80FD\u529B\u8BE6\u60C5\u3002";

    public ObservableCollection<CapabilityMatrixCellViewModel> ProxyCapabilityMatrixCells { get; } = [];

    public ObservableCollection<CapabilityMatrixCellViewModel> VisibleProxyCapabilityMatrixCells { get; } = [];

    public bool IsProxyCapabilityConfigOpen
    {
        get => _isProxyCapabilityConfigOpen;
        private set
        {
            if (SetProperty(ref _isProxyCapabilityConfigOpen, value))
            {
                OnPropertyChanged(nameof(ProxyCapabilityConfigButtonText));
            }
        }
    }

    public string ProxyCapabilityConfigButtonText
        => IsProxyCapabilityConfigOpen
            ? "\u6536\u8D77\u975E\u804A\u5929 API"
            : "\u975E\u804A\u5929 API...";

    public string ProxyEmbeddingsModel
    {
        get => _proxyEmbeddingsModel;
        set
        {
            if (SetProperty(ref _proxyEmbeddingsModel, value))
            {
                OnPropertyChanged(nameof(ProxyCapabilityModelConfigSummary));
                RefreshProxyCapabilityMatrixPlaceholders();
            }
        }
    }

    public string ProxyImagesModel
    {
        get => _proxyImagesModel;
        set
        {
            if (SetProperty(ref _proxyImagesModel, value))
            {
                OnPropertyChanged(nameof(ProxyCapabilityModelConfigSummary));
                RefreshProxyCapabilityMatrixPlaceholders();
            }
        }
    }

    public string ProxyAudioTranscriptionModel
    {
        get => _proxyAudioTranscriptionModel;
        set
        {
            if (SetProperty(ref _proxyAudioTranscriptionModel, value))
            {
                OnPropertyChanged(nameof(ProxyCapabilityModelConfigSummary));
                RefreshProxyCapabilityMatrixPlaceholders();
            }
        }
    }

    public string ProxyAudioSpeechModel
    {
        get => _proxyAudioSpeechModel;
        set
        {
            if (SetProperty(ref _proxyAudioSpeechModel, value))
            {
                OnPropertyChanged(nameof(ProxyCapabilityModelConfigSummary));
                RefreshProxyCapabilityMatrixPlaceholders();
            }
        }
    }

    public string ProxyModerationModel
    {
        get => _proxyModerationModel;
        set
        {
            if (SetProperty(ref _proxyModerationModel, value))
            {
                OnPropertyChanged(nameof(ProxyCapabilityModelConfigSummary));
                RefreshProxyCapabilityMatrixPlaceholders();
            }
        }
    }

    public string ProxyCapabilityModelConfigSummary
    {
        get
        {
            var configuredCount = GetConfiguredCapabilityMatrixDefinitions().Length;
            return
                $"\u5DF2\u914D\u7F6E {configuredCount}/5 \u9879\u3002" +
                "\u7559\u7A7A = \u672A\u914D\u7F6E\u3001\u672C\u8F6E\u4E0D\u53C2\u4E0E\u8BE5\u9879\u6D4B\u8BD5\u3002";
        }
    }

    public CapabilityMatrixCellViewModel? SelectedProxyCapabilityMatrixCell
    {
        get => _selectedProxyCapabilityMatrixCell;
        set
        {
            if (SetProperty(ref _selectedProxyCapabilityMatrixCell, value))
            {
                ProxyCapabilityMatrixDetailText = value?.DetailText
                    ?? "\u6682\u65E0\u975E\u804A\u5929 API \u80FD\u529B\u8BE6\u60C5\u3002";
            }
        }
    }

    public string ProxyCapabilityMatrixDetailText
    {
        get => _proxyCapabilityMatrixDetailText;
        private set => SetProperty(ref _proxyCapabilityMatrixDetailText, value);
    }

    public bool HasProxyCapabilityMatrixCells
        => VisibleProxyCapabilityMatrixCells.Count > 0;

    private Task ToggleProxyCapabilityConfigAsync()
    {
        IsProxyCapabilityConfigOpen = !IsProxyCapabilityConfigOpen;
        return Task.CompletedTask;
    }

    private void LoadProxyCapabilityMatrixState(AppStateSnapshot snapshot)
    {
        _proxyEmbeddingsModel = snapshot.ProxyEmbeddingsModel ?? string.Empty;
        _proxyImagesModel = snapshot.ProxyImagesModel ?? string.Empty;
        _proxyAudioTranscriptionModel = snapshot.ProxyAudioTranscriptionModel ?? string.Empty;
        _proxyAudioSpeechModel = snapshot.ProxyAudioSpeechModel ?? string.Empty;
        _proxyModerationModel = snapshot.ProxyModerationModel ?? string.Empty;

        OnPropertyChanged(nameof(ProxyEmbeddingsModel));
        OnPropertyChanged(nameof(ProxyImagesModel));
        OnPropertyChanged(nameof(ProxyAudioTranscriptionModel));
        OnPropertyChanged(nameof(ProxyAudioSpeechModel));
        OnPropertyChanged(nameof(ProxyModerationModel));
        OnPropertyChanged(nameof(ProxyCapabilityModelConfigSummary));

        RefreshProxyCapabilityMatrixPlaceholders(forceFromConfiguration: true);
    }

    private void ApplyProxyCapabilityMatrixStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.ProxyEmbeddingsModel = ProxyEmbeddingsModel;
        snapshot.ProxyImagesModel = ProxyImagesModel;
        snapshot.ProxyAudioTranscriptionModel = ProxyAudioTranscriptionModel;
        snapshot.ProxyAudioSpeechModel = ProxyAudioSpeechModel;
        snapshot.ProxyModerationModel = ProxyModerationModel;
    }

    private void RefreshProxyCapabilityMatrixPlaceholders(bool forceFromConfiguration = false)
    {
        var hasRuntimeResult = !forceFromConfiguration &&
                               _lastProxySingleResult is { } result &&
                               GetScenarioResults(result).Any(static scenario => IsProxyCapabilityMatrixScenario(scenario.Scenario));

        if (hasRuntimeResult && _lastProxySingleResult is not null)
        {
            ApplyProxyCapabilityMatrixResult(_lastProxySingleResult, appendToRelaySummaries: false);
            return;
        }

        ReplaceProxyCapabilityMatrixCells(BuildCapabilityMatrixCells(null));
    }

    private void ApplyProxyCapabilityMatrixResult(
        ProxyDiagnosticsResult result,
        bool appendToRelaySummaries)
    {
        var cells = BuildCapabilityMatrixCells(result);
        ReplaceProxyCapabilityMatrixCells(cells);

        if (!appendToRelaySummaries)
        {
            return;
        }

        var summarySection = BuildCapabilityMatrixSummarySection(cells);
        var detailSection = BuildCapabilityMatrixDetailSection(cells);
        var metricsSection = BuildCapabilityMatrixMetricSection(cells);

        ProxyCapabilityMatrixSummary =
            string.IsNullOrWhiteSpace(ProxyCapabilityMatrixSummary)
                ? summarySection
                : $"{ProxyCapabilityMatrixSummary}\n\n{summarySection}";

        ProxySingleCapabilityDetailSummary =
            string.IsNullOrWhiteSpace(ProxySingleCapabilityDetailSummary)
                ? detailSection
                : $"{ProxySingleCapabilityDetailSummary}\n\n{detailSection}";

        ProxyKeyMetricsSummary =
            string.IsNullOrWhiteSpace(ProxyKeyMetricsSummary)
                ? metricsSection
                : $"{ProxyKeyMetricsSummary}\n{metricsSection}";
    }

    private IReadOnlyList<CapabilityMatrixCellViewModel> BuildCapabilityMatrixCells(ProxyDiagnosticsResult? result)
    {
        var scenarios = result is null
            ? Array.Empty<ProxyProbeScenarioResult>()
            : GetScenarioResults(result);

        return GetCapabilityMatrixDefinitions()
            .Select(definition =>
            {
                var scenario = scenarios.FirstOrDefault(item => item.Scenario == definition.Scenario);
                return BuildCapabilityMatrixCell(definition, scenario);
            })
            .ToArray();
    }

    private CapabilityMatrixCellViewModel BuildCapabilityMatrixCell(
        CapabilityMatrixDefinition definition,
        ProxyProbeScenarioResult? scenario)
    {
        var configuredModel = GetCapabilityModel(definition.Scenario);
        var modelText = string.IsNullOrWhiteSpace(configuredModel)
            ? "\u672A\u914D\u7F6E"
            : configuredModel!.Trim();

        if (scenario is null)
        {
            var pending = !string.IsNullOrWhiteSpace(configuredModel);
            return new CapabilityMatrixCellViewModel(
                definition.Scenario,
                definition.Name,
                pending ? "\u5F85\u6267\u884C" : "\u672A\u914D\u7F6E",
                modelText,
                "--",
                "--",
                pending
                    ? "\u5DF2\u914D\u7F6E\u6A21\u578B\uFF0C\u7B49\u5F85\u6DF1\u6D4B\u6267\u884C\u3002"
                    : "\u7559\u7A7A\u5373\u4E0D\u53C2\u4E0E\u672C\u8F6E\u6D4B\u8BD5\u3002",
                BuildCapabilityMatrixCellDetail(
                    definition,
                    pending ? "\u5F85\u6267\u884C" : "\u672A\u914D\u7F6E",
                    modelText,
                    "--",
                    "--",
                    pending
                        ? "\u5DF2\u914D\u7F6E\u6A21\u578B\uFF0C\u7B49\u5F85\u6DF1\u5EA6\u6D4B\u8BD5\u8FD0\u884C\u5230\u8BE5\u9879\u3002"
                        : "\u672A\u586B\u5199\u8BE5\u80FD\u529B\u7684\u6A21\u578B\uFF0C\u5C06\u4EE5\u201C\u672A\u914D\u7F6E\u201D\u8BB0\u5F55\u3002",
                    null,
                    null));
        }

        var statusCodeText = scenario.StatusCode?.ToString() ?? "--";
        var latencyText = FormatMilliseconds(scenario.Latency);
        return new CapabilityMatrixCellViewModel(
            definition.Scenario,
            definition.Name,
            scenario.CapabilityStatus,
            modelText,
            statusCodeText,
            latencyText,
            scenario.Summary,
            BuildCapabilityMatrixCellDetail(
                definition,
                scenario.CapabilityStatus,
                modelText,
                statusCodeText,
                latencyText,
                scenario.Summary,
                scenario.Preview,
                scenario.Error));
    }

    private static string BuildCapabilityMatrixCellDetail(
        CapabilityMatrixDefinition definition,
        string stateText,
        string modelText,
        string statusCodeText,
        string latencyText,
        string summary,
        string? preview,
        string? error)
    {
        StringBuilder builder = new();
        builder.AppendLine($"{definition.Name}");
        builder.AppendLine($"\u8DEF\u5F84\uFF1A{definition.Path}");
        builder.AppendLine($"\u72B6\u6001\uFF1A{stateText}");
        builder.AppendLine($"\u6A21\u578B\uFF1A{modelText}");
        builder.AppendLine($"HTTP \u72B6\u6001\u7801\uFF1A{statusCodeText}");
        builder.AppendLine($"\u5EF6\u8FDF\uFF1A{latencyText}");
        builder.AppendLine($"\u7ED3\u8BBA\uFF1A{summary}");
        builder.AppendLine($"\u9884\u89C8\uFF1A{(string.IsNullOrWhiteSpace(preview) ? "--" : preview)}");
        builder.Append($"\u9519\u8BEF\uFF1A{(string.IsNullOrWhiteSpace(error) ? "\u65E0" : error)}");
        return builder.ToString();
    }

    private void ReplaceProxyCapabilityMatrixCells(IReadOnlyList<CapabilityMatrixCellViewModel> cells)
    {
        var previouslySelectedScenario = SelectedProxyCapabilityMatrixCell?.Scenario;
        var visibleCells = GetReportableCapabilityMatrixCells(cells);

        ProxyCapabilityMatrixCells.Clear();
        foreach (var cell in cells)
        {
            ProxyCapabilityMatrixCells.Add(cell);
        }

        VisibleProxyCapabilityMatrixCells.Clear();
        foreach (var cell in visibleCells)
        {
            VisibleProxyCapabilityMatrixCells.Add(cell);
        }

        OnPropertyChanged(nameof(HasProxyCapabilityMatrixCells));

        SelectedProxyCapabilityMatrixCell = previouslySelectedScenario.HasValue
            ? VisibleProxyCapabilityMatrixCells.FirstOrDefault(cell => cell.Scenario == previouslySelectedScenario.Value)
            : VisibleProxyCapabilityMatrixCells.FirstOrDefault();
    }

    private string BuildCapabilityMatrixSummarySection(IReadOnlyList<CapabilityMatrixCellViewModel> cells)
    {
        var visibleCells = GetReportableCapabilityMatrixCells(cells);
        if (visibleCells.Length == 0)
        {
            return "\u3010\u975E\u804A\u5929 API \u80FD\u529B\u77E9\u9635\u3011\n\u672C\u8F6E\u672A\u914D\u7F6E\u4EFB\u4F55\u80FD\u529B\u6A21\u578B\uFF0C\u5DF2\u8DF3\u8FC7\u3002";
        }

        StringBuilder builder = new();
        builder.AppendLine("\u3010\u975E\u804A\u5929 API \u80FD\u529B\u77E9\u9635\u3011");
        foreach (var cell in visibleCells)
        {
            builder.AppendLine(
                $"{cell.Name}\uFF1A{cell.StateText} / \u6A21\u578B {cell.ModelText} / HTTP {cell.StatusCodeText} / {cell.LatencyText}");
        }

        return builder.ToString().TrimEnd();
    }

    private string BuildCapabilityMatrixDetailSection(IReadOnlyList<CapabilityMatrixCellViewModel> cells)
    {
        var visibleCells = GetReportableCapabilityMatrixCells(cells);
        if (visibleCells.Length == 0)
        {
            return "\u3010\u975E\u804A\u5929 API \u8BE6\u60C5\u3011\n\u672C\u8F6E\u672A\u914D\u7F6E\u4EFB\u4F55\u80FD\u529B\u6A21\u578B\uFF0C\u5DF2\u8DF3\u8FC7\u3002";
        }

        StringBuilder builder = new();
        builder.AppendLine("\u3010\u975E\u804A\u5929 API \u8BE6\u60C5\u3011");
        foreach (var cell in visibleCells)
        {
            builder.AppendLine(cell.DetailText);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private string BuildCapabilityMatrixMetricSection(IReadOnlyList<CapabilityMatrixCellViewModel> cells)
    {
        var visibleCells = GetReportableCapabilityMatrixCells(cells);
        var configuredCount = visibleCells.Length;
        var passedCount = visibleCells.Count(cell => string.Equals(cell.StateText, "\u652F\u6301", StringComparison.Ordinal));
        var pendingCount = visibleCells.Count(cell => string.Equals(cell.StateText, "\u5F85\u6267\u884C", StringComparison.Ordinal));
        return
            $"\u975E\u804A\u5929 API \uFF1A\u5DF2\u914D\u7F6E {configuredCount}/5\uFF0C" +
            $"\u901A\u8FC7 {passedCount}\uFF0C\u5F85\u6267\u884C {pendingCount}\u3002";
    }

    private bool HasConfiguredProxyCapabilityModels()
        => GetConfiguredCapabilityMatrixDefinitions().Length > 0;

    private CapabilityMatrixDefinition[] GetConfiguredCapabilityMatrixDefinitions()
        => GetCapabilityMatrixDefinitions()
            .Where(definition => !string.IsNullOrWhiteSpace(GetCapabilityModel(definition.Scenario)))
            .ToArray();

    private static CapabilityMatrixCellViewModel[] GetReportableCapabilityMatrixCells(IReadOnlyList<CapabilityMatrixCellViewModel> cells)
        => cells
            .Where(cell => !string.Equals(cell.ModelText, "\u672A\u914D\u7F6E", StringComparison.Ordinal))
            .ToArray();

    private static bool IsCapabilityScenarioUnconfigured(ProxyProbeScenarioResult? scenario)
        => scenario is not null &&
           IsProxyCapabilityMatrixScenario(scenario.Scenario) &&
           string.Equals(scenario.CapabilityStatus, "\u672A\u914D\u7F6E", StringComparison.Ordinal);

    private string? GetCapabilityModel(ProxyProbeScenarioKind scenario)
        => scenario switch
        {
            ProxyProbeScenarioKind.Embeddings => NormalizeNullable(ProxyEmbeddingsModel),
            ProxyProbeScenarioKind.Images => NormalizeNullable(ProxyImagesModel),
            ProxyProbeScenarioKind.AudioTranscription => NormalizeNullable(ProxyAudioTranscriptionModel),
            ProxyProbeScenarioKind.AudioSpeech => NormalizeNullable(ProxyAudioSpeechModel),
            ProxyProbeScenarioKind.Moderation => NormalizeNullable(ProxyModerationModel),
            _ => null
        };

    private static CapabilityMatrixDefinition[] GetCapabilityMatrixDefinitions()
        =>
        [
            new(ProxyProbeScenarioKind.Embeddings, "Embeddings", "/v1/embeddings"),
            new(ProxyProbeScenarioKind.Images, "Images", "/v1/images/generations"),
            new(ProxyProbeScenarioKind.AudioTranscription, "Audio Transcription", "/v1/audio/transcriptions"),
            new(ProxyProbeScenarioKind.AudioSpeech, "Audio Speech / TTS", "/v1/audio/speech"),
            new(ProxyProbeScenarioKind.Moderation, "Moderation", "/v1/moderations")
        ];

    private static bool IsProxyCapabilityMatrixScenario(ProxyProbeScenarioKind scenario)
        => scenario is ProxyProbeScenarioKind.Embeddings or
                       ProxyProbeScenarioKind.Images or
                       ProxyProbeScenarioKind.AudioTranscription or
                       ProxyProbeScenarioKind.AudioSpeech or
                       ProxyProbeScenarioKind.Moderation;

    private sealed record CapabilityMatrixDefinition(
        ProxyProbeScenarioKind Scenario,
        string Name,
        string Path);
}
