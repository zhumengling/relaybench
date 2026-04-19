using System.Text;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task RunProxyBatchAsync()
        => ExecuteBusyActionAsync("正在运行入口组检测...", RunProxyBatchCoreAsync);

    private Task OpenProxyBatchEditorAsync()
    {
        SyncProxyBatchEditorItemsFromText();
        SelectedProxyBatchEditorItem = null;
        SetProxyBatchEditorMode(ProxyBatchEditorMode.SharedKeyGroup);
        if (string.IsNullOrWhiteSpace(ProxyBatchFormBaseUrl))
        {
            LoadCurrentProxyConfigIntoBatchForm();
        }

        IsProxyBatchEditorOpen = true;
        /*
        // StatusMessage = mode == ProxyBatchEditorMode.SharedKeyGroup
            ? $"已加入 {item.DisplayTitle}，共用 Key 信息已保留，可继续补下一个入口。"
            : $"已加入 {item.DisplayTitle}，可以继续填写下一条。";
        */
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
        LoadCurrentProxyConfigIntoBatchForm();
        StatusMessage = "已把主页默认网址和 key 带入填写面板。";
        SaveState();
        return Task.CompletedTask;
    }

    private Task CommitProxyBatchEditorItemAsync()
        => SelectedProxyBatchEditorItem is null
            ? AddProxyBatchEditorItemAsync()
            : UpdateProxyBatchEditorItemAsync();

    private Task AddProxyBatchEditorItemAsync()
    {
        var mode = ResolveEffectiveProxyBatchEditorMode();
        SetProxyBatchEditorMode(mode);
        var item = BuildProxyBatchEditorItemFromForm();
        ProxyBatchEditorItems.Add(item);
        NormalizeSiteGroupConsistency(item.SiteGroupName);
        RebuildProxyBatchTargetsTextFromEditorItems(persistState: false);
        PrepareProxyBatchEditorFormForNextAdd(item, mode);
        /*
        StatusMessage = mode == ProxyBatchEditorMode.SharedKeyGroup
            ? $"已加入 {item.DisplayTitle}，共用 Key 信息已保留，可继续补下一个入口。"
            : $"已加入 {item.DisplayTitle}，可以继续填写下一条。";
        StatusMessage = $"已新增条目：{item.DisplayTitle}";
        */
        StatusMessage = mode == ProxyBatchEditorMode.SharedKeyGroup
            ? $"已加入 {item.DisplayTitle}，共用 Key 信息已保留，可继续补下一个入口。"
            : $"已加入 {item.DisplayTitle}，可以继续填写下一条。";
        SaveState();
        return Task.CompletedTask;
    }

    private Task UpdateProxyBatchEditorItemAsync()
    {
        if (SelectedProxyBatchEditorItem is null)
        {
            StatusMessage = "请先从左侧列表选中一条记录，再更新。";
            return Task.CompletedTask;
        }

        SetProxyBatchEditorMode(ResolveEffectiveProxyBatchEditorMode());
        var previousGroupName = SelectedProxyBatchEditorItem.SiteGroupName;
        var updated = BuildProxyBatchEditorItemFromForm();
        SelectedProxyBatchEditorItem.ApplyFrom(updated);
        NormalizeSiteGroupConsistency(previousGroupName);
        NormalizeSiteGroupConsistency(SelectedProxyBatchEditorItem.SiteGroupName);
        RebuildProxyBatchTargetsTextFromEditorItems(persistState: false);
        StatusMessage = $"已更新条目：{SelectedProxyBatchEditorItem.DisplayTitle}";
        SaveState();
        return Task.CompletedTask;
    }

    private Task DeleteProxyBatchEditorItemAsync()
    {
        if (SelectedProxyBatchEditorItem is null)
        {
            StatusMessage = "请先从左侧列表选中一条记录，再删除。";
            return Task.CompletedTask;
        }

        var displayTitle = SelectedProxyBatchEditorItem.DisplayTitle;
        var groupName = SelectedProxyBatchEditorItem.SiteGroupName;
        ProxyBatchEditorItems.Remove(SelectedProxyBatchEditorItem);
        NormalizeSiteGroupConsistency(groupName);
        RebuildProxyBatchTargetsTextFromEditorItems(persistState: false);
        SelectedProxyBatchEditorItem = null;
        StatusMessage = $"已删除条目：{displayTitle}";
        SaveState();
        return Task.CompletedTask;
    }

    private Task ResetProxyBatchEditorFormAsync()
    {
        SelectedProxyBatchEditorItem = null;
        LoadCurrentProxyConfigIntoBatchForm();
        StatusMessage = "填写面板已清空，并带入主页默认项。";
        SaveState();
        return Task.CompletedTask;
    }
}
