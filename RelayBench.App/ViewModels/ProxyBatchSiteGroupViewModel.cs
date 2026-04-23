namespace RelayBench.App.ViewModels;

public sealed class ProxyBatchSiteGroupViewModel
{
    public ProxyBatchSiteGroupViewModel(
        string groupName,
        int entryCount,
        int enabledEntryCount,
        string baseUrlPreview,
        string keySummary,
        string modelSummary)
    {
        GroupName = groupName;
        EntryCount = entryCount;
        EnabledEntryCount = enabledEntryCount;
        BaseUrl = baseUrlPreview;
        KeyDisplay = keySummary;
        ModelDisplay = modelSummary;
    }

    public string GroupName { get; }

    public int EntryCount { get; }

    public int EnabledEntryCount { get; }

    public string DisplayTitle => GroupName;

    public string SiteGroupDisplay
        => EnabledEntryCount >= EntryCount
            ? $"{EntryCount} \u4e2a\u7f51\u5740"
            : $"\u542f\u7528 {EnabledEntryCount} / \u5171 {EntryCount}";

    public string BaseUrl { get; }

    public string KeyDisplay { get; }

    public string ModelDisplay { get; }
}
