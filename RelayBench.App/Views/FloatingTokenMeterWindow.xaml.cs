using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;

namespace RelayBench.App.Views;

public partial class FloatingTokenMeterWindow : Window
{
    private const double EdgePadding = 14d;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private bool _isPositionLocked;
    private bool _isMousePassThrough;

    public FloatingTokenMeterWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? OpenMainWindowRequested;

    public event EventHandler? HideRequested;

    public event EventHandler? ResetRequested;

    public event EventHandler? PlacementChanged;

    public event EventHandler? SettingsChanged;

    public bool IsPositionLocked => _isPositionLocked;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        if (Left <= 0 && Top <= 0)
        {
            Left = workArea.Right - Width - 28d;
            Top = workArea.Top + 126d;
        }

        ClampToWorkArea();
        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_isPositionLocked || _isMousePassThrough)
        {
            return;
        }

        try
        {
            DragMove();
            SnapToNearestEdge();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OpenMainWindowMenuItem_OnClick(object sender, RoutedEventArgs e)
        => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);

    private void LockPositionMenuItem_OnClick(object sender, RoutedEventArgs e)
        => SetPositionLocked(LockPositionMenuItem.IsChecked);

    private void MousePassThroughMenuItem_OnClick(object sender, RoutedEventArgs e)
        => SetMousePassThrough(MousePassThroughMenuItem.IsChecked);

    private void ResetCounterMenuItem_OnClick(object sender, RoutedEventArgs e)
        => ResetRequested?.Invoke(this, EventArgs.Empty);

    private void HideMenuItem_OnClick(object sender, RoutedEventArgs e)
        => HideRequested?.Invoke(this, EventArgs.Empty);

    public void SetMousePassThrough(bool isEnabled)
    {
        _isMousePassThrough = isEnabled;
        MousePassThroughMenuItem.IsChecked = isEnabled;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        exStyle = isEnabled
            ? exStyle | WsExTransparent
            : exStyle & ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, exStyle);
    }

    public void SetPositionLocked(bool isLocked)
    {
        _isPositionLocked = isLocked;
        LockPositionMenuItem.IsChecked = isLocked;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SnapToNearestEdge()
    {
        var workArea = SystemParameters.WorkArea;
        var centerX = Left + Width / 2d;
        var targetLeft = centerX < workArea.Left + workArea.Width / 2d
            ? workArea.Left + EdgePadding
            : workArea.Right - Width - EdgePadding;
        var targetTop = Math.Clamp(Top, workArea.Top + EdgePadding, workArea.Bottom - Height - EdgePadding);

        AnimateWindowPosition(targetLeft, targetTop);
    }

    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, workArea.Left + EdgePadding, workArea.Right - Width - EdgePadding);
        Top = Math.Clamp(Top, workArea.Top + EdgePadding, workArea.Bottom - Height - EdgePadding);
    }

    private void AnimateWindowPosition(double targetLeft, double targetTop)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = targetLeft;
            Top = targetTop;
            PlacementChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var leftAnimation = CreatePositionAnimation(Left, targetLeft);
        var topAnimation = CreatePositionAnimation(Top, targetTop);
        topAnimation.Completed += (_, _) => PlacementChanged?.Invoke(this, EventArgs.Empty);

        BeginAnimation(LeftProperty, leftAnimation);
        BeginAnimation(TopProperty, topAnimation);
    }

    private static DoubleAnimation CreatePositionAnimation(double from, double to)
        => new()
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
}
