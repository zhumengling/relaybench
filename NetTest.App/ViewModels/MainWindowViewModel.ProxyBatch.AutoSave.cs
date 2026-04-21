namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void ExecuteWithoutProxyBatchSiteGroupSelectionHandling(Action action)
    {
        var wasSuppressed = _suppressProxyBatchSiteGroupSelectionHandling;
        _suppressProxyBatchSiteGroupSelectionHandling = true;
        try
        {
            action();
        }
        finally
        {
            _suppressProxyBatchSiteGroupSelectionHandling = wasSuppressed;
        }
    }

    private bool TryAutoSaveSelectedProxyBatchSiteGroupDraft(out string? savedSiteName)
    {
        savedSiteName = null;
        if (SelectedProxyBatchSiteGroup is null)
        {
            return true;
        }

        try
        {
            if (!HasSelectedProxyBatchSiteGroupDraftChanges())
            {
                return true;
            }

            var commitResult = CommitProxyBatchDraftCore(
                resetDraftAfterSave: false,
                clearSelectionAfterSave: false);
            savedSiteName = commitResult.SiteName;
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"\u5f53\u524d\u7ad9\u70b9\u672a\u4fdd\u5b58\uff1a{ex.Message}";
            return false;
        }
    }

    private bool HasSelectedProxyBatchSiteGroupDraftChanges()
    {
        var selectedSiteGroup = SelectedProxyBatchSiteGroup;
        if (selectedSiteGroup is null)
        {
            return false;
        }

        if (!HasMeaningfulProxyBatchTemplateDraftRows())
        {
            return false;
        }

        var currentItems = GetProxyBatchEditorItemsByGroupName(selectedSiteGroup.GroupName);
        if (currentItems.Count == 0)
        {
            return false;
        }

        var draftItems = BuildCommittedProxyBatchSiteItemsFromDraft();
        if (draftItems.Count != currentItems.Count)
        {
            return true;
        }

        for (var index = 0; index < draftItems.Count; index++)
        {
            if (!string.Equals(
                    BuildProxyBatchComparableItemKey(draftItems[index]),
                    BuildProxyBatchComparableItemKey(currentItems[index]),
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private (string SiteName, bool ReplacedExistingSite) CommitProxyBatchDraftCore(
        bool resetDraftAfterSave,
        bool clearSelectionAfterSave)
    {
        var siteItems = BuildCommittedProxyBatchSiteItemsFromDraft();
        var siteName = siteItems[0].SiteGroupName ?? BuildProxyBatchTemplateSiteName(siteItems);
        var selectedGroupName = SelectedProxyBatchSiteGroup?.GroupName;
        var mergedItems = ProxyBatchEditorItems
            .Select(CloneProxyBatchEditorItem)
            .ToList();

        var replacedSelectedSite = false;
        var replacedSameNameSite = false;

        if (!string.IsNullOrWhiteSpace(selectedGroupName))
        {
            replacedSelectedSite = RemoveProxyBatchSiteGroup(mergedItems, selectedGroupName);
        }

        if (RemoveProxyBatchSiteGroup(mergedItems, siteName))
        {
            replacedSameNameSite = true;
        }

        mergedItems.AddRange(siteItems.Select(CloneProxyBatchEditorItem));
        if (mergedItems.Count > MaxProxyBatchSourceEntries)
        {
            throw new InvalidOperationException($"\u4fdd\u5b58\u540e\u4f1a\u8d85\u8fc7 {MaxProxyBatchSourceEntries} \u4e2a\u7f51\u5740\uff0c\u8bf7\u5148\u5220\u9664\u4e0d\u7528\u7684\u7ad9\u70b9\u6216\u7f29\u51cf\u5f53\u524d\u8868\u683c\u884c\u6570\u3002");
        }

        ExecuteWithoutProxyBatchSiteGroupSelectionHandling(() =>
        {
            ApplyProxyBatchEditorItems(mergedItems
                .Select(CreateProxyBatchConfigItemSnapshot)
                .ToArray());
            RebuildProxyBatchTargetsTextFromEditorItems(persistState: false);

            SelectedProxyBatchEditorItem = null;

            if (resetDraftAfterSave)
            {
                ResetProxyBatchTemplateDraft(clearSelection: clearSelectionAfterSave);
                return;
            }

            if (clearSelectionAfterSave)
            {
                SelectedProxyBatchSiteGroup = null;
                return;
            }

            SelectedProxyBatchSiteGroup = ProxyBatchSiteGroups
                .FirstOrDefault(item => string.Equals(item.GroupName, siteName, StringComparison.OrdinalIgnoreCase));
        });

        SaveState();
        return (siteName, replacedSelectedSite || replacedSameNameSite);
    }

    private IReadOnlyList<ProxyBatchEditorItemViewModel> GetProxyBatchEditorItemsByGroupName(string groupName)
        => ProxyBatchEditorItems
            .Where(item => string.Equals(ResolveProxyBatchSiteGroupName(item), groupName, StringComparison.OrdinalIgnoreCase))
            .Select(CloneProxyBatchEditorItem)
            .ToArray();

    private bool HasMeaningfulProxyBatchTemplateDraftRows()
        => ProxyBatchTemplateDraftItems.Any(item => !IsEmptyProxyBatchTemplateDraftRow(item));

    private static string BuildProxyBatchComparableItemKey(ProxyBatchEditorItemViewModel item)
    {
        var snapshot = CreateProxyBatchConfigItemSnapshot(item);
        return string.Join(
            "\u001f",
            snapshot.SiteGroupName,
            snapshot.EntryName,
            snapshot.BaseUrl,
            snapshot.EntryApiKey,
            snapshot.EntryModel,
            snapshot.IncludeInBatchTest ? "1" : "0");
    }
}
