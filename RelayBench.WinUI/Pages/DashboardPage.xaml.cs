using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Pages;

public sealed partial class DashboardPage : PageBase
{
    public DashboardViewModel ViewModel { get; } = new();

    public DashboardPage()
    {
        InitializeComponent();
    }
}
