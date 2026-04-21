using System.Windows;
using NetTest.App.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task AddProxyBatchTemplateRowAsync()
    {
        if (ProxyBatchTemplateDraftItems.Count >= MaxProxyBatchSourceEntries)
        {
            throw new InvalidOperationException($"当前站点最多先录入 {MaxProxyBatchSourceEntries} 行，请先删除不用的行。");
        }

        var item = new ProxyBatchEditorItemViewModel(
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            null,
            null,
            true);
        AttachProxyBatchTemplateDraftItem(item);
        ProxyBatchTemplateDraftItems.Add(item);
        RefreshProxyBatchTemplateDraftState();
        StatusMessage = "已新增一行空白入口。";
        return Task.CompletedTask;
    }

    private Task DeleteProxyBatchTemplateRowAsync(ProxyBatchEditorItemViewModel? row)
    {
        if (row is null)
        {
            StatusMessage = "\u8bf7\u5148\u9009\u4e2d\u8981\u5220\u9664\u7684\u884c\u3002";
            return Task.CompletedTask;
        }

        var keptRows = ProxyBatchTemplateDraftItems
            .Where(item => !ReferenceEquals(item, row))
            .Select(CloneProxyBatchTemplateDraftItem)
            .ToArray();

        if (keptRows.Length == ProxyBatchTemplateDraftItems.Count)
        {
            StatusMessage = "\u6ca1\u6709\u627e\u5230\u8981\u5220\u9664\u7684\u884c\u3002";
            return Task.CompletedTask;
        }

        ReplaceProxyBatchTemplateDraftItems(keptRows);
        StatusMessage = keptRows.Length == 0
            ? "\u5df2\u5220\u9664\u6700\u540e\u4e00\u884c\uff0c\u4ecd\u4fdd\u7559 1 \u4e2a\u7a7a\u767d\u884c\u4f9b\u7ee7\u7eed\u8f93\u5165\u3002"
            : $"\u5df2\u5220\u9664 1 \u884c\uff0c\u5f53\u524d\u5269\u4f59 {keptRows.Length} \u884c\u3002";
        return Task.CompletedTask;
    }

    private Task PasteProxyBatchTemplateRowsAsync()
    {
        var clipboardText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            StatusMessage = "剪贴板里没有可识别的文本。";
            return Task.CompletedTask;
        }

        var pastedRows = ProxyBatchTemplateClipboardParser.ParseDraftRows(clipboardText);
        if (pastedRows.Count == 0)
        {
            StatusMessage = "剪贴板内容里没有识别到可导入的行。";
            return Task.CompletedTask;
        }

        var existingRows = ProxyBatchTemplateDraftItems
            .Select(item => new ProxyBatchTemplateDraftRowData(
                item.EntryName,
                item.BaseUrl,
                item.EntryApiKey,
                item.EntryModel,
                item.IncludeInBatchTest))
            .ToArray();
        var mergedRows = ProxyBatchTemplateClipboardParser.MergeDraftRows(existingRows, pastedRows);
        if (mergedRows.Count > MaxProxyBatchSourceEntries)
        {
            throw new InvalidOperationException($"粘贴后会超过 {MaxProxyBatchSourceEntries} 行，请先删除不用的行再继续。");
        }

        ReplaceProxyBatchTemplateDraftItems(mergedRows.Select(row =>
            new ProxyBatchEditorItemViewModel(
                row.EntryName ?? string.Empty,
                row.BaseUrl ?? string.Empty,
                row.EntryApiKey,
                row.EntryModel,
                null,
                null,
                null,
                row.IncludeInBatchTest)));

        StatusMessage = $"已从剪贴板带入 {pastedRows.Count} 行，空白项已按上一行自动补齐。";
        return Task.CompletedTask;
    }

    private Task ApplyProxyBatchTemplateDefaultsAsync()
    {
        StatusMessage = "现在不再使用默认 Key / 默认分组；若只粘贴了 URL 或 Key，系统会按上一行自动补齐。";
        return Task.CompletedTask;
    }

    private Task ClearProxyBatchTemplateEmptyRowsAsync()
    {
        var keptRows = ProxyBatchTemplateDraftItems
            .Where(item => !IsEmptyProxyBatchTemplateDraftRow(item))
            .Select(CloneProxyBatchTemplateDraftItem)
            .ToArray();

        ReplaceProxyBatchTemplateDraftItems(keptRows);
        StatusMessage = keptRows.Length == 0
            ? "已清空当前站点内容，并保留第一行空白等待继续输入。"
            : $"已清理空白行，当前保留 {keptRows.Length} 行内容。";
        return Task.CompletedTask;
    }

    private Task FetchProxyBatchTemplateRowModelsAsync(ProxyBatchEditorItemViewModel? row)
    {
        if (row is null)
        {
            StatusMessage = "请先点击要拉模型的那一行。";
            return Task.CompletedTask;
        }

        _proxyBatchTemplateModelTargetRow = row;
        return FetchProxyModelsForTargetAsync(ProxyModelPickerTarget.BatchTemplateRowModel);
    }

    private string BuildProxyBatchTemplateSummary()
    {
        var meaningfulRows = ProxyBatchTemplateDraftItems
            .Where(item => !IsEmptyProxyBatchTemplateDraftRow(item))
            .ToArray();
        var blankRows = ProxyBatchTemplateDraftItems.Count - meaningfulRows.Length;
        var missingUrlRows = meaningfulRows.Count(item => string.IsNullOrWhiteSpace(item.BaseUrl));
        var invalidUrlRows = meaningfulRows.Count(item => !string.IsNullOrWhiteSpace(item.BaseUrl) && !IsValidHttpUrl(item.BaseUrl));
        var enabledRows = meaningfulRows.Count(item => item.IncludeInBatchTest);
        var disabledRows = meaningfulRows.Length - enabledRows;
        var duplicateUrlCount = meaningfulRows
            .Where(item => IsValidHttpUrl(item.BaseUrl))
            .GroupBy(item => item.BaseUrl.Trim(), StringComparer.OrdinalIgnoreCase)
            .Sum(group => Math.Max(0, group.Count() - 1));
        var siteNamePreview = BuildProxyBatchTemplateSiteNamePreview(meaningfulRows);

        return $"当前站点：{siteNamePreview}。表格共 {ProxyBatchTemplateDraftItems.Count} 行，已填写 {meaningfulRows.Length} 行，空白 {blankRows} 行；加入测试 {enabledRows} 行，跳过测试 {disabledRows} 行；缺 URL {missingUrlRows} 行，URL 无效 {invalidUrlRows} 行，重复 URL {duplicateUrlCount} 行。点击“加入入口组”后，会把当前所有行作为同一个站点保存，然后自动清空为下一个站点保留第一行空白。";
    }

    private string? ResolveProxyBatchTemplateRowApiKey(ProxyBatchEditorItemViewModel? row)
    {
        if (row is null)
        {
            return null;
        }

        var rowIndex = ProxyBatchTemplateDraftItems.IndexOf(row);
        for (var index = rowIndex; index >= 0; index--)
        {
            var value = NormalizeNullable(ProxyBatchTemplateDraftItems[index].EntryApiKey);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private List<ProxyBatchEditorItemViewModel> BuildCommittedProxyBatchSiteItemsFromDraft()
    {
        var sourceRows = ProxyBatchTemplateDraftItems
            .Where(item => !IsEmptyProxyBatchTemplateDraftRow(item))
            .ToArray();
        if (sourceRows.Length == 0)
        {
            throw new InvalidOperationException("请先在右侧表格里至少填写一行网址。");
        }

        List<ProxyBatchEditorItemViewModel> normalizedRows = [];
        string? previousEntryName = null;
        string? previousBaseUrl = null;
        string? previousApiKey = null;
        string? previousModel = null;

        for (var index = 0; index < sourceRows.Length; index++)
        {
            var row = sourceRows[index];
            var entryName = FirstNonEmpty(NormalizeNullable(row.EntryName), previousEntryName);
            var baseUrl = FirstNonEmpty(NormalizeNullable(row.BaseUrl), previousBaseUrl);
            var apiKey = FirstNonEmpty(NormalizeNullable(row.EntryApiKey), previousApiKey);
            var model = FirstNonEmpty(NormalizeNullable(row.EntryModel), previousModel);

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException($"第 {index + 1} 行缺少网址。");
            }

            if (!IsValidHttpUrl(baseUrl))
            {
                throw new InvalidOperationException($"第 {index + 1} 行的网址格式不正确：{baseUrl}");
            }

            normalizedRows.Add(new ProxyBatchEditorItemViewModel(
                entryName ?? string.Empty,
                baseUrl,
                apiKey,
                model,
                null,
                null,
                null,
                row.IncludeInBatchTest));

            previousEntryName = entryName;
            previousBaseUrl = baseUrl;
            previousApiKey = apiKey;
            previousModel = model;
        }

        var siteName = BuildProxyBatchTemplateSiteName(normalizedRows);
        for (var index = 0; index < normalizedRows.Count; index++)
        {
            var row = normalizedRows[index];
            var finalEntryName = NormalizeNullable(row.EntryName) ?? BuildBatchDefaultName(row.BaseUrl, index + 1);
            normalizedRows[index] = new ProxyBatchEditorItemViewModel(
                finalEntryName,
                row.BaseUrl,
                row.EntryApiKey,
                row.EntryModel,
                siteName,
                null,
                null,
                row.IncludeInBatchTest);
        }

        return normalizedRows;
    }

    private string BuildProxyBatchTemplateSiteName(IReadOnlyList<ProxyBatchEditorItemViewModel> rows)
    {
        var firstRow = rows[0];
        return NormalizeNullable(firstRow.EntryName)
               ?? TryGetHost(firstRow.BaseUrl)
               ?? $"站点 {ProxyBatchSiteGroups.Count + 1}";
    }

    private string BuildProxyBatchTemplateSiteNamePreview(IReadOnlyList<ProxyBatchEditorItemViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return "未命名站点";
        }

        return NormalizeNullable(rows[0].EntryName)
               ?? TryGetHost(rows[0].BaseUrl)
               ?? "未命名站点";
    }

    private static ProxyBatchEditorItemViewModel CloneProxyBatchTemplateDraftItem(ProxyBatchEditorItemViewModel item)
        => new(
            item.EntryName,
            item.BaseUrl,
            item.EntryApiKey,
            item.EntryModel,
            null,
            null,
            null,
            item.IncludeInBatchTest);

    private static bool IsEmptyProxyBatchTemplateDraftRow(ProxyBatchEditorItemViewModel item)
        => string.IsNullOrWhiteSpace(item.EntryName) &&
           string.IsNullOrWhiteSpace(item.BaseUrl) &&
           string.IsNullOrWhiteSpace(item.EntryApiKey) &&
           string.IsNullOrWhiteSpace(item.EntryModel);

    private static bool IsValidHttpUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
