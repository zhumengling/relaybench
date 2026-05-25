using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.Services;

/// <summary>
/// Supported export formats for diagnostic reports.
/// </summary>
public enum ExportFormat
{
    Json,
    Markdown
}

/// <summary>
/// Bundles history entries, chart data, and logs into a single diagnostic export file.
/// Supports JSON and Markdown output formats.
/// </summary>
internal sealed class DiagnosticReportService
{
    private readonly IHistoryRepository _historyRepository;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DiagnosticReportService(IHistoryRepository historyRepository)
    {
        _historyRepository = historyRepository;
    }

    /// <summary>
    /// Bundles history entries, chart data, and logs into a single export file.
    /// </summary>
    /// <param name="reportIds">History report IDs to include.</param>
    /// <param name="format">Export format (JSON or Markdown).</param>
    /// <param name="outputDirectory">Target directory for the export file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Full path to the generated export file.</returns>
    /// <exception cref="IOException">If the output directory is not writable.</exception>
    public async Task<string> ExportAsync(
        IReadOnlyList<string> reportIds,
        ExportFormat format,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reportIds);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        EnsureDirectoryWritable(outputDirectory);

        var reports = new List<HistoryReport>();
        foreach (var id in reportIds)
        {
            ct.ThrowIfCancellationRequested();
            var report = await _historyRepository.GetAsync(id, ct).ConfigureAwait(false);
            if (report is not null)
            {
                reports.Add(report);
            }
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var extension = format == ExportFormat.Json ? ".json" : ".md";
        var fileName = $"diagnostic-report-{timestamp}{extension}";
        var filePath = Path.Combine(outputDirectory, fileName);

        var content = format == ExportFormat.Json
            ? FormatAsJson(reports)
            : FormatAsMarkdown(reports);

        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct).ConfigureAwait(false);

        return filePath;
    }

    public async Task<string> ExportBundleAsync(
        IReadOnlyList<string> reportIds,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reportIds);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        EnsureDirectoryWritable(outputDirectory);

        var reports = new List<HistoryReport>();
        foreach (var id in reportIds)
        {
            ct.ThrowIfCancellationRequested();
            var report = await _historyRepository.GetAsync(id, ct).ConfigureAwait(false);
            if (report is not null)
            {
                reports.Add(report);
            }
        }

        var bundleRoot = Path.Combine(outputDirectory, $"diagnostic-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..57]);
        Directory.CreateDirectory(bundleRoot);
        await File.WriteAllTextAsync(
            Path.Combine(bundleRoot, "index.md"),
            FormatAsMarkdown(reports),
            Encoding.UTF8,
            ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(bundleRoot, "index.json"),
            FormatAsJson(reports),
            Encoding.UTF8,
            ct).ConfigureAwait(false);

        foreach (var report in reports)
        {
            ct.ThrowIfCancellationRequested();
            var reportDirectory = Path.Combine(
                bundleRoot,
                $"{report.CreatedAtUtc:yyyyMMdd-HHmmss}-{SanitizePathSegment(report.RunId)}");
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(reportDirectory, "report.md"),
                FormatSingleReportMarkdown(report),
                Encoding.UTF8,
                ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(reportDirectory, "payload.json"),
                report.PayloadJson,
                Encoding.UTF8,
                ct).ConfigureAwait(false);
            await WritePayloadArtifactsAsync(report.PayloadJson, reportDirectory, ct).ConfigureAwait(false);
        }

        return bundleRoot;
    }

    private static void EnsureDirectoryWritable(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException(
                    $"Output directory is not writable: {outputDirectory}", ex);
            }
            catch (IOException)
            {
                throw;
            }
        }

        // Verify write access by attempting to create and delete a temporary file.
        var testFile = Path.Combine(outputDirectory, $".write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(testFile)) { }
            File.Delete(testFile);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(
                $"Output directory is not writable: {outputDirectory}", ex);
        }
        catch (IOException)
        {
            throw;
        }
    }

    private static string FormatAsJson(List<HistoryReport> reports)
    {
        var exportPayload = new DiagnosticExportPayload
        {
            ExportedAtUtc = DateTime.UtcNow,
            ReportCount = reports.Count,
            Reports = reports.Select(r => new DiagnosticReportEntry
            {
                RunId = r.RunId,
                CreatedAtUtc = r.CreatedAtUtc,
                TestType = r.TestType,
                Endpoint = r.Endpoint,
                Summary = r.Summary,
                Score = r.Score,
                DurationMs = r.DurationMs,
                PayloadJson = r.PayloadJson
            }).ToList()
        };

        return JsonSerializer.Serialize(exportPayload, s_jsonOptions);
    }

    private static string FormatAsMarkdown(List<HistoryReport> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Diagnostic Report");
        sb.AppendLine();
        sb.AppendLine($"**Exported:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Report Count:** {reports.Count}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var report in reports)
        {
            sb.AppendLine($"## {report.TestType} — {report.Endpoint}");
            sb.AppendLine();
            sb.AppendLine($"- **Run ID:** {report.RunId}");
            sb.AppendLine($"- **Date:** {report.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"- **Summary:** {report.Summary}");

            if (report.Score.HasValue)
            {
                sb.AppendLine($"- **Score:** {report.Score.Value:F2}");
            }

            if (report.DurationMs.HasValue)
            {
                sb.AppendLine($"- **Duration:** {report.DurationMs.Value} ms");
            }

            if (!string.IsNullOrWhiteSpace(report.PayloadJson))
            {
                sb.AppendLine();
                sb.AppendLine("### Detail Data");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(report.PayloadJson);
                sb.AppendLine("```");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatSingleReportMarkdown(HistoryReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# RelayBench Report {report.RunId}");
        sb.AppendLine();
        sb.AppendLine($"- Type: {report.TestType}");
        sb.AppendLine($"- Created: {report.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- 入口: {report.Endpoint}");
        sb.AppendLine($"- Score: {(report.Score.HasValue ? report.Score.Value.ToString("F2") : "--")}");
        sb.AppendLine($"- Duration: {(report.DurationMs.HasValue ? $"{report.DurationMs.Value} ms" : "--")}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine(report.Summary);
        sb.AppendLine();
        sb.AppendLine("## Payload");
        sb.AppendLine();
        sb.AppendLine("See `payload.json` in this folder.");
        return sb.ToString();
    }

    private static async Task WritePayloadArtifactsAsync(
        string payloadJson,
        string reportDirectory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
            {
                return;
            }

            var textArtifacts = ExtractTextArtifacts(document.RootElement, "payload").Take(64).ToArray();
            var fileArtifacts = ExtractFileArtifacts(document.RootElement, "payload").Take(32).ToArray();
            if (textArtifacts.Length == 0 && fileArtifacts.Length == 0)
            {
                return;
            }

            var artifactDirectory = Path.Combine(reportDirectory, "artifacts");
            Directory.CreateDirectory(artifactDirectory);
            foreach (var artifact in textArtifacts)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = $"{SanitizePathSegment(artifact.Path)}.txt";
                await File.WriteAllTextAsync(
                    Path.Combine(artifactDirectory, fileName),
                    artifact.Value,
                    Encoding.UTF8,
                    ct).ConfigureAwait(false);
            }

            foreach (var artifact in fileArtifacts)
            {
                ct.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(artifact.SourcePath);
                var fileName = $"{SanitizePathSegment(artifact.Path)}{extension}";
                var destination = Path.Combine(artifactDirectory, fileName);
                await using var source = File.OpenRead(artifact.SourcePath);
                await using var target = File.Create(destination);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            // Raw payload remains available as payload.json.
        }
    }

    private static IEnumerable<(string Path, string Value)> ExtractTextArtifacts(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var childPath = $"{path}-{property.Name}";
                if (property.Name.Equals("sections", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var section in ExtractLegacySectionArtifacts(property.Value, childPath))
                    {
                        yield return section;
                    }
                }

                if (IsArtifactProperty(property.Name) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    yield return (childPath, property.Value.GetString()!);
                }

                foreach (var child in ExtractTextArtifacts(property.Value, childPath))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in ExtractTextArtifacts(item, $"{path}-{index++}"))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<(string Path, string Value)> ExtractLegacySectionArtifacts(JsonElement sections, string path)
    {
        var index = 0;
        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object)
            {
                index++;
                continue;
            }

            var title = TryGetString(section, "Title") ?? TryGetString(section, "title") ?? $"section-{index}";
            var content = TryGetString(section, "Content") ?? TryGetString(section, "content");
            if (!string.IsNullOrWhiteSpace(content))
            {
                yield return ($"{path}-{index}-{title}", content);
            }

            index++;
        }
    }

    private static IEnumerable<(string Path, string SourcePath)> ExtractFileArtifacts(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var childPath = $"{path}-{property.Name}";
                if (IsFileArtifactProperty(property.Name) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    TryGetExistingArtifactFile(property.Value.GetString(), out var file))
                {
                    yield return (childPath, file.FullName);
                }

                foreach (var child in ExtractFileArtifacts(property.Value, childPath))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in ExtractFileArtifacts(item, $"{path}-{index++}"))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool IsArtifactProperty(string name)
        => name.Equals("StandardOutput", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("StandardError", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("RawTrace", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("RawTraceOutput", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("TraceOutput", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("CommandLine", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("RawOutput", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileArtifactProperty(string name)
        => name.Equals("ImagePath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("MapImagePath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("RouteMapImagePath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ChartImagePath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ScreenshotPath", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ArtifactPath", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetExistingArtifactFile(string? path, out FileInfo file)
    {
        file = null!;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (!IsSupportedArtifactFileExtension(extension))
        {
            return false;
        }

        try
        {
            file = new FileInfo(path);
            return file.Exists;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSupportedArtifactFileExtension(string extension)
        => extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null
                };
            }
        }

        return null;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "report" : sanitized[..Math.Min(80, sanitized.Length)];
    }

    private sealed class DiagnosticExportPayload
    {
        public DateTime ExportedAtUtc { get; set; }
        public int ReportCount { get; set; }
        public List<DiagnosticReportEntry> Reports { get; set; } = [];
    }

    private sealed class DiagnosticReportEntry
    {
        public string RunId { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public string TestType { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public double? Score { get; set; }
        public int? DurationMs { get; set; }
        public string PayloadJson { get; set; } = string.Empty;
    }
}
