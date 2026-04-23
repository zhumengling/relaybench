namespace RelayBench.App.Services;

public sealed record DiagnosticReportBundleResult(
    string BundlePath,
    string DirectoryPath,
    string ManifestPath,
    string TextReportPath,
    IReadOnlyList<string> AttachmentPaths);
