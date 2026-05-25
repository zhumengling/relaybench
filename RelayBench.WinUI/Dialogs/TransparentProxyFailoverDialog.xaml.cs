using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class TransparentProxyFailoverDialog : ContentDialog
{
    public TransparentProxyFailoverDialog(TransparentProxyViewModel viewModel)
    {
        ViewModel = viewModel;
        ResetStatusText = BuildInitialStatusText(viewModel);
        InitializeComponent();
    }

    public TransparentProxyViewModel ViewModel { get; }

    public string ResetStatusText { get; private set; }

    private void OnResetRouteCircuitClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RouteQueueEntry route })
        {
            SetResetStatus("没有识别到要重置的路由。");
            return;
        }

        var reset = ViewModel.ResetRouteCircuit(route.RouteId);
        SetResetStatus(reset
            ? $"已重置“{route.RouteName}”的熔断状态。"
            : ViewModel.StatusText);
    }

    private void SetResetStatus(string value)
    {
        ResetStatusText = string.IsNullOrWhiteSpace(value)
            ? "重置操作没有返回状态。"
            : value.Trim();
        Bindings.Update();
    }

    private static string BuildInitialStatusText(TransparentProxyViewModel viewModel)
        => viewModel.IsRunning
            ? "可对 Open/Half-Open 路由执行手动 reset；重置后下一次请求会重新参与路由选择。"
            : "代理未运行时只显示静态队列；启动透明代理后可重置运行时熔断器。";
}
