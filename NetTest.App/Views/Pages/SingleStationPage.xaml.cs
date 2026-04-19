using System.Windows.Controls;

namespace NetTest.App.Views.Pages;

public partial class SingleStationPage : UserControl
{
    public SingleStationPage()
    {
        InitializeComponent();
    }

    private void LiveOutputTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }
}
