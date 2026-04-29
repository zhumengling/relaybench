using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RelayBench.App.Services;
using RelayBench.App.ViewModels;

namespace RelayBench.App;

public partial class MainWindow : Window
{
    private const int OverlayBackdropOpenDurationMs = 180;
    private const int OverlayPanelOpenDurationMs = 260;
    private const int OverlayCloseDurationMs = 180;
    private const double OverlayOpenInitialScale = 0.965d;
    private const double OverlayCloseTargetScale = 0.985d;
    private const double OverlayOpenInitialOffsetY = 18d;
    private const double OverlayCloseTargetOffsetY = 12d;
    private const int WindowOpenDurationMs = 300;
    private const double WindowOpenInitialScale = 0.975d;
    private const double WindowOpenInitialOffsetY = 18d;
    private const int WindowStateTransitionDurationMs = 280;
    private const double WindowStateMaximizedTransitionScale = 0.982d;
    private const double WindowStateRestoredTransitionScale = 1.018d;
    private const int WorkbenchPageTransitionDurationMs = 220;
    private const double WorkbenchPageTransitionOffsetX = 18d;
    private const int GlobalTaskProgressOpenDurationMs = 320;
    private const int GlobalTaskProgressCloseDurationMs = 250;
    private const int GlobalTaskProgressFillDurationMs = 560;
    private const double GlobalTaskProgressOpenInitialScale = 0.987d;
    private const double GlobalTaskProgressOpenInitialOffsetY = -18d;
    private const double GlobalTaskProgressCloseTargetScale = 0.994d;
    private const double GlobalTaskProgressCloseTargetOffsetY = -14d;
    private const int WindowCloseDurationMs = 280;
    private const double WindowCloseTargetScale = 0.958d;
    private const double WindowCloseTargetOffsetY = 32d;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

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
    private int _globalTaskProgressAnimationVersion;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

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
        ProxyChartActivityOverlayCanvas.SizeChanged += (_, _) => ScheduleProxyChartActivityOverlayUpdate();
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyRoundedWindowCorners();
        ApplyOverlayStates(immediate: true);
        UpdateGlobalTaskProgressVisual(immediate: true);
        _lastWorkbenchPageKey = _viewModel?.SelectedWorkbenchPageKey ?? string.Empty;
        ScheduleProxyChartViewportWidthUpdate();
        PlayWindowOpenAnimation();
    }

    private void ApplyRoundedWindowCorners()
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            var preference = DwmWindowCornerPreferenceRound;
            DwmSetWindowAttribute(
                helper.Handle,
                DwmWindowCornerPreference,
                ref preference,
                Marshal.SizeOf<int>());
        }
        catch
        {
            // DWM rounded corner preference is best-effort on supported Windows versions.
        }
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

        if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.IsGlobalTaskProgressVisible), StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(() => UpdateGlobalTaskProgressVisual(immediate: false), DispatcherPriority.Loaded);
            return;
        }

        if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.GlobalTaskProgressFraction), StringComparison.Ordinal))
        {
            Dispatcher.BeginInvoke(AnimateGlobalTaskProgressFill, DispatcherPriority.Loaded);
            return;
        }

        if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.ProxyChartDialogImage), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(MainWindowViewModel.IsNativeSingleCapabilityChartVisible), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(MainWindowViewModel.IsBusy), StringComparison.Ordinal))
        {
            ScheduleProxyChartActivityOverlayUpdate();
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

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void MinimizeWindowButton_OnClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestoreWindowButton_OnClick(object sender, RoutedEventArgs e)
        => ToggleWindowState();

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleWindowState()
    {
        if (ResizeMode == ResizeMode.NoResize)
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
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
        _overlayAnimations[nameof(MainWindowViewModel.IsProxyMultiModelPickerOpen)] =
            new OverlayAnimationState(ProxyMultiModelPickerOverlay, ProxyMultiModelPickerOverlayPanel, static viewModel => viewModel.IsProxyMultiModelPickerOpen);
        _overlayAnimations[nameof(MainWindowViewModel.IsOfficialApiTraceDialogOpen)] =
            new OverlayAnimationState(OfficialApiTraceOverlay, OfficialApiTraceOverlayPanel, static viewModel => viewModel.IsOfficialApiTraceDialogOpen);
        _overlayAnimations[nameof(MainWindowViewModel.IsConfirmationDialogOpen)] =
            new OverlayAnimationState(ConfirmationDialogOverlay, ConfirmationDialogOverlayPanel, static viewModel => viewModel.IsConfirmationDialogOpen);
        _overlayAnimations[nameof(MainWindowViewModel.IsClientApplyTargetDialogOpen)] =
            new OverlayAnimationState(ClientApplyTargetOverlay, ClientApplyTargetOverlayPanel, static viewModel => viewModel.IsClientApplyTargetDialogOpen);
        _overlayAnimations[nameof(MainWindowViewModel.IsAboutDialogOpen)] =
            new OverlayAnimationState(AboutDialogOverlay, AboutDialogOverlayPanel, static viewModel => viewModel.IsAboutDialogOpen);
        _overlayAnimations[nameof(MainWindowViewModel.IsProxyEndpointHistoryOpen)] =
            new OverlayAnimationState(ProxyEndpointHistoryOverlay, ProxyEndpointHistoryOverlayPanel, static viewModel => viewModel.IsProxyEndpointHistoryOpen);
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

    private void UpdateGlobalTaskProgressVisual(bool immediate)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (immediate)
        {
            SetGlobalTaskProgressVisualState(_viewModel.IsGlobalTaskProgressVisible, _viewModel.GlobalTaskProgressFraction);
            return;
        }

        AnimateGlobalTaskProgressVisibility(_viewModel.IsGlobalTaskProgressVisible);
    }

    private void SetGlobalTaskProgressVisualState(bool isVisible, double fraction)
    {
        _globalTaskProgressAnimationVersion++;
        GlobalTaskProgressBorder.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        GlobalTaskProgressBorder.Opacity = isVisible ? 1d : 0d;

        var (scale, translate) = EnsureElementTransforms(GlobalTaskProgressBorder);
        scale.ScaleX = 1d;
        scale.ScaleY = 1d;
        translate.X = 0d;
        translate.Y = 0d;

        var fillScale = GetGlobalTaskProgressFillScale();
        fillScale.ScaleX = Math.Clamp(fraction, 0d, 1d);
        fillScale.ScaleY = 1d;
    }

    private void AnimateGlobalTaskProgressVisibility(bool isVisible)
    {
        _globalTaskProgressAnimationVersion++;
        var version = _globalTaskProgressAnimationVersion;

        StopAnimation(GlobalTaskProgressBorder, UIElement.OpacityProperty);
        var (scale, translate) = EnsureElementTransforms(GlobalTaskProgressBorder);
        StopAnimation(scale, ScaleTransform.ScaleXProperty);
        StopAnimation(scale, ScaleTransform.ScaleYProperty);
        StopAnimation(translate, TranslateTransform.YProperty);

        if (isVisible)
        {
            GlobalTaskProgressBorder.Visibility = Visibility.Visible;
            GlobalTaskProgressBorder.Opacity = 0d;
            scale.ScaleX = GlobalTaskProgressOpenInitialScale;
            scale.ScaleY = GlobalTaskProgressOpenInitialScale;
            translate.Y = GlobalTaskProgressOpenInitialOffsetY;

            AnimateDouble(GlobalTaskProgressBorder, UIElement.OpacityProperty, 0d, 1d, GlobalTaskProgressOpenDurationMs, CreateEaseOut());
            AnimateDouble(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, 1d, GlobalTaskProgressOpenDurationMs, CreateEaseOut());
            AnimateDouble(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, 1d, GlobalTaskProgressOpenDurationMs, CreateEaseOut());
            AnimateDouble(translate, TranslateTransform.YProperty, translate.Y, 0d, GlobalTaskProgressOpenDurationMs, CreateEaseOut());
            AnimateGlobalTaskProgressFill();
            return;
        }

        if (GlobalTaskProgressBorder.Visibility != Visibility.Visible)
        {
            SetGlobalTaskProgressVisualState(false, 0d);
            return;
        }

        AnimateDouble(
            GlobalTaskProgressBorder,
            UIElement.OpacityProperty,
            GlobalTaskProgressBorder.Opacity,
            0d,
            GlobalTaskProgressCloseDurationMs,
            CreateEaseIn());
        AnimateDouble(
            scale,
            ScaleTransform.ScaleXProperty,
            scale.ScaleX,
            GlobalTaskProgressCloseTargetScale,
            GlobalTaskProgressCloseDurationMs,
            CreateEaseIn());
        AnimateDouble(
            scale,
            ScaleTransform.ScaleYProperty,
            scale.ScaleY,
            GlobalTaskProgressCloseTargetScale,
            GlobalTaskProgressCloseDurationMs,
            CreateEaseIn());

        var hideAnimation = CreateAnimation(
            translate.Y,
            GlobalTaskProgressCloseTargetOffsetY,
            GlobalTaskProgressCloseDurationMs,
            CreateEaseIn());
        hideAnimation.Completed += (_, _) =>
        {
            if (_viewModel is null ||
                version != _globalTaskProgressAnimationVersion ||
                _viewModel.IsGlobalTaskProgressVisible)
            {
                return;
            }

            SetGlobalTaskProgressVisualState(false, 0d);
        };
        translate.BeginAnimation(TranslateTransform.YProperty, hideAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateGlobalTaskProgressFill()
    {
        if (_viewModel is null)
        {
            return;
        }

        var fillScale = GetGlobalTaskProgressFillScale();
        StopAnimation(fillScale, ScaleTransform.ScaleXProperty);
        var target = Math.Clamp(_viewModel.GlobalTaskProgressFraction, 0d, 1d);
        AnimateDouble(fillScale, ScaleTransform.ScaleXProperty, fillScale.ScaleX, target, GlobalTaskProgressFillDurationMs, CreateEaseOut());
    }

    private ScaleTransform GetGlobalTaskProgressFillScale()
    {
        if (GlobalTaskProgressFill.RenderTransform is ScaleTransform scaleTransform)
        {
            return scaleTransform;
        }

        scaleTransform = new ScaleTransform(0d, 1d);
        GlobalTaskProgressFill.RenderTransform = scaleTransform;
        GlobalTaskProgressFill.RenderTransformOrigin = new Point(0d, 0.5d);
        return scaleTransform;
    }

    private void ScheduleProxyChartViewportWidthUpdate()
        => Dispatcher.BeginInvoke(
            UpdateProxyChartViewportWidth,
            DispatcherPriority.Loaded);

    private void ScheduleProxyChartActivityOverlayUpdate()
        => Dispatcher.BeginInvoke(
            UpdateProxyChartActivityOverlay,
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
        ScheduleProxyChartActivityOverlayUpdate();
    }

    private void UpdateProxyChartActivityOverlay()
    {
        ProxyChartActivityOverlayCanvas.Children.Clear();

        if (DataContext is not MainWindowViewModel viewModel ||
            !viewModel.IsBusy ||
            viewModel.IsNativeSingleCapabilityChartVisible ||
            viewModel.ProxyChartDialogImage is null ||
            viewModel.CurrentProxyChartActivityRegions.Count == 0)
        {
            return;
        }

        foreach (var region in viewModel.CurrentProxyChartActivityRegions)
        {
            if (region.Bounds.Width <= 0 || region.Bounds.Height <= 0)
            {
                continue;
            }

            ProxyChartActivityOverlayCanvas.Children.Add(CreateProxyChartActivityLine(region.Bounds));
        }
    }

    private static UIElement CreateProxyChartActivityLine(Rect bounds)
    {
        var lineHeight = Math.Max(3d, bounds.Height);
        var segmentWidth = Math.Min(190d, Math.Max(72d, bounds.Width * 0.24d));
        var segmentTransform = new TranslateTransform(-segmentWidth, 0d);
        var segment = new Border
        {
            Width = segmentWidth,
            Height = lineHeight,
            CornerRadius = new CornerRadius(lineHeight / 2d),
            Background = CreateProxyChartActivityBrush(),
            Opacity = 0.72d,
            RenderTransform = segmentTransform
        };

        var host = new Grid
        {
            Width = bounds.Width,
            Height = lineHeight,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        host.Children.Add(segment);
        Canvas.SetLeft(host, bounds.X);
        Canvas.SetTop(host, bounds.Y);

        var flowAnimation = new DoubleAnimation
        {
            From = -segmentWidth,
            To = bounds.Width,
            Duration = TimeSpan.FromMilliseconds(1180),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        segmentTransform.BeginAnimation(TranslateTransform.XProperty, flowAnimation, HandoffBehavior.SnapshotAndReplace);

        var breathAnimation = new DoubleAnimation
        {
            From = 0.38d,
            To = 0.92d,
            Duration = TimeSpan.FromMilliseconds(680),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        segment.BeginAnimation(UIElement.OpacityProperty, breathAnimation, HandoffBehavior.SnapshotAndReplace);

        return host;
    }

    private static Brush CreateProxyChartActivityBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 37, 99, 235), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(165, 37, 99, 235), 0.38));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(205, 168, 85, 247), 0.58));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 37, 99, 235), 1));
        brush.Freeze();
        return brush;
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
            MaxWidth = 420
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
