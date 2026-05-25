using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.Dialogs;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Pages;

public sealed partial class NetworkReviewPage : PageBase
{
    public NetworkReviewViewModel ViewModel { get; } = new();
    private bool _proxySubscribed;

    public NetworkReviewPage()
    {
        InitializeComponent();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeProxyStatus();
        RefreshProxyStatus();
        ReviewAmbientStoryboard.Begin();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ReviewAmbientStoryboard.Stop();

        if (!_proxySubscribed)
        {
            return;
        }

        var proxy = App.TransparentProxyViewModel;
        proxy.PropertyChanged -= OnProxyPropertyChanged;
        proxy.Routes.CollectionChanged -= OnProxyCollectionChanged;
        proxy.ProviderAccounts.CollectionChanged -= OnProxyCollectionChanged;
        proxy.ModelPool.CollectionChanged -= OnProxyCollectionChanged;
        proxy.RouteQueue.CollectionChanged -= OnProxyCollectionChanged;
        proxy.RecentActivityEvents.CollectionChanged -= OnProxyCollectionChanged;
        _proxySubscribed = false;
    }

    private void SubscribeProxyStatus()
    {
        if (_proxySubscribed)
        {
            return;
        }

        var proxy = App.TransparentProxyViewModel;
        proxy.PropertyChanged += OnProxyPropertyChanged;
        proxy.Routes.CollectionChanged += OnProxyCollectionChanged;
        proxy.ProviderAccounts.CollectionChanged += OnProxyCollectionChanged;
        proxy.ModelPool.CollectionChanged += OnProxyCollectionChanged;
        proxy.RouteQueue.CollectionChanged += OnProxyCollectionChanged;
        proxy.RecentActivityEvents.CollectionChanged += OnProxyCollectionChanged;
        _proxySubscribed = true;
    }

    private void OnProxyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransparentProxyViewModel.IsRunning)
            or nameof(TransparentProxyViewModel.ListenAddress)
            or nameof(TransparentProxyViewModel.ActiveConnections)
            or nameof(TransparentProxyViewModel.TokenSpeed)
            or nameof(TransparentProxyViewModel.CacheHitRate)
            or nameof(TransparentProxyViewModel.ResponseCacheSummary)
            or nameof(TransparentProxyViewModel.IoTokens)
            or nameof(TransparentProxyViewModel.ManagementSecuritySummary)
            or nameof(TransparentProxyViewModel.ProviderAccountSummary))
        {
            RefreshProxyStatus();
        }
    }

    private void OnProxyCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshProxyStatus();

    private void RefreshProxyStatus()
        => ViewModel.ApplyTransparentProxyStatus(App.TransparentProxyViewModel);

    private void OnOpenTransparentProxyClick(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not null)
        {
            App.MainWindow.NavigateToPage(typeof(TransparentProxyPage));
            return;
        }

        Frame?.Navigate(typeof(TransparentProxyPage));
    }

    private async void OnOpenApiTraceDialogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ApiTraceDialog(ViewModel).UseHostTheme(this);
        await dialog.ShowAsync();
    }

    private async void OnOpenUnlockRawTraceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NetworkReviewUnlockRow row })
        {
            ViewModel.OpenRawTraceCommand.Execute(row);
        }

        await ShowOfficialApiTraceDialogAsync();
    }

    private async Task ShowOfficialApiTraceDialogAsync()
    {
        if (!ViewModel.IsOfficialApiTraceDialogOpen)
        {
            return;
        }

        var dialog = new NetworkRawTraceDialog(
            ViewModel.OfficialApiTraceDialogTitle,
            ViewModel.OfficialApiTraceDialogContent).UseHostTheme(this);
        await dialog.ShowAsync();
        ViewModel.CloseOfficialApiTraceDialogCommand.Execute(null);
    }
}
