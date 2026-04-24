using System.Windows;
using System.Windows.Controls;

namespace RelayBench.App.Infrastructure;

public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxAssistant),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxAssistant),
            new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject dependencyObject)
        => (string)(dependencyObject.GetValue(BoundPasswordProperty) ?? string.Empty);

    public static void SetBoundPassword(DependencyObject dependencyObject, string? value)
        => dependencyObject.SetValue(BoundPasswordProperty, value ?? string.Empty);

    private static bool GetIsUpdating(DependencyObject dependencyObject)
        => (bool)dependencyObject.GetValue(IsUpdatingProperty);

    private static void SetIsUpdating(DependencyObject dependencyObject, bool value)
        => dependencyObject.SetValue(IsUpdatingProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not PasswordBox passwordBox)
        {
            return;
        }

        passwordBox.PasswordChanged -= PasswordBox_OnPasswordChanged;

        if (!GetIsUpdating(passwordBox))
        {
            passwordBox.Password = e.NewValue as string ?? string.Empty;
        }

        passwordBox.PasswordChanged += PasswordBox_OnPasswordChanged;
    }

    private static void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        SetIsUpdating(passwordBox, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        SetIsUpdating(passwordBox, false);
    }
}
