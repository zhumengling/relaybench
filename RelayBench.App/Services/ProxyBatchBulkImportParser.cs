using RelayBench.Core.Models;

namespace RelayBench.App.Services;

public sealed record ProxyBatchBulkImportEntry(
    string EntryName,
    string BaseUrl,
    string? EntryApiKey,
    string? EntryModel,
    string? SiteGroupName,
    string? SiteGroupApiKey,
    string? SiteGroupModel);

public static class ProxyBatchBulkImportParser
{
    public static IReadOnlyList<ProxyBatchBulkImportEntry> ParseSharedEntries(
        string rawText,
        string? siteGroupName,
        string? sharedApiKey,
        string? sharedModel)
    {
        var normalizedGroupName = NormalizeNullable(siteGroupName)
            ?? throw new InvalidOperationException("请先填写同站组名称，再导入同站入口。");

        List<ProxyBatchBulkImportEntry> entries = [];
        foreach (var line in EnumerateContentLines(rawText))
        {
            var columns = SplitColumns(line);
            string entryName;
            string baseUrl;

            if (columns.Length == 1)
            {
                baseUrl = columns[0];
                entryName = BuildDefaultEntryName(baseUrl, entries.Count + 1);
            }
            else if (columns.Length == 2)
            {
                if (LooksLikeUrl(columns[0]) && !LooksLikeUrl(columns[1]))
                {
                    baseUrl = columns[0];
                    entryName = NormalizeNullable(columns[1]) ?? BuildDefaultEntryName(baseUrl, entries.Count + 1);
                }
                else
                {
                    entryName = NormalizeNullable(columns[0]) ?? BuildDefaultEntryName(columns[1], entries.Count + 1);
                    baseUrl = columns[1];
                }
            }
            else
            {
                throw new InvalidOperationException($"同站批量导入第 {entries.Count + 1} 行格式不正确；请使用“URL”或“名称 | URL”。");
            }

            entries.Add(new ProxyBatchBulkImportEntry(
                entryName,
                NormalizeUrl(baseUrl, entries.Count + 1),
                null,
                null,
                normalizedGroupName,
                NormalizeNullable(sharedApiKey),
                NormalizeNullable(sharedModel)));
        }

        return entries.Count == 0
            ? throw new InvalidOperationException("没有可导入的同站入口。")
            : entries;
    }

    public static IReadOnlyList<ProxyBatchBulkImportEntry> ParseIndependentEntries(
        string rawText,
        string? siteGroupName = null)
    {
        var normalizedGroupName = NormalizeNullable(siteGroupName);
        List<ProxyBatchBulkImportEntry> entries = [];

        foreach (var line in EnumerateContentLines(rawText))
        {
            var columns = SplitColumns(line);
            if (columns.Length == 0 || columns.Length > 4)
            {
                throw new InvalidOperationException($"独立批量导入第 {entries.Count + 1} 行格式不正确；请使用“名称 | URL | Key | 模型”或等价的 Tab 分列格式。");
            }

            string? entryName = null;
            string? baseUrl = null;
            string? entryApiKey = null;
            string? entryModel = null;

            switch (columns.Length)
            {
                case 1:
                    baseUrl = columns[0];
                    break;
                case 2:
                    if (LooksLikeUrl(columns[0]))
                    {
                        baseUrl = columns[0];
                        entryApiKey = NormalizeNullable(columns[1]);
                    }
                    else
                    {
                        entryName = NormalizeNullable(columns[0]);
                        baseUrl = columns[1];
                    }

                    break;
                case 3:
                    if (LooksLikeUrl(columns[0]))
                    {
                        baseUrl = columns[0];
                        entryApiKey = NormalizeNullable(columns[1]);
                        entryModel = NormalizeNullable(columns[2]);
                    }
                    else
                    {
                        entryName = NormalizeNullable(columns[0]);
                        baseUrl = columns[1];
                        entryApiKey = NormalizeNullable(columns[2]);
                    }

                    break;
                case 4:
                    entryName = NormalizeNullable(columns[0]);
                    baseUrl = columns[1];
                    entryApiKey = NormalizeNullable(columns[2]);
                    entryModel = NormalizeNullable(columns[3]);
                    break;
            }

            var normalizedUrl = NormalizeUrl(baseUrl, entries.Count + 1);
            entries.Add(new ProxyBatchBulkImportEntry(
                entryName ?? BuildDefaultEntryName(normalizedUrl, entries.Count + 1),
                normalizedUrl,
                entryApiKey,
                entryModel,
                normalizedGroupName,
                null,
                null));
        }

        return entries.Count == 0
            ? throw new InvalidOperationException("没有可导入的独立入口。")
            : entries;
    }

    private static IEnumerable<string> EnumerateContentLines(string rawText)
        => (rawText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith('#') &&
                !line.StartsWith("//", StringComparison.Ordinal));

    private static string[] SplitColumns(string line)
        => (line.Contains('\t') ? line.Split('\t') : line.Split('|'))
            .Select(static part => part.Trim())
            .ToArray();

    private static bool LooksLikeUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string NormalizeUrl(string? value, int lineNumber)
    {
        var normalized = NormalizeNullable(value);
        if (!LooksLikeUrl(normalized))
        {
            throw new InvalidOperationException($"第 {lineNumber} 行的入口地址无效：{value ?? "（空）"}");
        }

        return normalized!;
    }

    private static string BuildDefaultEntryName(string baseUrl, int index)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return $"入口 {index}";
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
