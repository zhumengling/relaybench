using Xunit;

namespace RelayBench.Core.Tests.AdvancedTesting;

public sealed class AdvancedTestLabEndpointConfigTests
{
    [Fact]
    public void AdvancedTestLabPage_ExposesIndependentEndpointInputsBeforeStartActions()
    {
        var xamlPath = FindRepoFile("RelayBench.App", "Views", "Pages", "AdvancedTestLabPage.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("AdvancedBaseUrl", xaml, StringComparison.Ordinal);
        Assert.Contains("AdvancedApiKey", xaml, StringComparison.Ordinal);
        Assert.Contains("AdvancedModel", xaml, StringComparison.Ordinal);
        Assert.Contains("AdvancedModelOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("FetchAdvancedModelsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenAdvancedTestLabProxyEndpointHistoryCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"220\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"160\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AdvancedModel, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding AdvancedModelOptionSelection, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsAdvancedModelMenuOpen, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOpen=\"{Binding IsAdvancedModelMenuOpen, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AdvancedLabModelPickerBorderStyle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEditable=\"True\"", xaml, StringComparison.Ordinal);
        Assert.True(CountOccurrences(xaml, "AdvancedLabConfigIconButtonStyle") >= 2);

        Assert.True(
            xaml.IndexOf("AdvancedModel", StringComparison.Ordinal) <
            xaml.IndexOf("StartCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void AdvancedTestLabViewModel_StartUsesIndependentConfiguredEndpoint()
    {
        var sourcePath = FindRepoFile("RelayBench.App", "ViewModels", "AdvancedTesting", "AdvancedTestLabViewModel.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("BuildConfiguredEndpoint()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var endpoint = _endpointProvider();", source, StringComparison.Ordinal);
        Assert.Contains("FetchAdvancedModelsCommand", source, StringComparison.Ordinal);
        Assert.Contains("AdvancedModelOptionSelection", source, StringComparison.Ordinal);
        Assert.Contains("IsAdvancedModelMenuOpen", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowHistoryOverlay_SupportsAdvancedTestLabTarget()
    {
        var commandBindings = File.ReadAllText(FindRepoFile("RelayBench.App", "ViewModels", "MainWindowViewModel.CommandBindings.cs"));
        var construction = File.ReadAllText(FindRepoFile("RelayBench.App", "ViewModels", "MainWindowViewModel.Construction.cs"));
        var history = File.ReadAllText(FindRepoFile("RelayBench.App", "ViewModels", "MainWindowViewModel.ProxyEndpointHistory.cs"));

        Assert.Contains("OpenAdvancedTestLabProxyEndpointHistoryCommand", commandBindings, StringComparison.Ordinal);
        Assert.Contains("OpenAdvancedTestLabProxyEndpointHistoryCommand", construction, StringComparison.Ordinal);
        Assert.Contains("ProxyEndpointHistoryApplyTarget.AdvancedTestLab", history, StringComparison.Ordinal);
        Assert.Contains("AdvancedTestLab.AdvancedBaseUrl", history, StringComparison.Ordinal);
        Assert.Contains("AdvancedTestLab.AdvancedApiKey", history, StringComparison.Ordinal);
        Assert.Contains("AdvancedTestLab.AdvancedModel", history, StringComparison.Ordinal);
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

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var startIndex = 0;
        while (true)
        {
            var matchIndex = source.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (matchIndex < 0)
            {
                return count;
            }

            count++;
            startIndex = matchIndex + value.Length;
        }
    }
}
