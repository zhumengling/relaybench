using Xunit;

namespace RelayBench.Core.Tests.Navigation;

public sealed class WorkbenchNavigationOrderTests
{
    [Fact]
    public void MainWorkbenchNavigation_PlacesModelChatAfterBatchEvaluation()
    {
        var sourcePath = FindRepoFile("RelayBench.App", "ViewModels", "MainWindowViewModel.CommandBindings.cs");
        var source = File.ReadAllText(sourcePath);

        AssertInOrder(
            source,
            "new(\"interface-diagnostics\"",
            "new(\"advanced-test-lab\"",
            "new(\"batch-evaluation\"",
            "new(\"model-chat\"",
            "new(\"application-center\"",
            "new(\"network-review\"",
            "new(\"history-reports\"");
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        var previousIndex = -1;
        foreach (var marker in markers)
        {
            var index = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Could not find marker: {marker}");
            Assert.True(index > previousIndex, $"Marker is not in the expected order: {marker}");
            previousIndex = index;
        }
    }

    private static string FindRepoFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(relativeSegments)} from {AppContext.BaseDirectory}.");
    }
}
