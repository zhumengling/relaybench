using System.Text;
using NetTest.App.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task PreviewProxyBatchSharedImportAsync()
    {
        var entries = ProxyBatchBulkImportParser.ParseSharedEntries(
            ProxyBatchBulkImportSharedText,
            ProxyBatchFormSiteGroupName,
            ProxyBatchFormSiteGroupApiKey,
            ProxyBatchFormSiteGroupModel);

        ProxyBatchBulkImportPreview = BuildProxyBatchBulkImportPreview("同站共用 Key 导入预览", entries);
        StatusMessage = $"已预览 {entries.Count} 条同站入口，可继续点击“导入到入口组”。";
        return Task.CompletedTask;
    }

    private Task ImportProxyBatchSharedEntriesAsync()
    {
        var entries = ProxyBatchBulkImportParser.ParseSharedEntries(
            ProxyBatchBulkImportSharedText,
            ProxyBatchFormSiteGroupName,
            ProxyBatchFormSiteGroupApiKey,
            ProxyBatchFormSiteGroupModel);

        var outcome = ApplyProxyBatchBulkImportEntries(entries);
        ProxyBatchBulkImportSharedText = string.Empty;
        ProxyBatchBulkImportPreview = BuildProxyBatchBulkImportResultPreview("同站共用 Key 导入完成", entries, outcome);
        StatusMessage = $"已导入 {entries.Count} 条同站入口：新增 {outcome.AddedCount} 条，替换 {outcome.ReplacedCount} 条。";
        SaveState();
        return Task.CompletedTask;
    }

    private Task PreviewProxyBatchIndependentImportAsync()
    {
        var entries = ProxyBatchBulkImportParser.ParseIndependentEntries(
            ProxyBatchBulkImportIndependentText,
            ProxyBatchFormSiteGroupName);

        ProxyBatchBulkImportPreview = BuildProxyBatchBulkImportPreview("独立 Key / 模型导入预览", entries);
        StatusMessage = $"已预览 {entries.Count} 条独立入口，可继续点击“导入到入口组”。";
        return Task.CompletedTask;
    }

    private Task ImportProxyBatchIndependentEntriesAsync()
    {
        var entries = ProxyBatchBulkImportParser.ParseIndependentEntries(
            ProxyBatchBulkImportIndependentText,
            ProxyBatchFormSiteGroupName);

        var outcome = ApplyProxyBatchBulkImportEntries(entries);
        ProxyBatchBulkImportIndependentText = string.Empty;
        ProxyBatchBulkImportPreview = BuildProxyBatchBulkImportResultPreview("独立 Key / 模型导入完成", entries, outcome);
        StatusMessage = $"已导入 {entries.Count} 条独立入口：新增 {outcome.AddedCount} 条，替换 {outcome.ReplacedCount} 条。";
        SaveState();
        return Task.CompletedTask;
    }

    private BulkImportApplyOutcome ApplyProxyBatchBulkImportEntries(IReadOnlyList<ProxyBatchBulkImportEntry> entries)
    {
        List<ProxyBatchEditorItemViewModel> mergedItems = ProxyBatchEditorItems
            .Select(CloneProxyBatchEditorItem)
            .ToList();

        var addedCount = 0;
        var replacedCount = 0;
        foreach (var entry in entries)
        {
            var importedItem = CreateProxyBatchEditorItem(entry);
            var existingIndex = FindProxyBatchEditorItemIndex(mergedItems, importedItem.BaseUrl);
            if (existingIndex >= 0)
            {
                mergedItems[existingIndex] = importedItem;
                replacedCount++;
            }
            else
            {
                mergedItems.Add(importedItem);
                addedCount++;
            }
        }

        if (mergedItems.Count > MaxProxyBatchSourceEntries)
        {
            throw new InvalidOperationException($"导入后会有 {mergedItems.Count} 条入口，已超过上限 {MaxProxyBatchSourceEntries} 条，请先删减或分批导入。");
        }

        ApplyProxyBatchEditorItems(mergedItems
            .Select(CreateProxyBatchConfigItemSnapshot)
            .ToArray());

        var normalizedGroups = mergedItems
            .Select(item => NormalizeNullable(item.SiteGroupName))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var groupName in normalizedGroups)
        {
            NormalizeSiteGroupConsistency(groupName);
        }

        RebuildProxyBatchTargetsTextFromEditorItems(persistState: false);
        SelectedProxyBatchEditorItem = null;

        return new BulkImportApplyOutcome(addedCount, replacedCount, mergedItems.Count);
    }

    private string BuildProxyBatchBulkImportPreview(
        string title,
        IReadOnlyList<ProxyBatchBulkImportEntry> entries)
    {
        var existingUrls = ProxyBatchEditorItems
            .Select(item => item.BaseUrl)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replacedCount = entries.Count(entry => existingUrls.Contains(entry.BaseUrl));
        var addedCount = entries.Count - replacedCount;

        return BuildProxyBatchBulkImportText(title, entries, addedCount, replacedCount, null);
    }

    private string BuildProxyBatchBulkImportResultPreview(
        string title,
        IReadOnlyList<ProxyBatchBulkImportEntry> entries,
        BulkImportApplyOutcome outcome)
        => BuildProxyBatchBulkImportText(title, entries, outcome.AddedCount, outcome.ReplacedCount, outcome.FinalCount);

    private static string BuildProxyBatchBulkImportText(
        string title,
        IReadOnlyList<ProxyBatchBulkImportEntry> entries,
        int addedCount,
        int replacedCount,
        int? finalCount)
    {
        StringBuilder builder = new();
        builder.AppendLine(title);
        builder.AppendLine($"共识别 {entries.Count} 条；预计新增 {addedCount} 条，替换重复地址 {replacedCount} 条。");
        if (finalCount is not null)
        {
            builder.AppendLine($"导入后入口组总数：{finalCount} 条。");
        }

        builder.AppendLine();
        builder.AppendLine("预览：");
        foreach (var entry in entries.Take(6))
        {
            var siteGroup = string.IsNullOrWhiteSpace(entry.SiteGroupName)
                ? "独立入口"
                : $"同站组 {entry.SiteGroupName}";
            var keySummary = !string.IsNullOrWhiteSpace(entry.EntryApiKey)
                ? "本条目自带 key"
                : !string.IsNullOrWhiteSpace(entry.SiteGroupApiKey)
                    ? "继承同站 key"
                    : "未显式提供 key";
            var modelSummary = !string.IsNullOrWhiteSpace(entry.EntryModel)
                ? entry.EntryModel
                : !string.IsNullOrWhiteSpace(entry.SiteGroupModel)
                    ? entry.SiteGroupModel
                    : "未显式提供模型";
            builder.AppendLine($"- [{siteGroup}] {entry.EntryName} -> {entry.BaseUrl} | {keySummary} | 模型：{modelSummary}");
        }

        if (entries.Count > 6)
        {
            builder.AppendLine($"- ... 另有 {entries.Count - 6} 条未展开");
        }

        return builder.ToString().TrimEnd();
    }

    private static ProxyBatchEditorItemViewModel CloneProxyBatchEditorItem(ProxyBatchEditorItemViewModel item)
        => new(
            item.EntryName,
            item.BaseUrl,
            item.EntryApiKey,
            item.EntryModel,
            item.SiteGroupName,
            item.SiteGroupApiKey,
            item.SiteGroupModel,
            item.IncludeInBatchTest);

    private static ProxyBatchEditorItemViewModel CreateProxyBatchEditorItem(ProxyBatchBulkImportEntry entry)
        => new(
            entry.EntryName,
            entry.BaseUrl,
            NormalizeNullable(entry.EntryApiKey),
            NormalizeNullable(entry.EntryModel),
            NormalizeNullable(entry.SiteGroupName),
            NormalizeNullable(entry.SiteGroupApiKey),
            NormalizeNullable(entry.SiteGroupModel));

    private static int FindProxyBatchEditorItemIndex(
        IReadOnlyList<ProxyBatchEditorItemViewModel> items,
        string baseUrl)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed record BulkImportApplyOutcome(int AddedCount, int ReplacedCount, int FinalCount);
}
