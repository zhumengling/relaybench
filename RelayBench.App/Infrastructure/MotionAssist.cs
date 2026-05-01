using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RelayBench.App.Infrastructure;

public static class MotionAssist
{
    public static readonly DependencyProperty UseVisibilityTransitionProperty =
        DependencyProperty.RegisterAttached(
            "UseVisibilityTransition",
            typeof(bool),
            typeof(MotionAssist),
            new PropertyMetadata(false, OnUseVisibilityTransitionChanged));

    public static readonly DependencyProperty LoadedOffsetYProperty =
        DependencyProperty.RegisterAttached(
            "LoadedOffsetY",
            typeof(double),
            typeof(MotionAssist),
            new PropertyMetadata(10d));

    public static readonly DependencyProperty TransitionDurationMsProperty =
        DependencyProperty.RegisterAttached(
            "TransitionDurationMs",
            typeof(double),
            typeof(MotionAssist),
            new PropertyMetadata(180d));

    public static void SetUseVisibilityTransition(DependencyObject element, bool value)
        => element.SetValue(UseVisibilityTransitionProperty, value);

    public static bool GetUseVisibilityTransition(DependencyObject element)
        => (bool)element.GetValue(UseVisibilityTransitionProperty);

    public static void SetLoadedOffsetY(DependencyObject element, double value)
        => element.SetValue(LoadedOffsetYProperty, value);

    public static double GetLoadedOffsetY(DependencyObject element)
        => (double)element.GetValue(LoadedOffsetYProperty);

    public static void SetTransitionDurationMs(DependencyObject element, double value)
        => element.SetValue(TransitionDurationMsProperty, value);

    public static double GetTransitionDurationMs(DependencyObject element)
        => (double)element.GetValue(TransitionDurationMsProperty);

    private static void OnUseVisibilityTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.Loaded += ElementOnLoaded;
            element.IsVisibleChanged += ElementOnIsVisibleChanged;
        }
        else
        {
            element.Loaded -= ElementOnLoaded;
            element.IsVisibleChanged -= ElementOnIsVisibleChanged;
        }
    }

    private static void ElementOnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.IsVisible)
        {
            PlayEntrance(element);
        }
    }

    private static void ElementOnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && element.IsVisible)
        {
            PlayEntrance(element);
        }
    }

    private static void PlayEntrance(FrameworkElement element)
    {
        element.Opacity = 0d;

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(GetTransitionDurationMs(element), 80d, 360d));
        if (!SystemParameters.ClientAreaAnimation)
        {
            element.Opacity = 1d;
            EnsureTranslateTransform(element).Y = 0d;
            return;
        }

        var translate = EnsureTranslateTransform(element);
        translate.Y = GetLoadedOffsetY(element);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0d, 1d, new Duration(duration)) { EasingFunction = ease };
        element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

        var slide = new DoubleAnimation(GetLoadedOffsetY(element), 0d, new Duration(duration))
        {
            EasingFunction = ease
        };
        translate.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private static TranslateTransform EnsureTranslateTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform translate)
        {
            return translate;
        }

        if (element.RenderTransform is TransformGroup group)
        {
            foreach (var child in group.Children)
            {
                if (child is TranslateTransform existing)
                {
                    return existing;
                }
            }

            var newTranslate = new TranslateTransform();
            group.Children.Add(newTranslate);
            element.RenderTransform = group;
            return newTranslate;
        }

        if (element.RenderTransform is not null && element.RenderTransform != Transform.Identity)
        {
            var wrappedGroup = new TransformGroup();
            wrappedGroup.Children.Add(element.RenderTransform);
            var wrappedTranslate = new TranslateTransform();
            wrappedGroup.Children.Add(wrappedTranslate);
            element.RenderTransform = wrappedGroup;
            return wrappedTranslate;
        }

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }
}
