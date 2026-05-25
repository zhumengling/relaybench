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
    [RelayCommand]
    private void AddSite()
    {
        var entry = new BatchSiteEntry();
        SubscribeToChanges(entry);
        Sites.Add(entry);
        SelectedSite = entry;
    }

    /// <summary>
    /// Removes the selected site entry.
    /// </summary>
    [RelayCommand]
    private void RemoveSite()
    {
        if (SelectedSite is null) return;
        var idx = Sites.IndexOf(SelectedSite);
        Sites.Remove(SelectedSite);
        if (Sites.Count > 0)
            SelectedSite = Sites[Math.Min(idx, Sites.Count - 1)];
        else
            SelectedSite = null;
    }

    /// <summary>
    /// Duplicates the selected site entry (template row functionality).
    /// </summary>
    [RelayCommand]
    private void DuplicateSite()
    {
        if (SelectedSite is null) return;
        var copy = SelectedSite.Duplicate();
        SubscribeToChanges(copy);
        var idx = Sites.IndexOf(SelectedSite);
        Sites.Insert(idx + 1, copy);
        SelectedSite = copy;
    }

    /// <summary>
    /// Removes rows that have no user-entered site content while keeping one blank row available.
    /// </summary>
    [RelayCommand]
    private void ClearEmptyRows()
    {
        var emptyRows = Sites
            .Where(IsEmptySiteRow)
            .ToList();
        if (emptyRows.Count == 0)
        {
            ImportStatusText = "没有需要清理的空白行。";
            return;
        }

        var selectedWasRemoved = SelectedSite is not null && emptyRows.Contains(SelectedSite);
        var firstRemovedIndex = emptyRows
            .Select(row => Sites.IndexOf(row))
            .Where(static index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();

        foreach (var row in emptyRows)
        {
            Sites.Remove(row);
        }

        if (Sites.Count == 0)
        {
            var entry = new BatchSiteEntry();
            SubscribeToChanges(entry);
            Sites.Add(entry);
            SelectedSite = entry;
            ImportStatusText = $"已清理 {emptyRows.Count} 行空白入口，并保留 1 行空白入口。";
        }
        else if (selectedWasRemoved)
        {
            SelectedSite = Sites[Math.Min(firstRemovedIndex, Sites.Count - 1)];
            ImportStatusText = $"已清理 {emptyRows.Count} 行空白入口。";
        }
        else
        {
            ImportStatusText = $"已清理 {emptyRows.Count} 行空白入口。";
        }

        SaveToDisk();
    }

    [RelayCommand]
    private void ClearSelectedSite()
    {
        if (SelectedSite is null)
        {
            ImportStatusText = "请先选择一个入口。";
            return;
        }

        SelectedSite.Name = string.Empty;
        SelectedSite.BaseUrl = string.Empty;
        SelectedSite.ApiKey = string.Empty;
        SelectedSite.Model = string.Empty;
        SelectedSite.GroupName = string.Empty;
        SelectedSite.Timeout = 30;
        SelectedSite.TlsIgnore = false;
        SelectedSite.IsIncluded = true;
        ImportStatusText = "已清空当前入口。";
        MarkDirty();
        SaveToDisk();
    }

    [RelayCommand]
    private void AddDraftRow()
    {
        if (DraftRows.Count >= MaxDraftRows)
        {
            DraftStatusText = $"当前Site最多先录入 {MaxDraftRows} 行，请先删除不需要的行。";
            return;
        }

        var row = new BatchSiteDraftRow();
        SubscribeToDraftChanges(row);
        DraftRows.Add(row);
        SelectedDraftRow = row;
        DraftStatusText = "已新增一行空白入口。";
        RefreshDraftState();
    }

    [RelayCommand]
    private void DeleteDraftRow(BatchSiteDraftRow? row)
    {
        row ??= SelectedDraftRow;
        if (row is null || !DraftRows.Contains(row))
        {
            DraftStatusText = "请先选择要删除的草稿行。";
            return;
        }

        var index = DraftRows.IndexOf(row);
        DraftRows.Remove(row);
        if (DraftRows.Count == 0)
        {
            EnsureDraftRow();
        }

        SelectedDraftRow = DraftRows[Math.Min(Math.Max(index, 0), DraftRows.Count - 1)];
        DraftStatusText = "已删除 1 行草稿。";
        RefreshDraftState();
    }

    [RelayCommand]
    private async Task PasteDraftRowsAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                DraftStatusText = "剪贴板里没有可识别的文本。";
                return;
            }

            var text = await content.GetTextAsync();
            PasteTextIntoDraft(text);
        }
        catch (Exception ex)
        {
            DraftStatusText = $"读取剪贴板失败：{ex.Message}";
        }
    }

    public void PasteTextIntoDraft(string? clipboardText)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            DraftStatusText = "剪贴板里没有可识别的文本。";
            return;
        }

        if (TryApplySingleDraftClipboardValue(clipboardText))
        {
            RefreshDraftState();
            return;
        }

        var pastedRows = ParseDraftRows(clipboardText).ToList();
        if (pastedRows.Count == 0)
        {
            DraftStatusText = "剪贴板内容里没有识别到 URL、Key 或模型。";
            return;
        }

        var mergedRows = MergeDraftRows(DraftRows.Select(static row => row.Duplicate()), pastedRows);
        if (mergedRows.Count > MaxDraftRows)
        {
            DraftStatusText = $"粘贴后会超过 {MaxDraftRows} 行，请先删除不需要的行。";
            return;
        }

        ReplaceDraftRows(mergedRows);
        DraftStatusText = $"已从剪贴板识别 {pastedRows.Count} 行，空白字段会按上一行自动沿用。";
    }

    [RelayCommand]
    private void ClearDraftEmptyRows()
    {
        var keptRows = DraftRows
            .Where(static row => row.HasContent)
            .Select(static row => row.Duplicate())
            .ToList();
        ReplaceDraftRows(keptRows);
        DraftStatusText = keptRows.Count == 0
            ? "已清空当前草稿，并保留 1 行空白入口。"
            : $"已清理空白行，当前保留 {keptRows.Count} 行草稿。";
    }

    [RelayCommand]
    private void ToggleDraftRowsInclusion()
    {
        var rows = DraftRows.Where(static row => row.HasContent).DefaultIfEmpty().Where(row => row is not null).Cast<BatchSiteDraftRow>().ToArray();
        if (rows.Length == 0)
        {
            rows = DraftRows.ToArray();
        }

        var includeAll = rows.Any(static row => !row.IsIncluded);
        foreach (var row in rows)
        {
            row.IsIncluded = includeAll;
        }

        DraftStatusText = includeAll
            ? $"已将当前Site {rows.Length} 行入口全部设为加入测试。"
            : $"已将当前Site {rows.Length} 行入口全部设为跳过测试。";
        RefreshDraftState();
    }

    [RelayCommand]
    private void ResetDraft()
    {
        EditingGroupName = string.Empty;
        ReplaceDraftRows([]);
        DraftStatusText = "已清空当前Site草稿。";
    }

    [RelayCommand]
    private void CommitDraftRows()
    {
        try
        {
            var committed = BuildCommittedSitesFromDraft();
            var addedCount = 0;
            var replacedCount = 0;

            var editingGroupName = NormalizeNullable(EditingGroupName);
            if (!string.IsNullOrWhiteSpace(editingGroupName))
            {
                for (var index = Sites.Count - 1; index >= 0; index--)
                {
                    if (!string.Equals(ResolveSiteGroupName(Sites[index]), editingGroupName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Sites.RemoveAt(index);
                    replacedCount++;
                }
            }

            foreach (var entry in committed)
            {
                var existingIndex = FindSiteIndex(entry.BaseUrl);
                SubscribeToChanges(entry);
                if (existingIndex >= 0)
                {
                    Sites[existingIndex] = entry;
                    replacedCount++;
                }
                else
                {
                    Sites.Add(entry);
                    addedCount++;
                }
            }

            SelectedSite = committed.LastOrDefault();
            var savedGroupName = committed.FirstOrDefault()?.GroupName ?? editingGroupName;
            EditingGroupName = string.Empty;
            ReplaceDraftRows([]);
            HasImportError = false;
            ImportStatusText = !string.IsNullOrWhiteSpace(editingGroupName)
                ? $"Site组“{savedGroupName}”已保存：新增 {addedCount} 条，替换 {replacedCount} 条。"
                : $"入口组已更新：新增 {addedCount} 条，替换 {replacedCount} 条。";
            DraftStatusText = !string.IsNullOrWhiteSpace(editingGroupName)
                ? $"已保存Site组“{savedGroupName}”，右侧已清空，可继续录入下一Site。"
                : $"已加入入口组：新增 {addedCount} 条，替换 {replacedCount} 条。";
            SaveToDisk();
        }
        catch (Exception ex)
        {
            DraftStatusText = $"加入入口组失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void AddProxyBatchEditorItem()
        => CommitDraftRows();

    [RelayCommand]
    private void UpdateProxyBatchEditorItem()
        => CommitDraftRows();

    [RelayCommand]
    private void CommitProxyBatchEditorItem()
        => CommitDraftRows();

    [RelayCommand]
    private void DeleteProxyBatchEditorItem()
        => RemoveSelectedGroup();

    [RelayCommand]
    private void ResetProxyBatchEditorForm()
        => ResetDraft();

    [RelayCommand]
    private void AddProxyBatchTemplateRow()
        => AddDraftRow();

    [RelayCommand]
    private void DeleteProxyBatchTemplateRow(BatchSiteDraftRow? row)
        => DeleteDraftRow(row ?? SelectedDraftRow);

    [RelayCommand]
    private async Task PasteProxyBatchTemplateRowsAsync()
        => await PasteDraftRowsAsync();

    [RelayCommand]
    private void ApplyProxyBatchTemplateDefaults()
    {
        DraftStatusText = "当前录入会按上一行自动沿用 Key 和模型，不再注入默认占位配置。";
        ImportStatusText = DraftStatusText;
    }

    [RelayCommand]
    private void ClearProxyBatchTemplateEmptyRows()
        => ClearDraftEmptyRows();

    [RelayCommand]
    private void ToggleProxyBatchTemplateRowsTestInclusion()
        => ToggleDraftRowsInclusion();

    [RelayCommand]
    private void PreviewProxyBatchIndependentImport()
    {
        ImportMode = IndependentImportMode;
        PreviewBulkImport();
    }

    [RelayCommand]
    private void PreviewProxyBatchSharedImport()
    {
        ImportMode = SharedImportMode;
        PreviewBulkImport();
    }

    [RelayCommand]
    private void ImportProxyBatchIndependentEntries()
    {
        ImportMode = IndependentImportMode;
        BulkImport();
    }

    [RelayCommand]
    private void ImportProxyBatchSharedEntries()
    {
        ImportMode = SharedImportMode;
        BulkImport();
    }

}
