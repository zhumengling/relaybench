using System.Diagnostics;
using System.Reflection;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string ProjectHomepageUrl = "https://github.com/zhumengling/relaybench";
    private const string ProjectHomepageDisplayText = "github.com/zhumengling/relaybench";
    private const string ProjectLicenseDisplayText = "MIT";
    private const string ProjectAuthorDisplayText = "zhumengling";

    private bool _isAboutDialogOpen;

    public bool IsAboutDialogOpen
    {
        get => _isAboutDialogOpen;
        private set => SetProperty(ref _isAboutDialogOpen, value);
    }

    public string AboutProjectUrl => ProjectHomepageUrl;

    public string AboutProjectDisplayText => ProjectHomepageDisplayText;

    public string AboutVersionDisplayText
    {
        get
        {
            var informational = Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational.Trim();
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "--" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string AboutLicenseDisplayText => ProjectLicenseDisplayText;

    public string AboutAuthorDisplayText => ProjectAuthorDisplayText;

    private Task OpenAboutDialogAsync()
    {
        IsAboutDialogOpen = true;
        return Task.CompletedTask;
    }

    private Task CloseAboutDialogAsync()
    {
        IsAboutDialogOpen = false;
        return Task.CompletedTask;
    }

    private Task OpenProjectHomepageAsync()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ProjectHomepageUrl,
                UseShellExecute = true
            });

            StatusMessage = "已打开项目主页。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开项目主页失败：{ex.Message}";
        }

        return Task.CompletedTask;
    }
}
