namespace NetTest.App.ViewModels;

public sealed class ProxyBatchSiteGroupViewModel
{
    public ProxyBatchSiteGroupViewModel(
        string groupName,
        int entryCount,
        string baseUrlPreview,
        string keySummary,
        string modelSummary)
    {
        GroupName = groupName;
        EntryCount = entryCount;
        BaseUrl = baseUrlPreview;
        KeyDisplay = keySummary;
        ModelDisplay = modelSummary;
    }

    public string GroupName { get; }

    public int EntryCount { get; }

    public string DisplayTitle => GroupName;

    public string SiteGroupDisplay => $"{EntryCount} 个网址";

    public string BaseUrl { get; }

    public string KeyDisplay { get; }

    public string ModelDisplay { get; }
}
