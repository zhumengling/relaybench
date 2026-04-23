using System.Text;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private enum ProxyBatchKeySource
    {
        SiteGroup,
        Entry
    }

    private enum ProxyBatchEditorMode
    {
        SharedKeyGroup,
        MultiKey,
        BulkImport
    }

    private enum ProxyBatchProbeStage
    {
        Baseline,
        Throughput,
        Completed
    }

    private sealed record ProxyBatchSiteGroupContext(
        string Name,
        string? ApiKey,
        string? Model);

    private sealed record ProxyBatchSourceEntry(
        string Name,
        string BaseUrl,
        string? ApiKey,
        string? Model,
        bool IncludeInBatchTest,
        string? SiteGroupName,
        string? SiteGroupApiKey,
        string? SiteGroupModel);

    private sealed record ProxyBatchPlan(
        IReadOnlyList<ProxyBatchSourceEntry> SourceEntries,
        IReadOnlyList<ProxyBatchTargetEntry> Targets,
        bool UsesFallbackDefaultEntry);

    private sealed record ProxyBatchTargetEntry(
        string Name,
        string BaseUrl,
        string ApiKey,
        string ApiKeyAlias,
        string Model,
        string SourceEntryName,
        string? SiteGroupName,
        ProxyBatchKeySource KeySource);

    private sealed record ProxyBatchIndexedTargetEntry(
        int Index,
        ProxyBatchTargetEntry Entry);

    private sealed record ProxyBatchExecutionBucket(
        string Key,
        IReadOnlyList<ProxyBatchIndexedTargetEntry> Entries);

    private sealed record ProxyBatchProbeRow(
        ProxyBatchTargetEntry Entry,
        ProxyDiagnosticsResult Result,
        int Score,
        ProxyBatchProbeStage Stage,
        int CompletedBaselineScenarioCount,
        int TotalBaselineScenarioCount,
        bool IsPlaceholder = false,
        string? PlaceholderMessage = null);

}
