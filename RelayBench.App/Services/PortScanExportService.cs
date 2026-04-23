using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

public sealed class PortScanExportService
{
    private readonly string _exportsDirectory;

    public PortScanExportService()
    {
        _exportsDirectory = RelayBenchPaths.PortScanExportsDirectory;
    }

    public string ExportCsv(PortScanExportSnapshot snapshot)
    {
        var exportDirectory = CreateExportDirectory(snapshot.SuggestedName);
        var filePath = Path.Combine(exportDirectory, "port-scan-results.csv");

        StringBuilder builder = new();
        builder.AppendLine("Scope,Target,Endpoint,Address,Port,Protocol,LatencyMs,ServiceHint,Banner,TlsSummary,ApplicationSummary,ProbeNotes");

        foreach (var row in snapshot.CurrentFindings)
        {
            builder.AppendLine(ToCsvLine(row));
        }

        foreach (var row in snapshot.BatchFindings)
        {
            builder.AppendLine(ToCsvLine(row));
        }

        if (snapshot.CurrentFindings.Count == 0 && snapshot.BatchFindings.Count == 0)
        {
            builder.AppendLine(ToCsvValue("empty") + "," + ToCsvValue(snapshot.TargetLabel) + ",,,,,,,,,,");
        }

        File.WriteAllText(filePath, builder.ToString(), new UTF8Encoding(false));
        WriteSummarySidecar(exportDirectory, snapshot);
        return filePath;
    }

    public string ExportExcel(PortScanExportSnapshot snapshot)
    {
        var exportDirectory = CreateExportDirectory(snapshot.SuggestedName);
        var filePath = Path.Combine(exportDirectory, "port-scan-results.xlsx");

        List<ExcelSheetDefinition> sheets =
        [
            BuildOverviewSheet(snapshot),
            BuildCurrentFindingsSheet(snapshot),
            BuildBatchSummarySheet(snapshot),
            BuildBatchFindingsSheet(snapshot)
        ];

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        using FileStream fileStream = File.Create(filePath);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Create);

        WriteArchiveEntry(archive, "[Content_Types].xml", BuildContentTypesXml(sheets.Count));
        WriteArchiveEntry(archive, "_rels/.rels", BuildRootRelationshipsXml());
        WriteArchiveEntry(archive, "docProps/app.xml", BuildAppXml(sheets));
        WriteArchiveEntry(archive, "docProps/core.xml", BuildCoreXml());
        WriteArchiveEntry(archive, "xl/workbook.xml", BuildWorkbookXml(sheets));
        WriteArchiveEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationshipsXml(sheets.Count));
        WriteArchiveEntry(archive, "xl/styles.xml", BuildStylesXml());

        for (var index = 0; index < sheets.Count; index++)
        {
            WriteArchiveEntry(archive, $"xl/worksheets/sheet{index + 1}.xml", BuildWorksheetXml(sheets[index]));
        }

        WriteSummarySidecar(exportDirectory, snapshot);
        return filePath;
    }

    private string CreateExportDirectory(string suggestedName)
    {
        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(suggestedName) ? "port-scan" : suggestedName);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        var exportDirectory = Path.Combine(_exportsDirectory, $"{stamp}_{safeName}");
        Directory.CreateDirectory(exportDirectory);
        return exportDirectory;
    }

    private static void WriteSummarySidecar(string exportDirectory, PortScanExportSnapshot snapshot)
    {
        StringBuilder builder = new();
        builder.AppendLine("PortScan Export Summary");
        builder.AppendLine($"GeneratedAt: {snapshot.ExportedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();

        foreach (var entry in snapshot.SummaryRows)
        {
            builder.AppendLine($"{entry.Label}: {entry.Value}");
        }

        File.WriteAllText(Path.Combine(exportDirectory, "summary.txt"), builder.ToString(), new UTF8Encoding(false));
    }

    private static ExcelSheetDefinition BuildOverviewSheet(PortScanExportSnapshot snapshot)
    {
        List<IReadOnlyList<object?>> rows =
        [
            new object?[] { "字段", "值" }
        ];

        foreach (var entry in snapshot.SummaryRows)
        {
            rows.Add([entry.Label, entry.Value]);
        }

        rows.Add(["当前结果条数", snapshot.CurrentFindings.Count]);
        rows.Add(["批量汇总条数", snapshot.BatchRows.Count]);
        rows.Add(["批量明细条数", snapshot.BatchFindings.Count]);

        return new ExcelSheetDefinition(
            "Overview",
            rows,
            [
                new ExcelColumnDefinition(26, ExcelCellKind.Text),
                new ExcelColumnDefinition(84, ExcelCellKind.Wrap)
            ],
            FreezeHeaderRow: false,
            EnableAutoFilter: false,
            UseOverviewLabelStyle: true);
    }

    private static ExcelSheetDefinition BuildCurrentFindingsSheet(PortScanExportSnapshot snapshot)
    {
        List<IReadOnlyList<object?>> rows =
        [
            new object?[] { "Scope", "Target", "Endpoint", "Address", "Port", "Protocol", "LatencyMs", "ServiceHint", "Banner", "TlsSummary", "ApplicationSummary", "ProbeNotes" }
        ];

        if (snapshot.CurrentFindings.Count == 0)
        {
            rows.Add(["current", snapshot.TargetLabel, "无数据", null, null, null, null, null, null, null, null, null]);
        }
        else
        {
            foreach (var row in snapshot.CurrentFindings)
            {
                rows.Add(ToExcelRow(row));
            }
        }

        return new ExcelSheetDefinition(
            "CurrentFindings",
            rows,
            BuildFindingColumns());
    }

    private static ExcelSheetDefinition BuildBatchSummarySheet(PortScanExportSnapshot snapshot)
    {
        List<IReadOnlyList<object?>> rows =
        [
            new object?[] { "Target", "Status", "OpenEndpointCount", "OpenPortCount", "ResolvedAddresses", "Summary", "Error", "CheckedAt" }
        ];

        if (snapshot.BatchRows.Count == 0)
        {
            rows.Add(["暂无批量结果", null, null, null, null, null, null, null]);
        }
        else
        {
            foreach (var row in snapshot.BatchRows)
            {
                rows.Add([row.Target, row.Status, row.OpenEndpointCount, row.OpenPortCount, row.ResolvedAddresses, row.Summary, row.Error, row.CheckedAt]);
            }
        }

        return new ExcelSheetDefinition(
            "BatchSummary",
            rows,
            [
                new ExcelColumnDefinition(24, ExcelCellKind.Text),
                new ExcelColumnDefinition(14, ExcelCellKind.Center),
                new ExcelColumnDefinition(18, ExcelCellKind.Number),
                new ExcelColumnDefinition(16, ExcelCellKind.Number),
                new ExcelColumnDefinition(34, ExcelCellKind.Wrap),
                new ExcelColumnDefinition(44, ExcelCellKind.Wrap),
                new ExcelColumnDefinition(28, ExcelCellKind.Wrap),
                new ExcelColumnDefinition(22, ExcelCellKind.Center)
            ]);
    }

    private static ExcelSheetDefinition BuildBatchFindingsSheet(PortScanExportSnapshot snapshot)
    {
        List<IReadOnlyList<object?>> rows =
        [
            new object?[] { "Scope", "Target", "Endpoint", "Address", "Port", "Protocol", "LatencyMs", "ServiceHint", "Banner", "TlsSummary", "ApplicationSummary", "ProbeNotes" }
        ];

        if (snapshot.BatchFindings.Count == 0)
        {
            rows.Add(["batch", snapshot.TargetLabel, "暂无批量明细", null, null, null, null, null, null, null, null, null]);
        }
        else
        {
            foreach (var row in snapshot.BatchFindings)
            {
                rows.Add(ToExcelRow(row));
            }
        }

        return new ExcelSheetDefinition(
            "BatchFindings",
            rows,
            BuildFindingColumns());
    }

    private static IReadOnlyList<ExcelColumnDefinition> BuildFindingColumns()
        =>
        [
            new ExcelColumnDefinition(12, ExcelCellKind.Center),
            new ExcelColumnDefinition(24, ExcelCellKind.Text),
            new ExcelColumnDefinition(24, ExcelCellKind.Text),
            new ExcelColumnDefinition(22, ExcelCellKind.Text),
            new ExcelColumnDefinition(10, ExcelCellKind.Number),
            new ExcelColumnDefinition(10, ExcelCellKind.Center),
            new ExcelColumnDefinition(12, ExcelCellKind.Number),
            new ExcelColumnDefinition(20, ExcelCellKind.Text),
            new ExcelColumnDefinition(36, ExcelCellKind.Wrap),
            new ExcelColumnDefinition(36, ExcelCellKind.Wrap),
            new ExcelColumnDefinition(42, ExcelCellKind.Wrap),
            new ExcelColumnDefinition(42, ExcelCellKind.Wrap)
        ];

    private static string ToCsvLine(PortScanExportFindingRow row)
        => string.Join(
            ",",
            ToCsvValue(row.Scope),
            ToCsvValue(row.Target),
            ToCsvValue(row.Endpoint),
            ToCsvValue(row.Address),
            ToCsvValue(row.Port.ToString(CultureInfo.InvariantCulture)),
            ToCsvValue(row.Protocol),
            ToCsvValue(row.LatencyMs.ToString(CultureInfo.InvariantCulture)),
            ToCsvValue(row.ServiceHint),
            ToCsvValue(row.Banner),
            ToCsvValue(row.TlsSummary),
            ToCsvValue(row.ApplicationSummary),
            ToCsvValue(row.ProbeNotes));

    private static IReadOnlyList<object?> ToExcelRow(PortScanExportFindingRow row)
        => [row.Scope, row.Target, row.Endpoint, row.Address, row.Port, row.Protocol, row.LatencyMs, row.ServiceHint, row.Banner, row.TlsSummary, row.ApplicationSummary, row.ProbeNotes];

    private static string ToCsvValue(string? value)
    {
        var normalized = value?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') ?? string.Empty;
        normalized = normalized.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{normalized}\"";
    }

    private static void WriteArchiveEntry(ZipArchive archive, string relativePath, string content)
    {
        var entry = archive.CreateEntry(relativePath, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        using StreamWriter writer = new(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildContentTypesXml(int sheetCount)
    {
        StringBuilder builder = new();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        builder.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        builder.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        builder.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        builder.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        builder.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
        builder.Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
        for (var index = 1; index <= sheetCount; index++)
        {
            builder.Append($"<Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        builder.Append("</Types>");
        return builder.ToString();
    }

    private static string BuildRootRelationshipsXml()
        => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>";

    private static string BuildAppXml(IReadOnlyList<ExcelSheetDefinition> sheets)
    {
        StringBuilder builder = new();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">");
        builder.Append("<Application>RelayBench</Application>");
        builder.Append($"<TitlesOfParts><vt:vector size=\"{sheets.Count}\" baseType=\"lpstr\">");
        foreach (var sheet in sheets)
        {
            builder.Append($"<vt:lpstr>{EscapeXml(sheet.Name)}</vt:lpstr>");
        }

        builder.Append("</vt:vector></TitlesOfParts>");
        builder.Append($"<HeadingPairs><vt:vector size=\"2\" baseType=\"variant\"><vt:variant><vt:lpstr>Worksheets</vt:lpstr></vt:variant><vt:variant><vt:i4>{sheets.Count}</vt:i4></vt:variant></vt:vector></HeadingPairs>");
        builder.Append("</Properties>");
        return builder.ToString();
    }

    private static string BuildCoreXml()
    {
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:creator>RelayBench</dc:creator><cp:lastModifiedBy>RelayBench</cp:lastModifiedBy><dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified></cp:coreProperties>";
    }

    private static string BuildWorkbookXml(IReadOnlyList<ExcelSheetDefinition> sheets)
    {
        StringBuilder builder = new();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><bookViews><workbookView xWindow=\"0\" yWindow=\"0\" windowWidth=\"24000\" windowHeight=\"14000\"/></bookViews><sheets>");
        for (var index = 0; index < sheets.Count; index++)
        {
            builder.Append($"<sheet name=\"{EscapeXml(SanitizeSheetName(sheets[index].Name))}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>");
        }

        builder.Append("</sheets></workbook>");
        return builder.ToString();
    }

    private static string BuildWorkbookRelationshipsXml(int sheetCount)
    {
        StringBuilder builder = new();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var index = 1; index <= sheetCount; index++)
        {
            builder.Append($"<Relationship Id=\"rId{index}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index}.xml\"/>");
        }

        builder.Append($"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        builder.Append("</Relationships>");
        return builder.ToString();
    }

    private static string BuildStylesXml()
        => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
           "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
           "<fonts count=\"3\">" +
           "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
           "<font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
           "<font><b/><color rgb=\"FF1F1F1F\"/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
           "</fonts>" +
           "<fills count=\"4\">" +
           "<fill><patternFill patternType=\"none\"/></fill>" +
           "<fill><patternFill patternType=\"gray125\"/></fill>" +
           "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
           "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
           "</fills>" +
           "<borders count=\"2\">" +
           "<border/>" +
           "<border><left style=\"thin\"><color auto=\"1\"/></left><right style=\"thin\"><color auto=\"1\"/></right><top style=\"thin\"><color auto=\"1\"/></top><bottom style=\"thin\"><color auto=\"1\"/></bottom></border>" +
           "</borders>" +
           "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
           "<cellXfs count=\"7\">" +
           "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
           "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
           "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" +
           "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
           "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" +
           "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
           "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
           "</cellXfs>" +
           "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
           "</styleSheet>";

    private static string BuildWorksheetXml(ExcelSheetDefinition sheet)
    {
        StringBuilder builder = new();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        if (sheet.FreezeHeaderRow && sheet.Rows.Count > 1)
        {
            builder.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        }
        else
        {
            builder.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
        }

        if (sheet.Columns.Count > 0)
        {
            builder.Append("<cols>");
            for (var index = 0; index < sheet.Columns.Count; index++)
            {
                var width = sheet.Columns[index].Width.ToString("0.##", CultureInfo.InvariantCulture);
                builder.Append($"<col min=\"{index + 1}\" max=\"{index + 1}\" width=\"{width}\" customWidth=\"1\"/>");
            }

            builder.Append("</cols>");
        }

        builder.Append("<sheetFormatPr defaultRowHeight=\"18\"/>");
        builder.Append("<sheetData>");

        for (var rowIndex = 0; rowIndex < sheet.Rows.Count; rowIndex++)
        {
            var row = sheet.Rows[rowIndex];
            var rowNumber = rowIndex + 1;
            var isHeaderRow = rowIndex == 0;
            builder.Append(isHeaderRow
                ? $"<row r=\"{rowNumber}\" ht=\"22\" customHeight=\"1\">"
                : $"<row r=\"{rowNumber}\">");

            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                var cellReference = $"{GetColumnName(columnIndex + 1)}{rowNumber}";
                var kind = columnIndex < sheet.Columns.Count ? sheet.Columns[columnIndex].Kind : ExcelCellKind.Text;
                var styleIndex = ResolveStyleIndex(sheet, rowIndex, columnIndex, kind);
                builder.Append(BuildCellXml(cellReference, row[columnIndex], styleIndex));
            }

            builder.Append("</row>");
        }

        builder.Append("</sheetData>");

        if (sheet.EnableAutoFilter && sheet.Rows.Count > 1 && sheet.Columns.Count > 0)
        {
            builder.Append($"<autoFilter ref=\"A1:{GetColumnName(sheet.Columns.Count)}{sheet.Rows.Count}\"/>");
        }

        builder.Append("</worksheet>");
        return builder.ToString();
    }

    private static int ResolveStyleIndex(ExcelSheetDefinition sheet, int rowIndex, int columnIndex, ExcelCellKind kind)
    {
        if (rowIndex == 0)
        {
            return 1;
        }

        if (sheet.UseOverviewLabelStyle && columnIndex == 0)
        {
            return 2;
        }

        return kind switch
        {
            ExcelCellKind.Wrap => 4,
            ExcelCellKind.Number => 5,
            ExcelCellKind.Center => 6,
            _ => 3
        };
    }

    private static string BuildCellXml(string cellReference, object? value, int styleIndex)
    {
        var styleAttribute = $" s=\"{styleIndex}\"";
        if (value is null)
        {
            return $"<c r=\"{cellReference}\"{styleAttribute} t=\"inlineStr\"><is><t></t></is></c>";
        }

        return value switch
        {
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                => $"<c r=\"{cellReference}\"{styleAttribute}><v>{Convert.ToString(value, CultureInfo.InvariantCulture)}</v></c>",
            _ => $"<c r=\"{cellReference}\"{styleAttribute} t=\"inlineStr\"><is><t xml:space=\"preserve\">{EscapeXml(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}</t></is></c>"
        };
    }

    private static string GetColumnName(int columnNumber)
    {
        StringBuilder builder = new();
        while (columnNumber > 0)
        {
            columnNumber--;
            builder.Insert(0, (char)('A' + (columnNumber % 26)));
            columnNumber /= 26;
        }

        return builder.ToString();
    }

    private static string EscapeXml(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return SecurityElement.Escape(value) ?? string.Empty;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalidChars.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "port-scan" : sanitized;
    }

    private static string SanitizeSheetName(string value)
    {
        var sanitized = value
            .Replace("\\", "_", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .Replace(":", "_", StringComparison.Ordinal)
            .Replace("*", "_", StringComparison.Ordinal)
            .Replace("?", "_", StringComparison.Ordinal)
            .Replace("[", "_", StringComparison.Ordinal)
            .Replace("]", "_", StringComparison.Ordinal);

        sanitized = sanitized.Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "Sheet";
        }

        return sanitized.Length <= 31 ? sanitized : sanitized[..31];
    }

    private enum ExcelCellKind
    {
        Text,
        Number,
        Center,
        Wrap
    }

    private sealed record ExcelColumnDefinition(double Width, ExcelCellKind Kind);

    private sealed record ExcelSheetDefinition(
        string Name,
        IReadOnlyList<IReadOnlyList<object?>> Rows,
        IReadOnlyList<ExcelColumnDefinition> Columns,
        bool FreezeHeaderRow = true,
        bool EnableAutoFilter = true,
        bool UseOverviewLabelStyle = false);
}

public sealed record PortScanExportSnapshot(
    string SuggestedName,
    DateTimeOffset ExportedAt,
    string TargetLabel,
    IReadOnlyList<PortScanExportSummaryRow> SummaryRows,
    IReadOnlyList<PortScanExportFindingRow> CurrentFindings,
    IReadOnlyList<PortScanExportBatchRow> BatchRows,
    IReadOnlyList<PortScanExportFindingRow> BatchFindings);

public sealed record PortScanExportSummaryRow(string Label, string Value);

public sealed record PortScanExportFindingRow(
    string Scope,
    string Target,
    string Endpoint,
    string Address,
    int Port,
    string Protocol,
    long LatencyMs,
    string ServiceHint,
    string? Banner,
    string? TlsSummary,
    string? ApplicationSummary,
    string? ProbeNotes);

public sealed record PortScanExportBatchRow(
    string Target,
    string Status,
    int OpenEndpointCount,
    int OpenPortCount,
    string ResolvedAddresses,
    string Summary,
    string Error,
    string CheckedAt);
