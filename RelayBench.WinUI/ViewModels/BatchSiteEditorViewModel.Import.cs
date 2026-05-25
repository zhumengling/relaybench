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
    private void BulkImport()
    {
        try
        {
            var entries = ParseImportEntries().ToList();
            if (entries.Count == 0)
            {
                SetImportError("没有可导入的入口。");
                return;
            }

            var addedCount = 0;
            var replacedCount = 0;
            foreach (var entry in entries)
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

            SelectedSite = entries.LastOrDefault();
            ImportText = string.Empty;
            HasImportError = false;
            ImportStatusText = $"导入完成：新增 {addedCount} 条，替换 {replacedCount} 条。";
            ImportPreview = "导入完成，可继续粘贴下一批入口。";
            SaveToDisk();
        }
        catch (Exception ex)
        {
            SetImportError(ex.Message);
        }
    }

    [RelayCommand]
    private void ToggleBulkImport()
    {
        IsBulkImportOpen = !IsBulkImportOpen;
        RefreshImportPreview();
    }

    [RelayCommand]
    private void PreviewBulkImport()
    {
        RefreshImportPreview();
    }

    /// <summary>
    /// Toggles the editor panel expanded/collapsed state.
    /// </summary>
    [RelayCommand]
    private void ToggleEditor()
    {
        IsEditorExpanded = !IsEditorExpanded;
    }

    /// <summary>
    /// Selects or deselects all sites for the execution plan.
    /// </summary>
    [RelayCommand]
    private void SelectAll(string parameter)
    {
        var include = parameter == "true";
        foreach (var site in Sites)
            site.IsIncluded = include;
    }

    /// <summary>
    /// Parses a single import line. Detects delimiter (pipe or tab).
    /// </summary>
    public static BatchSiteEntry? ParseImportLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var columns = SplitColumns(line);
        if (columns.Length == 0)
        {
            return null;
        }

        return CreateIndependentEntry(columns, null, 1);
    }

    private IReadOnlyList<BatchSiteEntry> ParseImportEntries()
    {
        var lines = EnumerateContentLines(ImportText).ToList();
        if (lines.Count == 0)
        {
            return [];
        }

        if (!IsSharedImport)
        {
            return ParseIndependentImportEntries(lines);
        }

        List<BatchSiteEntry> entries = [];
        for (var index = 0; index < lines.Count; index++)
        {
            var columns = SplitColumns(lines[index]);
            var entry = CreateSharedEntry(columns, index + 1);

            entries.Add(entry);
        }

        return entries;
    }

    private IReadOnlyList<BatchSiteEntry> ParseIndependentImportEntries(IReadOnlyList<string> lines)
    {
        List<BatchSiteEntry> entries = [];
        var previous = FindPreviousIndependentImportContext();

        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var draft = ParseIndependentDraftRow(SplitColumns(lines[index]), lineNumber);
            var baseUrl = draft.HasBaseUrl ? NormalizeNullable(draft.BaseUrl) : previous.BaseUrl;
            var normalizedUrl = NormalizeUrl(baseUrl, lineNumber);
            var apiKey = draft.HasApiKey ? NormalizeNullable(draft.ApiKey) : previous.ApiKey;
            var model = draft.HasModel ? NormalizeNullable(draft.Model) : previous.Model;
            var name = draft.HasName ? NormalizeNullable(draft.Name) : null;
            var entryName = name
                ?? (!draft.HasBaseUrl ? previous.Name : null)
                ?? BuildDefaultName(normalizedUrl, lineNumber);

            var entry = new BatchSiteEntry(
                normalizedUrl,
                apiKey ?? string.Empty,
                model ?? string.Empty,
                name: entryName);
            entries.Add(entry);

            previous = new IndependentImportContext(entryName, normalizedUrl, apiKey, model);
        }

        return entries;
    }

    private IndependentImportContext FindPreviousIndependentImportContext()
    {
        for (var index = Sites.Count - 1; index >= 0; index--)
        {
            var site = Sites[index];
            if (!string.IsNullOrWhiteSpace(site.BaseUrl) ||
                !string.IsNullOrWhiteSpace(site.ApiKey) ||
                !string.IsNullOrWhiteSpace(site.Model))
            {
                return new IndependentImportContext(
                    NormalizeNullable(site.Name),
                    NormalizeNullable(site.BaseUrl),
                    NormalizeNullable(site.ApiKey),
                    NormalizeNullable(site.Model));
            }
        }

        return default;
    }

    private static BatchSiteEntry CreateIndependentEntry(string[] columns, string? defaultGroupName, int lineNumber)
    {
        if (columns.Length is < 1 or > 4)
        {
            throw new InvalidOperationException($"第 {lineNumber} 行格式不正确，请使用 URL、URL|Key|模型 或 名称|URL|Key|模型。");
        }

        string? name = null;
        string? baseUrl = null;
        string? apiKey = null;
        string? model = null;

        switch (columns.Length)
        {
            case 1:
                baseUrl = columns[0];
                break;
            case 2:
                if (LooksLikeUrl(columns[0]))
                {
                    baseUrl = columns[0];
                    apiKey = NormalizeNullable(columns[1]);
                }
                else
                {
                    name = NormalizeNullable(columns[0]);
                    baseUrl = columns[1];
                }
                break;
            case 3:
                if (LooksLikeUrl(columns[0]))
                {
                    baseUrl = columns[0];
                    apiKey = NormalizeNullable(columns[1]);
                    model = NormalizeNullable(columns[2]);
                }
                else
                {
                    name = NormalizeNullable(columns[0]);
                    baseUrl = columns[1];
                    apiKey = NormalizeNullable(columns[2]);
                }
                break;
            case 4:
                name = NormalizeNullable(columns[0]);
                baseUrl = columns[1];
                apiKey = NormalizeNullable(columns[2]);
                model = NormalizeNullable(columns[3]);
                break;
        }

        var normalizedUrl = NormalizeUrl(baseUrl, lineNumber);
        return new BatchSiteEntry(
            normalizedUrl,
            apiKey ?? string.Empty,
            model ?? string.Empty,
            groupName: defaultGroupName ?? string.Empty,
            name: name ?? BuildDefaultName(normalizedUrl, lineNumber));
    }

    private static IndependentImportDraftRow ParseIndependentDraftRow(string[] columns, int lineNumber)
    {
        if (columns.Length is < 1 or > 4)
        {
            throw new InvalidOperationException($"第 {lineNumber} 行格式不正确，请使用 URL、URL|Key|模型 或 名称|URL|Key|模型。");
        }

        return columns.Length switch
        {
            1 when LooksLikeUrl(columns[0]) => new IndependentImportDraftRow(
                null,
                columns[0],
                null,
                null,
                HasName: false,
                HasBaseUrl: true,
                HasApiKey: false,
                HasModel: false),
            1 => new IndependentImportDraftRow(
                null,
                null,
                columns[0],
                null,
                HasName: false,
                HasBaseUrl: false,
                HasApiKey: true,
                HasModel: false),
            2 when LooksLikeUrl(columns[0]) => new IndependentImportDraftRow(
                null,
                columns[0],
                columns[1],
                null,
                HasName: false,
                HasBaseUrl: true,
                HasApiKey: true,
                HasModel: false),
            2 => new IndependentImportDraftRow(
                columns[0],
                columns[1],
                null,
                null,
                HasName: true,
                HasBaseUrl: true,
                HasApiKey: false,
                HasModel: false),
            3 when LooksLikeUrl(columns[0]) => new IndependentImportDraftRow(
                null,
                columns[0],
                columns[1],
                columns[2],
                HasName: false,
                HasBaseUrl: true,
                HasApiKey: true,
                HasModel: true),
            3 => new IndependentImportDraftRow(
                columns[0],
                columns[1],
                columns[2],
                null,
                HasName: true,
                HasBaseUrl: true,
                HasApiKey: true,
                HasModel: false),
            4 => new IndependentImportDraftRow(
                columns[0],
                columns[1],
                columns[2],
                columns[3],
                HasName: true,
                HasBaseUrl: true,
                HasApiKey: true,
                HasModel: true),
            _ => throw new InvalidOperationException($"第 {lineNumber} 行格式不正确，请使用 URL、URL|Key|模型 或 名称|URL|Key|模型。")
        };
    }

    private BatchSiteEntry CreateSharedEntry(string[] columns, int lineNumber)
    {
        var groupName = NormalizeNullable(SharedGroupName)
            ?? throw new InvalidOperationException("同站共享导入需要先填写分组名。");
        if (columns.Length is < 1 or > 2)
        {
            throw new InvalidOperationException($"第 {lineNumber} 行格式不正确，同站共享导入请使用 URL 或 名称|URL。");
        }

        var name = columns.Length == 2 && !LooksLikeUrl(columns[0])
            ? NormalizeNullable(columns[0])
            : null;
        var baseUrl = columns.Length == 2 && !LooksLikeUrl(columns[0])
            ? columns[1]
            : columns[0];
        var normalizedUrl = NormalizeUrl(baseUrl, lineNumber);

        return new BatchSiteEntry(
            normalizedUrl,
            NormalizeNullable(SharedApiKey) ?? string.Empty,
            NormalizeNullable(SharedModel) ?? string.Empty,
            groupName: groupName,
            name: name ?? BuildDefaultName(normalizedUrl, lineNumber));
    }

    private void RefreshImportPreview()
    {
        if (string.IsNullOrWhiteSpace(ImportText))
        {
            HasImportError = false;
            ImportStatusText = IsSharedImport
                ? "同站共享：每行填写 URL 或 名称|URL，共用下方 Key 和模型。"
                : "独立入口：每行填写 URL、URL|Key|模型 或 名称|URL|Key|模型；省略 Key/模型会沿用上一行。";
            ImportPreview = "粘贴入口后可先预览再导入。";
            return;
        }

        try
        {
            var entries = ParseImportEntries();
            HasImportError = false;
            ImportStatusText = $"识别到 {entries.Count} 条入口，导入时会按接口地址替换重复项。";
            ImportPreview = BuildImportPreview(entries);
        }
        catch (Exception ex)
        {
            SetImportError(ex.Message);
        }
    }

    private static string BuildImportPreview(IReadOnlyList<BatchSiteEntry> entries)
    {
        var lines = entries
            .Take(6)
            .Select(static entry =>
            {
                var keyText = string.IsNullOrWhiteSpace(entry.ApiKey) ? "未提供 Key" : "已提供 Key";
                var modelText = string.IsNullOrWhiteSpace(entry.Model) ? "未指定模型" : entry.Model;
                var groupText = string.IsNullOrWhiteSpace(entry.GroupName) ? "独立入口" : $"分组 {entry.GroupName}";
                return $"- [{groupText}] {entry.DisplayName} -> {entry.BaseUrl} | {keyText} | {modelText}";
            })
            .ToList();

        if (entries.Count > lines.Count)
        {
            lines.Add($"- 另有 {entries.Count - lines.Count} 条未展开");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void SetImportError(string message)
    {
        HasImportError = true;
        ImportStatusText = $"导入格式错误：{message}";
        ImportPreview = message;
    }

    private int FindSiteIndex(string baseUrl)
    {
        var normalizedBaseUrl = NormalizeComparableBaseUrl(baseUrl);
        for (var index = 0; index < Sites.Count; index++)
        {
            if (string.Equals(
                    NormalizeComparableBaseUrl(Sites[index].BaseUrl),
                    normalizedBaseUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<string> EnumerateContentLines(string rawText)
        => (rawText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(static line =>
                !string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith('#') &&
                !line.StartsWith("//", StringComparison.Ordinal));

    private static string[] SplitColumns(string line)
        => (line.Contains('\t') ? line.Split('\t') : line.Split('|'))
            .Select(static part => part.Trim())
            .ToArray();

    private static bool LooksLikeUrl(string? value)
        => BatchEndpointText.LooksLikeBaseUrl(value);

    private static string NormalizeUrl(string? value, int lineNumber)
    {
        var baseUrl = BatchEndpointText.NormalizeBaseUrl(NormalizeNullable(value));
        if (baseUrl is null)
        {
            throw new InvalidOperationException($"第 {lineNumber} 行接口地址无效：{value ?? "空"}");
        }

        return baseUrl;
    }

    private static string BuildDefaultName(string baseUrl, int index)
        => BatchEndpointText.TryGetHost(baseUrl) ?? $"入口 {index}";

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeComparableBaseUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');

    private static void ReplaceAvailableModels(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !target.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(value.Trim());
            }
        }
    }

    private static string ResolveSiteGroupName(BatchSiteEntry site)
        => NormalizeNullable(site.GroupName)
           ?? NormalizeNullable(site.Name)
           ?? TryGetHost(site.BaseUrl)
           ?? "未命名Site";

    private string BuildGroupNameForRows(IReadOnlyList<BatchSiteEntry> rows)
    {
        var first = rows[0];
        return FirstNonEmpty(SharedGroupName, first.Name, TryGetHost(first.BaseUrl))
               ?? $"Site组 {CountExistingGroups() + 1}";
    }

    private int CountExistingGroups()
        => Sites
            .Select(static site => NormalizeNullable(site.GroupName))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static string? TryGetHost(string? value)
        => BatchEndpointText.TryGetHost(value);

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(NormalizeNullable).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static bool IsEmptySiteRow(BatchSiteEntry site)
        => string.IsNullOrWhiteSpace(site.Name) &&
           string.IsNullOrWhiteSpace(site.BaseUrl) &&
           string.IsNullOrWhiteSpace(site.ApiKey) &&
           string.IsNullOrWhiteSpace(site.Model) &&
           string.IsNullOrWhiteSpace(site.GroupName);

}
