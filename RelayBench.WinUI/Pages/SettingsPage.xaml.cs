using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Pages;

public sealed partial class SettingsPage : PageBase
{
    public SettingsViewModel ViewModel { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
    }
}
