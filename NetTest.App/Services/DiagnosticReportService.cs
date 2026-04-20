using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using NetTest.App.Infrastructure;

namespace NetTest.App.Services;

public sealed class DiagnosticReportService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _reportsDirectory;

    public DiagnosticReportService()
    {
        _reportsDirectory = NetTestPaths.ReportsDirectory;
        Directory.CreateDirectory(_reportsDirectory);
    }

    public string ReportsDirectory => _reportsDirectory;

    public string ExportTextReport(string reportName, IReadOnlyList<(string Title, string Content)> sections)
    {
        var bundle = ExportBundleReport(
            reportName,
            sections.Select(section => new DiagnosticReportSection(section.Title, section.Content)).ToArray(),
            Array.Empty<DiagnosticReportTextArtifact>(),
            Array.Empty<DiagnosticReportImageArtifact>(),
            new
            {
                format = "legacy-text-only"
            });

        return bundle.TextReportPath;
    }

    public DiagnosticReportBundleResult ExportBundleReport(
        string reportName,
        IReadOnlyList<DiagnosticReportSection> sections,
        IReadOnlyList<DiagnosticReportTextArtifact> textArtifacts,
        IReadOnlyList<DiagnosticReportImageArtifact> imageArtifacts,
        object structuredData)
    {
        var safeName = SanitizeFileName(reportName);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        var reportDirectory = Path.Combine(_reportsDirectory, $"{stamp}_{safeName}");
        var textReportPath = Path.Combine(reportDirectory, "report.txt");
        var manifestPath = Path.Combine(reportDirectory, "manifest.json");
        var sectionsPath = Path.Combine(reportDirectory, "sections.json");
        var structuredPath = Path.Combine(reportDirectory, "report.json");
        var zipPath = $"{reportDirectory}.zip";

        if (Directory.Exists(reportDirectory))
        {
            Directory.Delete(reportDirectory, recursive: true);
        }

        Directory.CreateDirectory(reportDirectory);

        WriteTextReport(textReportPath, sections);
        File.WriteAllText(sectionsPath, JsonSerializer.Serialize(sections, SerializerOptions), new UTF8Encoding(false));
        File.WriteAllText(structuredPath, JsonSerializer.Serialize(structuredData, SerializerOptions), new UTF8Encoding(false));

        List<string> attachments = [];
        foreach (var artifact in textArtifacts)
        {
            attachments.Add(WriteTextArtifact(reportDirectory, artifact));
        }

        foreach (var artifact in imageArtifacts)
        {
            attachments.Add(WriteImageArtifact(reportDirectory, artifact));
        }

        var manifest = new
        {
            reportName,
            generatedAt = DateTimeOffset.Now,
            textReport = "report.txt",
            sectionsFile = "sections.json",
            structuredFile = "report.json",
            screenshotReferences = imageArtifacts.Select(artifact => new
            {
                artifact.Description,
                artifact.RelativePath
            }).ToArray(),
            rawArtifacts = textArtifacts.Select(artifact => new
            {
                artifact.Description,
                artifact.RelativePath
            }).ToArray()
        };

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, SerializerOptions), new UTF8Encoding(false));

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(reportDirectory, zipPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);

        return new DiagnosticReportBundleResult(
            zipPath,
            reportDirectory,
            manifestPath,
            textReportPath,
            attachments);
    }

    private static void WriteTextReport(string filePath, IReadOnlyList<DiagnosticReportSection> sections)
    {
        StringBuilder builder = new();
        builder.AppendLine("RelayBench 诊断报告");
        builder.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();

        foreach (var section in sections)
        {
            builder.AppendLine($"## {section.Title}");
            builder.AppendLine(string.IsNullOrWhiteSpace(section.Content) ? "（空）" : section.Content.Trim());
            builder.AppendLine();
        }

        File.WriteAllText(filePath, builder.ToString().TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string WriteTextArtifact(string rootDirectory, DiagnosticReportTextArtifact artifact)
    {
        var fullPath = Path.Combine(rootDirectory, NormalizeRelativePath(artifact.RelativePath));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, artifact.Content ?? string.Empty, new UTF8Encoding(false));
        return fullPath;
    }

    private static string WriteImageArtifact(string rootDirectory, DiagnosticReportImageArtifact artifact)
    {
        var fullPath = Path.Combine(rootDirectory, NormalizeRelativePath(artifact.RelativePath));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        BitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(artifact.Image));

        using var stream = File.Create(fullPath);
        encoder.Save(stream);
        return fullPath;
    }

    private static string NormalizeRelativePath(string value)
        => value.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalidChars.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "relaybench-report" : sanitized;
    }
}
