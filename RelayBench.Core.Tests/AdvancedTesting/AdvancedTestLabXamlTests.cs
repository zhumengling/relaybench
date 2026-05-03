using Xunit;

namespace RelayBench.Core.Tests.AdvancedTesting;

public sealed class AdvancedTestLabXamlTests
{
    [Fact]
    public void AdvancedLogRuns_UseOneWayBindings()
    {
        var xamlPath = FindRepoFile("RelayBench.App", "Views", "Pages", "AdvancedTestLabPage.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("<Run Text=\"{Binding TimeText}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Run Text=\"{Binding Level}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Run Text=\"{Binding Message}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"{Binding OverallProgress}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding DetailDialogContent}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedSuiteCards_ConstrainTextInsideNavigationRail()
    {
        var pagePath = FindRepoFile("RelayBench.App", "Views", "Pages", "AdvancedTestLabPage.xaml");
        var themePath = FindRepoFile("RelayBench.App", "Resources", "AdvancedTestLabTheme.xaml");
        var pageXaml = File.ReadAllText(pagePath);
        var themeXaml = File.ReadAllText(themePath);

        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Disabled\"", themeXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0,0,14,0\" />", themeXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"0,0,6,8\" />", themeXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"{TemplateBinding HorizontalContentAlignment}\"", themeXaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"32\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"54\"", pageXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedStopConfirmation_UsesInlineProjectDialog()
    {
        var pagePath = FindRepoFile("RelayBench.App", "Views", "Pages", "AdvancedTestLabPage.xaml");
        var viewModelPath = FindRepoFile("RelayBench.App", "ViewModels", "AdvancedTesting", "AdvancedTestLabViewModel.cs");
        var pageXaml = File.ReadAllText(pagePath);
        var viewModel = File.ReadAllText(viewModelPath);

        Assert.DoesNotContain("MessageBox.Show", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsStopConfirmationOpen", viewModel, StringComparison.Ordinal);
        Assert.Contains("ConfirmStopCommand", viewModel, StringComparison.Ordinal);
        Assert.Contains("CancelStopCommand", viewModel, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsStopConfirmationOpen, Converter={StaticResource BoolToVisibilityConverter}}\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("ConfirmStopCommand", pageXaml, StringComparison.Ordinal);
        Assert.Contains("CancelStopCommand", pageXaml, StringComparison.Ordinal);
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
