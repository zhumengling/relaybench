using System.Collections.ObjectModel;
using System.Text;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isOfficialApiTraceDialogOpen;
    private string _officialApiTraceDialogTitle = "\u7F51\u9875 API \u539F\u59CB Trace";
    private string _officialApiTraceDialogContent = "\u8BF7\u5148\u8FD0\u884C\u4E00\u6B21\u7F51\u9875 API \u68C0\u6D4B\uFF0C\u7136\u540E\u518D\u67E5\u770B\u539F\u59CB\u8FD4\u56DE\u3002";

    public ObservableCollection<OfficialApiStatusRowViewModel> OfficialApiStatusRows { get; } = [];

    public bool IsOfficialApiTraceDialogOpen
    {
        get => _isOfficialApiTraceDialogOpen;
        private set => SetProperty(ref _isOfficialApiTraceDialogOpen, value);
    }

    public string OfficialApiTraceDialogTitle
    {
        get => _officialApiTraceDialogTitle;
        private set => SetProperty(ref _officialApiTraceDialogTitle, value);
    }

    public string OfficialApiTraceDialogContent
    {
        get => _officialApiTraceDialogContent;
        private set => SetProperty(ref _officialApiTraceDialogContent, value);
    }

    public bool HasOfficialApiStatusRows => OfficialApiStatusRows.Count > 0;

    public AsyncRelayCommand CloseOfficialApiTraceDialogCommand { get; private set; } = null!;

    private Task CloseOfficialApiTraceDialogAsync()
    {
        IsOfficialApiTraceDialogOpen = false;
        return Task.CompletedTask;
    }

    private Task OpenOfficialApiTraceDialogAsync(string title, string content)
    {
        OfficialApiTraceDialogTitle = title;
        OfficialApiTraceDialogContent = string.IsNullOrWhiteSpace(content)
            ? "\u672c\u6b21\u672a\u6355\u83b7\u5230\u53ef\u5c55\u793a\u7684\u539f\u59cb\u8fd4\u56de\u5185\u5bb9\u3002"
            : content;
        IsOfficialApiTraceDialogOpen = true;
        return Task.CompletedTask;
    }

    private void RefreshOfficialApiStatusRows(UnlockCatalogResult result)
    {
        OfficialApiStatusRows.Clear();

        foreach (var check in result.Checks
                     .OrderBy(check => check.Provider, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(check => check.Name, StringComparer.OrdinalIgnoreCase))
        {
            OfficialApiStatusRows.Add(BuildOfficialApiStatusRow(check));
        }

        OnPropertyChanged(nameof(HasOfficialApiStatusRows));
    }

    private OfficialApiStatusRowViewModel BuildOfficialApiStatusRow(UnlockEndpointCheck check)
    {
        var availabilityText = BuildOfficialApiAvailabilityText(check);
        var summary = string.IsNullOrWhiteSpace(check.SemanticSummary)
            ? check.Summary
            : check.SemanticSummary;
        var endpointMetaText =
            $"{check.Method} \u00B7 HTTP {check.StatusCode?.ToString() ?? "--"} \u00B7 {FormatMilliseconds(check.Latency)}";
        var traceTitle = $"{check.Name} \u00B7 \u539f\u59cb Trace";
        var traceContent = BuildOfficialApiTraceContent(check);
        var (statusBackground, statusForeground) = ResolveOfficialApiStatusColors(check);

        return new OfficialApiStatusRowViewModel(
            check.Provider,
            check.Name,
            availabilityText,
            summary,
            endpointMetaText,
            statusBackground,
            statusForeground,
            () => OpenOfficialApiTraceDialogAsync(traceTitle, traceContent));
    }

    private static string BuildOfficialApiAvailabilityText(UnlockEndpointCheck check)
        => check.SemanticCategory switch
        {
            "Ready" => "\u53ef\u4ee5\u8bbf\u95ee",
            "AuthRequired" => "\u53ef\u8fde\u901a\uff0c\u9700\u9274\u6743",
            "RegionRestricted" => "\u53ef\u8fde\u901a\uff0c\u4f46\u7591\u4f3c\u53d7\u9650",
            "ReviewRequired" => check.Reachable ? "\u5df2\u54cd\u5e94\uff0c\u9700\u590d\u6838" : "\u4e0d\u53ef\u8bbf\u95ee",
            "Unreachable" => "\u4e0d\u53ef\u8bbf\u95ee",
            _ => check.Reachable ? "\u5df2\u54cd\u5e94\uff0c\u9700\u590d\u6838" : "\u4e0d\u53ef\u8bbf\u95ee"
        };

    private static (string Background, string Foreground) ResolveOfficialApiStatusColors(UnlockEndpointCheck check)
        => check.SemanticCategory switch
        {
            "Ready" => ("#ECFDF3", "#027A48"),
            "AuthRequired" => ("#FFF7E8", "#B54708"),
            "RegionRestricted" => ("#FEF3F2", "#B42318"),
            "ReviewRequired" => ("#F2F4F7", "#344054"),
            "Unreachable" => ("#FEF3F2", "#B42318"),
            _ => ("#F2F4F7", "#344054")
        };

    private static string BuildOfficialApiTraceContent(UnlockEndpointCheck check)
    {
        StringBuilder builder = new();
        builder.AppendLine($"\u540d\u79f0\uff1a{check.Name}");
        builder.AppendLine($"\u5382\u5546\uff1a{check.Provider}");
        builder.AppendLine($"URL\uff1a{check.Url}");
        builder.AppendLine($"\u65b9\u6cd5\uff1a{check.Method}");
        builder.AppendLine($"HTTP \u72b6\u6001\uff1a{check.StatusCode?.ToString() ?? "--"}");
        builder.AppendLine($"\u7f51\u7edc\u53ef\u8fbe\uff1a{(check.Reachable ? "\u662f" : "\u5426")}");
        builder.AppendLine($"\u5ef6\u8fdf\uff1a{FormatMilliseconds(check.Latency)}");
        builder.AppendLine($"\u7f51\u7edc\u7ed3\u8bba\uff1a{check.Verdict}");
        builder.AppendLine($"\u8bed\u4e49\u5206\u7c7b\uff1a{TranslateSemanticCategory(check.SemanticCategory)}");
        builder.AppendLine($"\u8bed\u4e49\u7ed3\u8bba\uff1a{check.SemanticVerdict}");
        builder.AppendLine();
        builder.AppendLine("\u7f51\u7edc\u6458\u8981\uff1a");
        builder.AppendLine(check.Summary);
        builder.AppendLine();
        builder.AppendLine("\u8bed\u4e49\u8bf4\u660e\uff1a");
        builder.AppendLine(check.SemanticSummary);
        builder.AppendLine();
        builder.AppendLine("\u539f\u59cb\u8bc1\u636e / \u54cd\u5e94\u7247\u6bb5\uff1a");
        builder.AppendLine(string.IsNullOrWhiteSpace(check.Evidence) ? "\u65e0" : check.Evidence);
        builder.AppendLine();
        builder.AppendLine($"\u6700\u7ec8\u5730\u5740\uff1a{check.FinalUrl ?? "\u65e0"}");
        builder.AppendLine($"\u5185\u5bb9\u7c7b\u578b\uff1a{check.ResponseContentType ?? "\u65e0"}");
        builder.AppendLine($"\u9519\u8bef\uff1a{check.Error ?? "\u65e0"}");
        return builder.ToString().TrimEnd();
    }
}
