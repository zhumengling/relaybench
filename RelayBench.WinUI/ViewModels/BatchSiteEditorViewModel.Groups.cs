using System.Collections.ObjectModel;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class BatchSiteEditorViewModel : ObservableObject
{
    public void BringCurrentEndpointToDraft(string? baseUrl, string? apiKey, string? model)
    {
        EditingGroupName = string.Empty;
        var row = DraftRows.FirstOrDefault(static row => !row.HasContent);
        if (row is null)
        {
            if (DraftRows.Count >= MaxDraftRows)
            {
                DraftStatusText = $"当前Site最多先录入 {MaxDraftRows} 行，请先删除不需要的行。";
                return;
            }

            row = new BatchSiteDraftRow();
            SubscribeToDraftChanges(row);
            DraftRows.Add(row);
        }

        row.BaseUrl = NormalizeNullable(baseUrl) ?? row.BaseUrl;
        row.ApiKey = NormalizeNullable(apiKey) ?? row.ApiKey;
        row.Model = NormalizeNullable(model) ?? row.Model;
        if (string.IsNullOrWhiteSpace(row.Name) && !string.IsNullOrWhiteSpace(row.BaseUrl))
        {
            row.Name = TryGetHost(row.BaseUrl) ?? "当前入口";
        }

        SelectedDraftRow = row;
        DraftStatusText = "已把主页当前入口带入草稿。";
        RefreshDraftState();
    }

    public void LoadDraftFromGroup(BatchSiteGroupSummary? group)
    {
        var groupName = NormalizeNullable(group?.Name);
        if (string.IsNullOrWhiteSpace(groupName) || groupName == "暂无入口组")
        {
            DraftStatusText = "请先选择一个已有Site组。";
            return;
        }

        var sites = Sites
            .Where(site => string.Equals(ResolveSiteGroupName(site), groupName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sites.Length == 0)
        {
            DraftStatusText = $"没有找到Site组“{groupName}”。";
            return;
        }

        var rows = sites.Select(static site =>
        {
            var row = new BatchSiteDraftRow
            {
                Name = site.Name,
                BaseUrl = site.BaseUrl,
                ApiKey = site.ApiKey,
                Model = site.Model,
                IsIncluded = site.IsIncluded,
                ModelCatalogSummary = site.ModelCatalogSummary,
                ProtocolSummary = site.ProtocolSummary,
            };

            foreach (var model in site.AvailableModels)
            {
                row.AvailableModels.Add(model);
            }

            return row;
        });

        EditingGroupName = groupName;
        ReplaceDraftRows(rows);
        SelectedSite = sites.LastOrDefault();
        HasImportError = false;
        ImportStatusText = $"已载入Site组“{groupName}”，保存会覆盖这个Site组。";
        DraftStatusText = $"已载入Site组“{groupName}”：{sites.Length} 行，可继续编辑 URL、Key、模型和是否加入测试。";
    }

    [RelayCommand]
    private void RemoveSelectedGroup()
    {
        var editingGroupName = NormalizeNullable(EditingGroupName);
        var selectedSite = SelectedSite;
        if (string.IsNullOrWhiteSpace(editingGroupName) && selectedSite is null)
        {
            ImportStatusText = "请先从左侧选择要删除的Site组。";
            DraftStatusText = ImportStatusText;
            return;
        }

        var removed = 0;
        if (string.IsNullOrWhiteSpace(editingGroupName) &&
            selectedSite is not null &&
            string.IsNullOrWhiteSpace(selectedSite.GroupName))
        {
            removed = Sites.Remove(selectedSite) ? 1 : 0;
        }
        else
        {
            var targetGroupName = editingGroupName ?? ResolveSiteGroupName(selectedSite!);
            for (var index = Sites.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(ResolveSiteGroupName(Sites[index]), targetGroupName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Sites.RemoveAt(index);
                removed++;
            }
        }

        if (removed == 0)
        {
            ImportStatusText = "没有找到要删除的Site组。";
            DraftStatusText = ImportStatusText;
            return;
        }

        EditingGroupName = string.Empty;
        SelectedSite = Sites.FirstOrDefault();
        ReplaceDraftRows([]);
        ImportStatusText = $"已删除 {removed} 行入口。";
        DraftStatusText = "已删除选中的Site组，右侧已重置为空白草稿。";
        SaveToDisk();
    }

    /// <summary>
    /// Groups currently ungrouped filled rows as one site and materializes inherited Key/Model values.
    /// </summary>
    [RelayCommand]
    private void GroupUngroupedRows()
    {
        var targetRows = Sites
            .Where(site => !IsEmptySiteRow(site) && string.IsNullOrWhiteSpace(site.GroupName))
            .ToList();
        if (targetRows.Count == 0)
        {
            ImportStatusText = "没有可归组的未分组入口。";
            return;
        }

        var groupName = BuildGroupNameForRows(targetRows);
        string? previousApiKey = null;
        string? previousModel = null;
        var filledFields = 0;

        foreach (var site in Sites.Where(site => !IsEmptySiteRow(site)))
        {
            var inheritedApiKey = FirstNonEmpty(site.ApiKey, previousApiKey);
            var inheritedModel = FirstNonEmpty(site.Model, previousModel);

            if (targetRows.Contains(site))
            {
                if (string.IsNullOrWhiteSpace(site.ApiKey) && !string.IsNullOrWhiteSpace(inheritedApiKey))
                {
                    site.ApiKey = inheritedApiKey;
                    filledFields++;
                }

                if (string.IsNullOrWhiteSpace(site.Model) && !string.IsNullOrWhiteSpace(inheritedModel))
                {
                    site.Model = inheritedModel;
                    filledFields++;
                }

                site.GroupName = groupName;
            }

            previousApiKey = FirstNonEmpty(site.ApiKey, previousApiKey);
            previousModel = FirstNonEmpty(site.Model, previousModel);
        }

        SelectedSite = targetRows[^1];
        ImportStatusText = filledFields > 0
            ? $"已将 {targetRows.Count} 行归为同站组“{groupName}”，并补齐 {filledFields} 个沿用字段。"
            : $"已将 {targetRows.Count} 行归为同站组“{groupName}”。";
        MarkDirty();
        SaveToDisk();
    }

    /// <summary>
    /// Parses the ImportText and adds entries. Supports pipe-delimited and tab-delimited formats.
    /// Independent mode supports URL, URL|Key, URL|Key|Model, Name|URL|Key|Model.
    /// Missing Key/Model columns inherit from the previous imported or existing row.
    /// Shared mode supports URL or Name|URL with the shared key/model fields.
    /// </summary>
}
