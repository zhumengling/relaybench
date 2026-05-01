using System.Windows;
using RelayBench.App.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task AddProxyBatchTemplateRowAsync()
    {
        if (ProxyBatchTemplateDraftItems.Count >= MaxProxyBatchSourceEntries)
        {
            throw new InvalidOperationException($"\u5f53\u524d\u7ad9\u70b9\u6700\u591a\u5148\u5f55\u5165 {MaxProxyBatchSourceEntries} \u884c\uff0c\u8bf7\u5148\u5220\u9664\u4e0d\u7528\u7684\u884c\u3002");
        }

        var item = CreateEmptyProxyBatchTemplateDraftItem();
        AttachProxyBatchTemplateDraftItem(item);
        ProxyBatchTemplateDraftItems.Add(item);
        RefreshProxyBatchTemplateDraftState();
        StatusMessage = "\u5df2\u65b0\u589e\u4e00\u884c\u7a7a\u767d\u5165\u53e3\u3002";
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
            StatusMessage = "\u526a\u8d34\u677f\u91cc\u6ca1\u6709\u53ef\u8bc6\u522b\u7684\u6587\u672c\u3002";
            return Task.CompletedTask;
        }

        if (TryApplySingleClipboardValueToProxyBatchTemplate(clipboardText))
        {
            return Task.CompletedTask;
        }

        var pastedRows = ProxyBatchTemplateClipboardParser.ParseDraftRows(clipboardText);
        if (pastedRows.Count == 0)
        {
            StatusMessage = "\u526a\u8d34\u677f\u5185\u5bb9\u91cc\u6ca1\u6709\u8bc6\u522b\u5230\u53ef\u5bfc\u5165\u7684\u884c\u3002";
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
            throw new InvalidOperationException($"\u7c98\u8d34\u540e\u4f1a\u8d85\u8fc7 {MaxProxyBatchSourceEntries} \u884c\uff0c\u8bf7\u5148\u5220\u9664\u4e0d\u7528\u7684\u884c\u518d\u7ee7\u7eed\u3002");
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

        StatusMessage = $"\u5df2\u4ece\u526a\u8d34\u677f\u5e26\u5165 {pastedRows.Count} \u884c\uff0c\u7a7a\u767d\u9879\u5df2\u6309\u4e0a\u4e00\u884c\u81ea\u52a8\u8865\u9f50\u3002";
        return Task.CompletedTask;
    }

    private bool TryApplySingleClipboardValueToProxyBatchTemplate(string clipboardText)
    {
        var singleValue = NormalizeSingleProxyBatchTemplateClipboardValue(clipboardText);
        if (singleValue is null)
        {
            return false;
        }

        var isUrl = IsValidHttpUrl(singleValue);
        var fieldDisplayName = isUrl ? "URL" : "key";
        var draftRows = ProxyBatchTemplateDraftItems
            .Select(CloneProxyBatchTemplateDraftItem)
            .ToList();

        if (draftRows.Count == 0)
        {
            draftRows.Add(CreateEmptyProxyBatchTemplateDraftItem());
        }

        var missingFieldRowIndex = FindFirstProxyBatchTemplateDraftRowIndex(
            draftRows,
            item => !IsEmptyProxyBatchTemplateDraftRow(item) &&
                    string.IsNullOrWhiteSpace(isUrl ? item.BaseUrl : item.EntryApiKey));

        if (missingFieldRowIndex >= 0)
        {
            draftRows[missingFieldRowIndex] = ApplySingleClipboardValueToProxyBatchTemplateDraftRow(
                draftRows[missingFieldRowIndex],
                singleValue,
                isUrl);
            ReplaceProxyBatchTemplateDraftItems(draftRows);
            StatusMessage = $"\u5df2\u8bc6\u522b\u4e3a\u5355\u4e2a {fieldDisplayName}\uff0c\u5df2\u8865\u5230\u7b2c {missingFieldRowIndex + 1} \u884c\u3002";
            return true;
        }

        var emptyRowIndex = FindFirstProxyBatchTemplateDraftRowIndex(draftRows, IsEmptyProxyBatchTemplateDraftRow);
        var previousRow = FindPreviousProxyBatchTemplateMeaningfulRow(
            draftRows,
            emptyRowIndex >= 0 ? emptyRowIndex - 1 : draftRows.Count - 1);

        if (emptyRowIndex >= 0)
        {
            draftRows[emptyRowIndex] = BuildNextProxyBatchTemplateDraftRow(
                previousRow,
                draftRows[emptyRowIndex],
                singleValue,
                isUrl);
            ReplaceProxyBatchTemplateDraftItems(draftRows);
            StatusMessage = previousRow is null
                ? $"\u5df2\u8bc6\u522b\u4e3a\u5355\u4e2a {fieldDisplayName}\uff0c\u5df2\u5199\u5165\u7b2c {emptyRowIndex + 1} \u884c\u3002"
                : $"\u5df2\u8bc6\u522b\u4e3a\u5355\u4e2a {fieldDisplayName}\uff0c\u7b2c {emptyRowIndex + 1} \u884c\u5df2\u6cbf\u7528\u4e0a\u4e00\u884c\u5176\u4ed6\u5185\u5bb9\u5e76\u5199\u5165\u65b0\u503c\u3002";
            return true;
        }

        if (draftRows.Count >= MaxProxyBatchSourceEntries)
        {
            throw new InvalidOperationException($"\u7c98\u8d34\u540e\u4f1a\u8d85\u8fc7 {MaxProxyBatchSourceEntries} \u884c\uff0c\u8bf7\u5148\u5220\u9664\u4e0d\u7528\u7684\u884c\u518d\u7ee7\u7eed\u3002");
        }

        draftRows.Add(BuildNextProxyBatchTemplateDraftRow(previousRow, null, singleValue, isUrl));
        ReplaceProxyBatchTemplateDraftItems(draftRows);
        StatusMessage = previousRow is null
            ? $"\u5df2\u8bc6\u522b\u4e3a\u5355\u4e2a {fieldDisplayName}\uff0c\u5df2\u65b0\u589e\u7b2c {draftRows.Count} \u884c\u3002"
            : $"\u5df2\u8bc6\u522b\u4e3a\u5355\u4e2a {fieldDisplayName}\uff0c\u5df2\u65b0\u589e\u7b2c {draftRows.Count} \u884c\uff0c\u5e76\u6cbf\u7528\u4e0a\u4e00\u884c\u5176\u4ed6\u5185\u5bb9\u3002";
        return true;
    }

    private Task ApplyProxyBatchTemplateDefaultsAsync()
    {
        StatusMessage = "\u73b0\u5728\u4e0d\u518d\u4f7f\u7528\u9ed8\u8ba4 Key / \u9ed8\u8ba4\u5206\u7ec4\uff1b\u82e5\u53ea\u7c98\u8d34\u4e86 URL \u6216 Key\uff0c\u7cfb\u7edf\u4f1a\u6309\u4e0a\u4e00\u884c\u81ea\u52a8\u8865\u9f50\u3002";
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
            ? "\u5df2\u6e05\u7a7a\u5f53\u524d\u7ad9\u70b9\u5185\u5bb9\uff0c\u5e76\u4fdd\u7559\u7b2c\u4e00\u884c\u7a7a\u767d\u7b49\u5f85\u7ee7\u7eed\u8f93\u5165\u3002"
            : $"\u5df2\u6e05\u7406\u7a7a\u767d\u884c\uff0c\u5f53\u524d\u4fdd\u7559 {keptRows.Length} \u884c\u5185\u5bb9\u3002";
        return Task.CompletedTask;
    }

    private Task ToggleProxyBatchTemplateRowsTestInclusionAsync()
    {
        var meaningfulRows = ProxyBatchTemplateDraftItems
            .Where(item => !IsEmptyProxyBatchTemplateDraftRow(item))
            .ToArray();
        var rows = meaningfulRows.Length > 0
            ? meaningfulRows
            : ProxyBatchTemplateDraftItems.ToArray();
        var includeAll = rows.Any(item => !item.IncludeInBatchTest);

        ExecuteWithoutProxyBatchTemplateDraftSync(() =>
        {
            foreach (var row in rows)
            {
                row.IncludeInBatchTest = includeAll;
            }
        });

        RefreshProxyBatchTemplateDraftState();
        StatusMessage = includeAll
            ? $"已将当前站点 {rows.Length} 个入口全部设为加入测试。"
            : $"已将当前站点 {rows.Length} 个入口全部设为跳过测试。";
        return Task.CompletedTask;
    }

    private Task FetchProxyBatchTemplateRowModelsAsync(ProxyBatchEditorItemViewModel? row)
    {
        if (row is null)
        {
            StatusMessage = "\u8bf7\u5148\u70b9\u51fb\u8981\u62c9\u6a21\u578b\u7684\u90a3\u4e00\u884c\u3002";
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

        return $"\u5f53\u524d\u7ad9\u70b9\uff1a{siteNamePreview}\u3002\u8868\u683c\u5171 {ProxyBatchTemplateDraftItems.Count} \u884c\uff0c\u5df2\u586b\u5199 {meaningfulRows.Length} \u884c\uff0c\u7a7a\u767d {blankRows} \u884c\uff1b\u52a0\u5165\u6d4b\u8bd5 {enabledRows} \u884c\uff0c\u8df3\u8fc7\u6d4b\u8bd5 {disabledRows} \u884c\uff1b\u7f3a URL {missingUrlRows} \u884c\uff0cURL \u65e0\u6548 {invalidUrlRows} \u884c\uff0c\u91cd\u590d URL {duplicateUrlCount} \u884c\u3002\u70b9\u51fb\u201c\u52a0\u5165\u5165\u53e3\u7ec4\u201d\u540e\uff0c\u4f1a\u628a\u5f53\u524d\u6240\u6709\u884c\u4f5c\u4e3a\u540c\u4e00\u4e2a\u7ad9\u70b9\u4fdd\u5b58\uff0c\u7136\u540e\u81ea\u52a8\u6e05\u7a7a\u4e3a\u4e0b\u4e00\u4e2a\u7ad9\u70b9\u4fdd\u7559\u7b2c\u4e00\u884c\u7a7a\u767d\u3002";
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
            throw new InvalidOperationException("\u8bf7\u5148\u5728\u53f3\u4fa7\u8868\u683c\u91cc\u81f3\u5c11\u586b\u5199\u4e00\u884c\u7f51\u5740\u3002");
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
                throw new InvalidOperationException($"\u7b2c {index + 1} \u884c\u7f3a\u5c11\u7f51\u5740\u3002");
            }

            if (!IsValidHttpUrl(baseUrl))
            {
                throw new InvalidOperationException($"\u7b2c {index + 1} \u884c\u7684\u7f51\u5740\u683c\u5f0f\u4e0d\u6b63\u786e\uff1a{baseUrl}");
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
               ?? $"\u7ad9\u70b9 {ProxyBatchSiteGroups.Count + 1}";
    }

    private string BuildProxyBatchTemplateSiteNamePreview(IReadOnlyList<ProxyBatchEditorItemViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return "\u672a\u547d\u540d\u7ad9\u70b9";
        }

        return NormalizeNullable(rows[0].EntryName)
               ?? TryGetHost(rows[0].BaseUrl)
               ?? "\u672a\u547d\u540d\u7ad9\u70b9";
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

    private static ProxyBatchEditorItemViewModel CreateEmptyProxyBatchTemplateDraftItem()
        => new(
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            null,
            null,
            true);

    private static string? NormalizeSingleProxyBatchTemplateClipboardValue(string? clipboardText)
    {
        var normalized = NormalizeNullable(
            (clipboardText ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n'));

        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('\n') ||
            normalized.Contains('\t') ||
            normalized.Contains('|'))
        {
            return null;
        }

        return normalized;
    }

    private static int FindFirstProxyBatchTemplateDraftRowIndex(
        IReadOnlyList<ProxyBatchEditorItemViewModel> rows,
        Func<ProxyBatchEditorItemViewModel, bool> predicate)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (predicate(rows[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static ProxyBatchEditorItemViewModel? FindPreviousProxyBatchTemplateMeaningfulRow(
        IReadOnlyList<ProxyBatchEditorItemViewModel> rows,
        int startIndex)
    {
        for (var index = Math.Min(startIndex, rows.Count - 1); index >= 0; index--)
        {
            if (!IsEmptyProxyBatchTemplateDraftRow(rows[index]))
            {
                return rows[index];
            }
        }

        return null;
    }

    private static ProxyBatchEditorItemViewModel ApplySingleClipboardValueToProxyBatchTemplateDraftRow(
        ProxyBatchEditorItemViewModel row,
        string clipboardValue,
        bool isUrl)
        => new(
            NormalizeNullable(row.EntryName) ?? string.Empty,
            isUrl ? clipboardValue : NormalizeNullable(row.BaseUrl) ?? string.Empty,
            isUrl ? NormalizeNullable(row.EntryApiKey) : clipboardValue,
            NormalizeNullable(row.EntryModel),
            null,
            null,
            null,
            row.IncludeInBatchTest);

    private static ProxyBatchEditorItemViewModel BuildNextProxyBatchTemplateDraftRow(
        ProxyBatchEditorItemViewModel? previousRow,
        ProxyBatchEditorItemViewModel? targetRow,
        string clipboardValue,
        bool isUrl)
    {
        var entryName = FirstNonEmpty(
                            NormalizeNullable(targetRow?.EntryName),
                            NormalizeNullable(previousRow?.EntryName))
                        ?? string.Empty;
        var baseUrl = FirstNonEmpty(
                          NormalizeNullable(targetRow?.BaseUrl),
                          NormalizeNullable(previousRow?.BaseUrl))
                      ?? string.Empty;
        var entryApiKey = FirstNonEmpty(
            NormalizeNullable(targetRow?.EntryApiKey),
            NormalizeNullable(previousRow?.EntryApiKey));
        var entryModel = FirstNonEmpty(
            NormalizeNullable(targetRow?.EntryModel),
            NormalizeNullable(previousRow?.EntryModel));
        var includeInBatchTest = targetRow?.IncludeInBatchTest ?? previousRow?.IncludeInBatchTest ?? true;

        if (isUrl)
        {
            baseUrl = clipboardValue;
        }
        else
        {
            entryApiKey = clipboardValue;
        }

        return new ProxyBatchEditorItemViewModel(
            entryName,
            baseUrl,
            entryApiKey,
            entryModel,
            null,
            null,
            null,
            includeInBatchTest);
    }

    private static bool IsEmptyProxyBatchTemplateDraftRow(ProxyBatchEditorItemViewModel item)
        => string.IsNullOrWhiteSpace(item.EntryName) &&
           string.IsNullOrWhiteSpace(item.BaseUrl) &&
           string.IsNullOrWhiteSpace(item.EntryApiKey) &&
           string.IsNullOrWhiteSpace(item.EntryModel);

    private static bool IsValidHttpUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
