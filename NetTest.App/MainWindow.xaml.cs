using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NetTest.App.Services;
using NetTest.App.ViewModels;

namespace NetTest.App;

public partial class MainWindow : Window
{
    private const int OverlayBackdropOpenDurationMs = 260;
    private const int OverlayPanelOpenDurationMs = 360;
    private const int OverlayCloseDurationMs = 230;
    private const double OverlayOpenInitialScale = 0.915d;
    private const double OverlayCloseTargetScale = 0.962d;
    private const double OverlayOpenInitialOffsetY = 42d;
    private const double OverlayCloseTargetOffsetY = 24d;
    private const int WindowOpenDurationMs = 380;
    private const double WindowOpenInitialScale = 0.956d;
    private const double WindowOpenInitialOffsetY = 30d;
    private const int WindowStateTransitionDurationMs = 280;
    private const double WindowStateMaximizedTransitionScale = 0.982d;
    private const double WindowStateRestoredTransitionScale = 1.018d;
    private const int WorkbenchPageTransitionDurationMs = 300;
    private const double WorkbenchPageTransitionOffsetX = 46d;
    private const int WindowCloseDurationMs = 280;
    private const double WindowCloseTargetScale = 0.958d;
    private const double WindowCloseTargetOffsetY = 32d;

    private sealed class OverlayAnimationState(
        Grid overlay,
        Border panel,
        Func<MainWindowViewModel, bool> isOpenAccessor)
    {
        public Grid Overlay { get; } = overlay;

        public Border Panel { get; } = panel;

        public Func<MainWindowViewModel, bool> IsOpenAccessor { get; } = isOpenAccessor;

        public int Version { get; set; }
    }

    private readonly ToolTip _proxyChartHoverToolTip = new()
    {
        Placement = PlacementMode.Mouse,
        StaysOpen = true,
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(208, 213, 221)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 8, 10, 8),
        HasDropShadow = true
    };

    private readonly Dictionary<string, OverlayAnimationState> _overlayAnimations = [];
    private ProxyChartHitRegion? _activeProxyChartHitRegion;
    private MainWindowViewModel? _viewModel;
    private bool _allowWindowClose;
    private bool _isWindowCloseAnimationRunning;
    private bool _hasPlayedWindowOpenAnimation;
    private string _lastWorkbenchPageKey = string.Empty;

    public MainWindow()
    {
        Opacity = 0;
        InitializeComponent();

        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;

        InitializeOverlayAnimations();

        ProxyChartDialogImageControl.ToolTip = _proxyChartHoverToolTip;
        Loaded += MainWindow_OnLoaded;
        SizeChanged += (_, _) => ScheduleProxyChartViewportWidthUpdate();
        StateChanged += (_, _) => AnimateShellStateTransition();
        Closed += (_, _) => DetachViewModel();
        ProxyChartImageScrollViewer.IsVisibleChanged += (_, _) => ScheduleProxyChartViewportWidthUpdate();
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyOverlayStates(immediate: true);
        _lastWorkbenchPageKey = _viewModel?.SelectedWorkbenchPageKey ?? string.Empty;
        ScheduleProxyChartViewportWidthUpdate();
        PlayWindowOpenAnimation();
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null || string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }

        if (_overlayAnimations.ContainsKey(e.PropertyName))
        {
            Dispatcher.BeginInvoke(() => UpdateOverlayVisibility(e.PropertyName), DispatcherPriority.Loaded);
            return;
        }

        if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.SelectedWorkbenchPageKey), StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(AnimateWorkbenchPageTransition, DispatcherPriority.Loaded);
        }
    }

    private void LiveOutputTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }

    private void ProxyChartImageScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
        => ScheduleProxyChartViewportWidthUpdate();

    private void ProxyChartDialogImage_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Image image ||
            DataContext is not MainWindowViewModel viewModel ||
            viewModel.CurrentProxyChartHitRegions.Count == 0)
        {
            HideProxyChartHitToolTip();
            return;
        }

        var position = e.GetPosition(image);
        var hitRegion = viewModel.CurrentProxyChartHitRegions.FirstOrDefault(region => region.Bounds.Contains(position));
        if (hitRegion is null)
        {
            HideProxyChartHitToolTip();
            return;
        }

        if (_proxyChartHoverToolTip.IsOpen &&
            _activeProxyChartHitRegion is not null &&
            string.Equals(_activeProxyChartHitRegion.Title, hitRegion.Title, StringComparison.Ordinal) &&
            string.Equals(_activeProxyChartHitRegion.Description, hitRegion.Description, StringComparison.Ordinal))
        {
            return;
        }

        _activeProxyChartHitRegion = hitRegion;
        _proxyChartHoverToolTip.Content = BuildProxyChartHitToolTipContent(hitRegion);
        _proxyChartHoverToolTip.IsOpen = true;
    }

    private void ProxyChartDialogImage_OnMouseLeave(object sender, MouseEventArgs e)
        => HideProxyChartHitToolTip();

    private void ProxyBatchDraftDataGrid_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid ||
            e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindVisualParent<ButtonBase>(source) is not null ||
            FindVisualParent<ScrollBar>(source) is not null ||
            FindVisualParent<DataGridColumnHeader>(source) is not null)
        {
            return;
        }

        var cell = FindVisualParent<DataGridCell>(source);
        if (cell is null ||
            cell.IsReadOnly ||
            cell.IsEditing)
        {
            return;
        }

        if (!cell.IsKeyboardFocusWithin)
        {
            cell.Focus();
        }

        dataGrid.SelectedItem = cell.DataContext;
        dataGrid.CurrentCell = new DataGridCellInfo(cell);
        if (!dataGrid.BeginEdit(e))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (FindVisualChild<TextBox>(cell) is not TextBox textBox)
            {
                return;
            }

            textBox.Focus();
            textBox.CaretIndex = textBox.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private void InitializeOverlayAnimations()
    {
        if (_viewModel is null)
        {
            return;
        }

        _overlayAnimations[nameof(MainWindowViewModel.IsProxyTrendChartOpen)] =
            new OverlayAnimationState(ProxyChartOverlay, ProxyChartOverlayPanel, static viewModel => viewModel.IsProxyTrendChartOpen);
        _overlayAnimations[nameof(MainWindowViewModel.IsProxyModelPickerOpen)] =
            new OverlayAnimationState(ProxyModelPickerOverlay, ProxyModelPickerOverlayPanel, static viewModel => viewModel.IsProxyModelPickerOpen);
        _overlayAnimations[nameof(MainWindowViewModel.IsOfficialApiTraceDialogOpen)] =
            new OverlayAnimationState(OfficialApiTraceOverlay, OfficialApiTraceOverlayPanel, static viewModel => viewModel.IsOfficialApiTraceDialogOpen);
        _overlayAnimations[nameof(MainWindowViewModel.IsProxyBatchEditorOpen)] =
            new OverlayAnimationState(ProxyBatchEditorOverlay, ProxyBatchEditorOverlayPanel, static viewModel => viewModel.IsProxyBatchEditorOpen);
    }

    private void ApplyOverlayStates(bool immediate)
    {
        if (_viewModel is null)
        {
            return;
        }

        foreach (var item in _overlayAnimations.Values)
        {
            var isOpen = item.IsOpenAccessor(_viewModel);
            if (immediate)
            {
                SetOverlayState(item, isOpen);
            }
            else
            {
                AnimateOverlay(item, isOpen);
            }
        }
    }

    private void UpdateOverlayVisibility(string propertyName)
    {
        if (_viewModel is null || !_overlayAnimations.TryGetValue(propertyName, out var item))
        {
            return;
        }

        AnimateOverlay(item, item.IsOpenAccessor(_viewModel));
    }

    private static void SetOverlayState(OverlayAnimationState item, bool isOpen)
    {
        item.Version++;
        item.Overlay.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        item.Overlay.IsHitTestVisible = isOpen;
        item.Overlay.Opacity = isOpen ? 1d : 0d;

        var (scale, translate) = EnsureElementTransforms(item.Panel);
        scale.ScaleX = 1d;
        scale.ScaleY = 1d;
        translate.X = 0d;
        translate.Y = 0d;
        item.Panel.Opacity = 1d;
    }

    private void AnimateOverlay(OverlayAnimationState item, bool isOpen)
    {
        item.Version++;
        var version = item.Version;
        StopOverlayAnimations(item);

        var (scale, translate) = EnsureElementTransforms(item.Panel);
        if (isOpen)
        {
            item.Overlay.Visibility = Visibility.Visible;
            item.Overlay.IsHitTestVisible = true;
            item.Overlay.Opacity = 0d;
            item.Panel.Opacity = 0d;
            scale.ScaleX = OverlayOpenInitialScale;
            scale.ScaleY = OverlayOpenInitialScale;
            translate.Y = OverlayOpenInitialOffsetY;

            AnimateDouble(item.Overlay, UIElement.OpacityProperty, 0d, 1d, OverlayBackdropOpenDurationMs, CreateEaseOut());
            AnimateDouble(item.Panel, UIElement.OpacityProperty, 0d, 1d, OverlayPanelOpenDurationMs, CreateEaseOut());
            AnimateDouble(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, 1d, OverlayPanelOpenDurationMs, CreateEaseOut());
            AnimateDouble(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, 1d, OverlayPanelOpenDurationMs, CreateEaseOut());
            AnimateDouble(translate, TranslateTransform.YProperty, translate.Y, 0d, OverlayPanelOpenDurationMs, CreateEaseOut());
            return;
        }

        if (item.Overlay.Visibility != Visibility.Visible)
        {
            SetOverlayState(item, false);
            return;
        }

        item.Overlay.IsHitTestVisible = false;
        AnimateDouble(item.Overlay, UIElement.OpacityProperty, item.Overlay.Opacity, 0d, OverlayCloseDurationMs, CreateEaseIn());
        AnimateDouble(item.Panel, UIElement.OpacityProperty, item.Panel.Opacity, 0d, OverlayCloseDurationMs, CreateEaseIn());
        AnimateDouble(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, OverlayCloseTargetScale, OverlayCloseDurationMs, CreateEaseIn());
        AnimateDouble(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, OverlayCloseTargetScale, OverlayCloseDurationMs, CreateEaseIn());

        var hideAnimation = CreateAnimation(translate.Y, OverlayCloseTargetOffsetY, OverlayCloseDurationMs, CreateEaseIn());
        hideAnimation.Completed += (_, _) =>
        {
            if (_viewModel is null || version != item.Version || item.IsOpenAccessor(_viewModel))
            {
                return;
            }

            SetOverlayState(item, false);
        };
        translate.BeginAnimation(TranslateTransform.YProperty, hideAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void PlayWindowOpenAnimation()
    {
        if (_hasPlayedWindowOpenAnimation)
        {
            return;
        }

        _hasPlayedWindowOpenAnimation = true;
        var (scale, translate) = EnsureElementTransforms(MainShellBorder);
        MainShellBorder.Opacity = 0d;
        scale.ScaleX = WindowOpenInitialScale;
        scale.ScaleY = WindowOpenInitialScale;
        translate.Y = WindowOpenInitialOffsetY;

        AnimateDouble(this, Window.OpacityProperty, 0d, 1d, WindowOpenDurationMs, CreateEaseOut());
        AnimateDouble(MainShellBorder, UIElement.OpacityProperty, 0d, 1d, WindowOpenDurationMs, CreateEaseOut());
        AnimateDouble(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, 1d, WindowOpenDurationMs, CreateEaseOut());
        AnimateDouble(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, 1d, WindowOpenDurationMs, CreateEaseOut());
        AnimateDouble(translate, TranslateTransform.YProperty, translate.Y, 0d, WindowOpenDurationMs, CreateEaseOut());
    }

    private void AnimateShellStateTransition()
    {
        if (!IsLoaded || _isWindowCloseAnimationRunning)
        {
            return;
        }

        var (scale, _) = EnsureElementTransforms(MainShellBorder);
        var fromScale = WindowState == WindowState.Maximized
            ? WindowStateMaximizedTransitionScale
            : WindowStateRestoredTransitionScale;
        StopAnimation(scale, ScaleTransform.ScaleXProperty);
        StopAnimation(scale, ScaleTransform.ScaleYProperty);
        StopAnimation(MainShellBorder, UIElement.OpacityProperty);

        scale.ScaleX = fromScale;
        scale.ScaleY = fromScale;
        MainShellBorder.Opacity = 0.985d;

        AnimateDouble(MainShellBorder, UIElement.OpacityProperty, MainShellBorder.Opacity, 1d, WindowStateTransitionDurationMs, CreateEaseOut());
        AnimateDouble(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, 1d, WindowStateTransitionDurationMs, CreateEaseOut());
        AnimateDouble(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, 1d, WindowStateTransitionDurationMs, CreateEaseOut());
    }

    private void AnimateWorkbenchPageTransition()
    {
        if (_viewModel is null)
        {
            return;
        }

        var currentPageKey = _viewModel.SelectedWorkbenchPageKey ?? string.Empty;
        if (string.Equals(currentPageKey, _lastWorkbenchPageKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastWorkbenchPageKey = currentPageKey;
        var (_, translate) = EnsureElementTransforms(WorkbenchPageHost);
        StopAnimation(WorkbenchPageHost, UIElement.OpacityProperty);
        StopAnimation(translate, TranslateTransform.XProperty);

        WorkbenchPageHost.Opacity = 0d;
        translate.X = WorkbenchPageTransitionOffsetX;

        AnimateDouble(WorkbenchPageHost, UIElement.OpacityProperty, 0d, 1d, WorkbenchPageTransitionDurationMs, CreateEaseOut());
        AnimateDouble(translate, TranslateTransform.XProperty, translate.X, 0d, WorkbenchPageTransitionDurationMs, CreateEaseOut());
    }

    private void ScheduleProxyChartViewportWidthUpdate()
        => Dispatcher.BeginInvoke(
            UpdateProxyChartViewportWidth,
            DispatcherPriority.Loaded);

    private void UpdateProxyChartViewportWidth()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var viewportWidth = ProxyChartImageScrollViewer?.ViewportWidth ?? 0;
        if (viewportWidth <= 0)
        {
            viewportWidth = ProxyChartImageScrollViewer?.ActualWidth ?? 0;
        }

        if (viewportWidth <= 0 && ProxyChartImageScrollViewer?.Parent is FrameworkElement parent)
        {
            viewportWidth = parent.ActualWidth;
        }

        if (viewportWidth <= 0)
        {
            viewportWidth = ActualWidth - 84;
        }

        viewModel.UpdateProxyChartViewportWidth(viewportWidth);
    }

    private void HideProxyChartHitToolTip()
    {
        _activeProxyChartHitRegion = null;
        if (_proxyChartHoverToolTip.IsOpen)
        {
            _proxyChartHoverToolTip.IsOpen = false;
        }
    }

    private static object BuildProxyChartHitToolTipContent(ProxyChartHitRegion hitRegion)
    {
        var panel = new StackPanel
        {
            MaxWidth = 320
        };

        panel.Children.Add(new TextBlock
        {
            Text = hitRegion.Title,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Black
        });

        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            Text = hitRegion.Description,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 84, 103))
        });

        return panel;
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureElementTransforms(UIElement element)
    {
        if (element.RenderTransform is TransformGroup existingGroup)
        {
            var existingScale = existingGroup.Children.OfType<ScaleTransform>().FirstOrDefault();
            var existingTranslate = existingGroup.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (existingScale is not null && existingTranslate is not null)
            {
                return (existingScale, existingTranslate);
            }
        }

        var scale = new ScaleTransform(1d, 1d);
        var translate = new TranslateTransform();
        TransformGroup group = new();
        group.Children.Add(scale);
        group.Children.Add(translate);
        element.RenderTransform = group;
        if (element is FrameworkElement frameworkElement)
        {
            frameworkElement.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        return (scale, translate);
    }

    private static void AnimateDouble(
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        int durationMilliseconds,
        IEasingFunction easing)
    {
        var animation = CreateAnimation(from, to, durationMilliseconds, easing);
        switch (target)
        {
            case UIElement element:
                element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
                break;
            case Animatable animatable:
                animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
                break;
        }
    }

    private static DoubleAnimation CreateAnimation(double from, double to, int durationMilliseconds, IEasingFunction easing)
        => new()
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };

    private static IEasingFunction CreateEaseOut()
        => new QuarticEase { EasingMode = EasingMode.EaseOut };

    private static IEasingFunction CreateEaseIn()
        => new CubicEase { EasingMode = EasingMode.EaseIn };

    private static void StopAnimation(DependencyObject target, DependencyProperty property)
    {
        switch (target)
        {
            case UIElement element:
                element.BeginAnimation(property, null);
                break;
            case Animatable animatable:
                animatable.BeginAnimation(property, null);
                break;
        }
    }

    private static void StopOverlayAnimations(OverlayAnimationState item)
    {
        var (scale, translate) = EnsureElementTransforms(item.Panel);
        StopAnimation(item.Overlay, UIElement.OpacityProperty);
        StopAnimation(item.Panel, UIElement.OpacityProperty);
        StopAnimation(scale, ScaleTransform.ScaleXProperty);
        StopAnimation(scale, ScaleTransform.ScaleYProperty);
        StopAnimation(translate, TranslateTransform.XProperty);
        StopAnimation(translate, TranslateTransform.YProperty);
    }

    private void DetachViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        _viewModel = null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T matched)
            {
                return matched;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent)
        where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T matched)
            {
                return matched;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowWindowClose)
        {
            e.Cancel = true;
            if (!_isWindowCloseAnimationRunning)
            {
                _isWindowCloseAnimationRunning = true;
                BeginWindowCloseAnimation();
            }

            return;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PersistState();
        }

        base.OnClosing(e);
    }

    private void BeginWindowCloseAnimation()
    {
        HideProxyChartHitToolTip();
        var (scale, translate) = EnsureElementTransforms(MainShellBorder);
        StopAnimation(this, Window.OpacityProperty);
        StopAnimation(MainShellBorder, UIElement.OpacityProperty);
        StopAnimation(scale, ScaleTransform.ScaleXProperty);
        StopAnimation(scale, ScaleTransform.ScaleYProperty);
        StopAnimation(translate, TranslateTransform.YProperty);

        var closeAnimation = CreateAnimation(MainShellBorder.Opacity, 0d, WindowCloseDurationMs, CreateEaseIn());
        closeAnimation.Completed += (_, _) =>
        {
            _allowWindowClose = true;
            _isWindowCloseAnimationRunning = false;
            Close();
        };

        MainShellBorder.BeginAnimation(UIElement.OpacityProperty, closeAnimation, HandoffBehavior.SnapshotAndReplace);
        AnimateDouble(this, Window.OpacityProperty, Opacity, 0d, WindowCloseDurationMs, CreateEaseIn());
        AnimateDouble(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, WindowCloseTargetScale, WindowCloseDurationMs, CreateEaseIn());
        AnimateDouble(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, WindowCloseTargetScale, WindowCloseDurationMs, CreateEaseIn());
        AnimateDouble(translate, TranslateTransform.YProperty, translate.Y, WindowCloseTargetOffsetY, WindowCloseDurationMs, CreateEaseIn());
    }
}
