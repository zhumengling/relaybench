using System.Text;
using System.Windows;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task RunProxyBatchAsync()
    {
        if (!TryAutoSaveSelectedProxyBatchSiteGroupDraft(out _))
        {
            return;
        }

        try
        {
            _ = BuildProxyBatchPlan(requireRunnable: true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"当前无法开始快速对比：{ex.Message}";
            MessageBox.Show(
                $"还不能开始快速对比。\n\n{ex.Message}",
                "请先补全入口信息",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await ExecuteProxyBusyActionAsync(
            "正在运行入口组检测...",
            RunProxyBatchCoreAsync,
            "\u6279\u91CF\u5FEB\u901F\u5BF9\u6BD4",
            "\u51C6\u5907\u4E2D",
            6d);
    }

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
        if (!TryAutoSaveSelectedProxyBatchSiteGroupDraft(out _))
        {
            return Task.CompletedTask;
        }

        ExecuteWithoutProxyBatchSiteGroupSelectionHandling(() => SelectedProxyBatchSiteGroup = null);
        SelectedProxyBatchEditorItem = null;
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
        var commitResult = CommitProxyBatchDraftCore(
            resetDraftAfterSave: true,
            clearSelectionAfterSave: true);

        StatusMessage = commitResult.ReplacedExistingSite
            ? $"\u5df2\u4fdd\u5b58\u7ad9\u70b9\uff1a{commitResult.SiteName}\u3002\u53f3\u4fa7\u5df2\u6e05\u7a7a\uff0c\u53ef\u7ee7\u7eed\u5f55\u5165\u4e0b\u4e00\u4e2a\u7ad9\u70b9\u3002"
            : $"\u5df2\u52a0\u5165\u7ad9\u70b9\uff1a{commitResult.SiteName}\u3002\u53f3\u4fa7\u5df2\u6e05\u7a7a\uff0c\u53ef\u7ee7\u7eed\u5f55\u5165\u4e0b\u4e00\u4e2a\u7ad9\u70b9\u3002";
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
