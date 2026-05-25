using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class ClientApiStatusDetailDialog : ContentDialog
{
    public ClientApiStatusDetailDialog(ClientApiStatusRow row)
    {
        Row = row;
        InitializeComponent();
    }

    public ClientApiStatusRow Row { get; }
}
