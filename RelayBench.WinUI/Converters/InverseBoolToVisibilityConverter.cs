using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace RelayBench.WinUI.Converters;

/// <summary>
/// Converts a boolean to Visibility, inverting the value:
/// true -> Collapsed, false -> Visible.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b && b)
            return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility v)
            return v != Visibility.Visible;
        return false;
    }
}
