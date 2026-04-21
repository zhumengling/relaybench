namespace NetTest.App.Services;

public sealed record ProxyBatchTemplateClipboardRow(
    string EntryName,
    string BaseUrl,
    string? EntryApiKey,
    string? EntryModel);

public sealed record ProxyBatchTemplateClipboardDraftRow(
    string? EntryName,
    string? BaseUrl,
    string? EntryApiKey,
    string? EntryModel,
    bool HasEntryName,
    bool HasBaseUrl,
    bool HasEntryApiKey,
    bool HasEntryModel);

public sealed record ProxyBatchTemplateDraftRowData(
    string? EntryName,
    string? BaseUrl,
    string? EntryApiKey,
    string? EntryModel,
    bool IncludeInBatchTest);

public static class ProxyBatchTemplateClipboardParser
{
    public static IReadOnlyList<ProxyBatchTemplateClipboardRow> Parse(string? rawText)
    {
        List<ProxyBatchTemplateClipboardRow> rows = [];
        var draftRows = ParseDraftRows(rawText);

        for (var index = 0; index < draftRows.Count; index++)
        {
            var row = draftRows[index];
            var normalizedUrl = NormalizeRequiredUrl(row.BaseUrl, index + 1);
            rows.Add(new ProxyBatchTemplateClipboardRow(
                NormalizeNullable(row.EntryName) ?? BuildDefaultEntryName(normalizedUrl, index + 1),
                normalizedUrl,
                NormalizeNullable(row.EntryApiKey),
                NormalizeNullable(row.EntryModel)));
        }

        return rows;
    }

    public static IReadOnlyList<ProxyBatchTemplateClipboardDraftRow> ParseDraftRows(string? rawText)
    {
        List<ProxyBatchTemplateClipboardDraftRow> rows = [];

        foreach (var line in EnumerateContentLines(rawText))
        {
            var columns = SplitColumns(line);
            if (columns.Length == 0 || IsHeaderRow(columns))
            {
                continue;
            }

            rows.Add(ParseDraftRow(columns));
        }

        return rows;
    }

    public static IReadOnlyList<ProxyBatchTemplateDraftRowData> MergeDraftRows(
        IReadOnlyList<ProxyBatchTemplateDraftRowData> existingRows,
        IReadOnlyList<ProxyBatchTemplateClipboardDraftRow> pastedRows)
    {
        List<ProxyBatchTemplateDraftRowData> result = existingRows.ToList();
        if (result.Count == 0)
        {
            result.Add(new ProxyBatchTemplateDraftRowData(null, null, null, null, true));
        }

        var insertionIndex = FindInsertionIndex(result);
        var previous = FindPreviousRow(result, insertionIndex);

        foreach (var pastedRow in pastedRows)
        {
            var merged = new ProxyBatchTemplateDraftRowData(
                pastedRow.HasEntryName ? NormalizeNullable(pastedRow.EntryName) : previous.EntryName,
                pastedRow.HasBaseUrl ? NormalizeNullable(pastedRow.BaseUrl) : previous.BaseUrl,
                pastedRow.HasEntryApiKey ? NormalizeNullable(pastedRow.EntryApiKey) : previous.EntryApiKey,
                pastedRow.HasEntryModel ? NormalizeNullable(pastedRow.EntryModel) : previous.EntryModel,
                previous.IncludeInBatchTest);

            if (insertionIndex < result.Count && IsEmpty(result[insertionIndex]))
            {
                result[insertionIndex] = merged;
            }
            else
            {
                result.Insert(insertionIndex, merged);
            }

            previous = merged;
            insertionIndex++;
        }

        return result;
    }

    private static ProxyBatchTemplateClipboardDraftRow ParseDraftRow(string[] columns)
    {
        var normalized = columns
            .Select(static value => NormalizeNullable(value))
            .ToArray();

        return normalized.Length switch
        {
            1 when LooksLikeUrl(normalized[0]) => new ProxyBatchTemplateClipboardDraftRow(
                null,
                normalized[0],
                null,
                null,
                false,
                true,
                false,
                false),
            1 => new ProxyBatchTemplateClipboardDraftRow(
                null,
                null,
                normalized[0],
                null,
                false,
                false,
                true,
                false),
            2 when LooksLikeUrl(normalized[0]) => new ProxyBatchTemplateClipboardDraftRow(
                null,
                normalized[0],
                normalized[1],
                null,
                false,
                true,
                true,
                false),
            2 => new ProxyBatchTemplateClipboardDraftRow(
                normalized[0],
                normalized[1],
                null,
                null,
                true,
                true,
                false,
                false),
            3 when LooksLikeUrl(normalized[0]) => new ProxyBatchTemplateClipboardDraftRow(
                null,
                normalized[0],
                normalized[1],
                normalized[2],
                false,
                true,
                true,
                true),
            3 => new ProxyBatchTemplateClipboardDraftRow(
                normalized[0],
                normalized[1],
                normalized[2],
                null,
                true,
                true,
                true,
                false),
            4 => new ProxyBatchTemplateClipboardDraftRow(
                normalized[0],
                normalized[1],
                normalized[2],
                normalized[3],
                true,
                true,
                true,
                true),
            _ => throw new InvalidOperationException("批量粘贴每行最多支持 4 列：名称、URL、Key、模型。")
        };
    }

    private static IEnumerable<string> EnumerateContentLines(string? rawText)
        => (rawText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith('#') &&
                !line.StartsWith("//", StringComparison.Ordinal));

    private static string[] SplitColumns(string line)
    {
        var delimiter = line.Contains('\t') ? '\t' : '|';
        return line.Split(delimiter)
            .Select(static part => part.Trim())
            .ToArray();
    }

    private static bool IsHeaderRow(IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            return false;
        }

        static string NormalizeHeader(string? value)
            => (value ?? string.Empty)
                .TrimStart('\uFEFF')
                .Trim()
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

        var first = NormalizeHeader(columns[0]);
        var second = columns.Count > 1 ? NormalizeHeader(columns[1]) : string.Empty;

        return (first is "名称" or "入口名称" or "name") &&
               (second is "url" or "网址" or "地址" or "入口地址");
    }

    private static int FindInsertionIndex(IReadOnlyList<ProxyBatchTemplateDraftRowData> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (IsEmpty(rows[index]))
            {
                return index;
            }
        }

        return rows.Count;
    }

    private static ProxyBatchTemplateDraftRowData FindPreviousRow(
        IReadOnlyList<ProxyBatchTemplateDraftRowData> rows,
        int insertionIndex)
    {
        for (var index = Math.Min(insertionIndex - 1, rows.Count - 1); index >= 0; index--)
        {
            if (!IsEmpty(rows[index]))
            {
                return rows[index];
            }
        }

        return new ProxyBatchTemplateDraftRowData(null, null, null, null, true);
    }

    private static bool IsEmpty(ProxyBatchTemplateDraftRowData row)
        => string.IsNullOrWhiteSpace(row.EntryName) &&
           string.IsNullOrWhiteSpace(row.BaseUrl) &&
           string.IsNullOrWhiteSpace(row.EntryApiKey) &&
           string.IsNullOrWhiteSpace(row.EntryModel);

    private static bool LooksLikeUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string NormalizeRequiredUrl(string? value, int index)
    {
        var normalized = NormalizeNullable(value);
        if (!LooksLikeUrl(normalized))
        {
            throw new InvalidOperationException($"第 {index} 行的 URL 无效：{value ?? "（空）"}");
        }

        return normalized!;
    }

    private static string BuildDefaultEntryName(string? baseUrl, int index)
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
