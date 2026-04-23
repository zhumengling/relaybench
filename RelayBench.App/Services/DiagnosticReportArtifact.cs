using System.Windows.Media.Imaging;

namespace RelayBench.App.Services;

public sealed record DiagnosticReportSection(
    string Title,
    string Content);

public sealed record DiagnosticReportTextArtifact(
    string RelativePath,
    string Content,
    string Description);

public sealed record DiagnosticReportImageArtifact(
    string RelativePath,
    BitmapSource Image,
    string Description);
