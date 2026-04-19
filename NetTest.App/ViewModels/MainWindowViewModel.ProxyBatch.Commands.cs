using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task RunProxyBatchAsync()
        => ExecuteProxyBusyActionAsync("正在运行入口组检测...", RunProxyBatchCoreAsync);

    private Task OpenProxyBatchEditorAsync()
    {
        SyncProxyBatchEditorItemsFromText();
        SetProxyBatchEditorMode(ProxyBatchEditorMode.BulkImport);
        ResetProxyBatchTemplateDraft(clearSelection: true);
        SelectedProxyBatchEditorItem = null;
        IsProxyBatchEditorOpen = true;
        StatusMessage = ProxyBatchSiteGroups.Count == 0
            ? "已打开站点录入面板，可开始录入第一个站点。"
            : "已打开站点录入面板；左侧可选已有站点，右侧可继续录入新站点。";
        return Task.CompletedTask;
    }

    private Task CloseProxyBatchEditorAsync()
    {
        IsProxyBatchEditorOpen = false;
        SaveState();
        return Task.CompletedTask;
    }

    private Task AddCurrentProxyBaseUrlToBatchAsync()
    {
        SetProxyBatchEditorMode(ProxyBatchEditorMode.BulkImport);
        ResetProxyBatchTemplateDraft(clearSelection: true);

        var firstRow = ProxyBatchTemplateDraftItems[0];
        var baseUrl = NormalizeNullable(ProxyBaseUrl) ?? string.Empty;
        firstRow.EntryName = string.IsNullOrWhiteSpace(baseUrl)
            ? string.Empty
            : BuildBatchDefaultName(baseUrl, 1);
        firstRow.BaseUrl = baseUrl;
        firstRow.EntryApiKey = NormalizeNullable(ProxyApiKey);
        firstRow.EntryModel = NormalizeNullable(ProxyModel);

        RefreshProxyBatchTemplateDraftState();
        StatusMessage = string.IsNullOrWhiteSpace(baseUrl)
            ? "主页还没有默认网址，已为你保留一个空白站点，可直接开始填写。"
            : "已把主页的网址、key 和模型带入当前站点的第一行。";
        SaveState();
        return Task.CompletedTask;
    }

    private Task CommitProxyBatchEditorItemAsync()
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
            throw new InvalidOperationException($"保存后会超过 {MaxProxyBatchSourceEntries} 个网址，请先删除不用的站点或缩减当前表格行数。");
        }

        ApplyProxyBatchEditorItems(mergedItems
            .Select(CreateProxyBatchConfigItemSnapshot)
            .ToArray());
        RebuildProxyBatchTargetsTextFromEditorItems(persistState: false);

        SelectedProxyBatchEditorItem = null;
        ResetProxyBatchTemplateDraft(clearSelection: true);

        StatusMessage = replacedSelectedSite || replacedSameNameSite
            ? $"已保存站点：{siteName}。右侧已清空，可继续录入下一个站点。"
            : $"已加入站点：{siteName}。右侧已清空，可继续录入下一个站点。";
        SaveState();
        return Task.CompletedTask;
    }

    private Task AddProxyBatchEditorItemAsync()
        => CommitProxyBatchEditorItemAsync();

    private Task UpdateProxyBatchEditorItemAsync()
        => CommitProxyBatchEditorItemAsync();

    private Task DeleteProxyBatchEditorItemAsync()
    {
        if (SelectedProxyBatchSiteGroup is null)
        {
            StatusMessage = "请先从左侧列表选中一个站点，再删除。";
            return Task.CompletedTask;
        }

        var groupName = SelectedProxyBatchSiteGroup.GroupName;
        var mergedItems = ProxyBatchEditorItems
            .Select(CloneProxyBatchEditorItem)
            .ToList();
        if (!RemoveProxyBatchSiteGroup(mergedItems, groupName))
        {
            StatusMessage = $"没有找到站点：{groupName}";
            return Task.CompletedTask;
        }

        ApplyProxyBatchEditorItems(mergedItems
            .Select(CreateProxyBatchConfigItemSnapshot)
            .ToArray());
        RebuildProxyBatchTargetsTextFromEditorItems(persistState: false);

        SelectedProxyBatchEditorItem = null;
        ResetProxyBatchTemplateDraft(clearSelection: true);
        StatusMessage = $"已删除站点：{groupName}。右侧已清空，可继续录入下一个站点。";
        SaveState();
        return Task.CompletedTask;
    }

    private Task ResetProxyBatchEditorFormAsync()
    {
        SetProxyBatchEditorMode(ProxyBatchEditorMode.BulkImport);
        SelectedProxyBatchEditorItem = null;
        ResetProxyBatchTemplateDraft(clearSelection: true);
        StatusMessage = "当前站点已清空，只保留第一行空白，可继续录入。";
        SaveState();
        return Task.CompletedTask;
    }

    private static bool RemoveProxyBatchSiteGroup(List<ProxyBatchEditorItemViewModel> items, string groupName)
    {
        var removed = false;
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(ResolveProxyBatchSiteGroupName(items[index]), groupName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.RemoveAt(index);
            removed = true;
        }

        return removed;
    }
}
