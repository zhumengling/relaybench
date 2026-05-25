using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Core;
using Windows.System;

namespace RelayBench.WinUI.Pages;

public sealed partial class ModelChatPage : PageBase
{
    private const double Compact工作区Width = 980d;
    private const double VeryCompact工作区Width = 760d;

    public ModelChatViewModel ViewModel { get; } = new();
    private bool _proxySubscribed;
    private bool _chatMessagesSubscribed;
    private bool _autoScrollChatMessages = true;

    public ModelChatPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SizeChanged += (_, args) => QueueResponsive工作区(args.NewSize.Width);
        Loaded += (_, _) =>
        {
            SyncChatSettingsPanelLayout();
            QueueResponsive工作区(ActualWidth);
        };
    }

    private void QueueResponsive工作区(double width)
    {
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ApplyResponsive工作区(width)))
        {
            ApplyResponsive工作区(width);
        }
    }

    private void ApplyResponsive工作区(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var isCompact = width < Compact工作区Width;
        var isVeryCompact = width < VeryCompact工作区Width;
        var isStacked = width < ResponsiveLayoutService.StackedWidthThreshold;

        if (isCompact && ViewModel.IsChatSettingsPanelOpen)
        {
            ViewModel.IsChatSettingsPanelOpen = false;
        }

        Chat工作区Grid.ColumnSpacing = isCompact ? 8 : 10;
        ChatSessionColumn.Width = isStacked
            ? new GridLength(1, GridUnitType.Star)
            : isCompact
                ? new GridLength(248)
                : new GridLength(286);
        ChatSessionPanel.Visibility = isVeryCompact ? Visibility.Collapsed : Visibility.Visible;
        ApplyToolbarLayout(isVeryCompact);
        SyncChatSettingsPanelLayout();
    }

    private void ApplyToolbarLayout(bool isVeryCompact)
    {
        Grid.SetRow(ChatToolbarEndpointGrid, isVeryCompact ? 1 : 0);
        Grid.SetColumn(ChatToolbarEndpointGrid, isVeryCompact ? 0 : 1);
        Grid.SetColumnSpan(ChatToolbarEndpointGrid, isVeryCompact ? 5 : 1);
    }

    private void SyncChatSettingsPanelLayout()
    {
        var isOpen = ViewModel.IsChatSettingsPanelOpen;
        ChatSettingsColumn.Width = isOpen ? new GridLength(320) : new GridLength(0);
        ChatSettingsColumn.MinWidth = isOpen ? 280 : 0;
        ChatSettingsPanel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeProxyContext();
        ViewModel.TryApplyTransparentProxyEndpoint(App.TransparentProxyViewModel, overwrite: false);
        ViewModel.LoadPresets();
        ViewModel.LoadSessions();
        SubscribeChatMessages();
        await ViewModel.LoadCachedModelsAsync();
        RefreshProxyContext();
        UpdateStreamingAnimation();
        ScrollChatMessagesToBottom();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeProxyContext();
        UnsubscribeChatMessages();
        StreamingPulseStoryboard.Stop();
        StreamingGlow.Opacity = 0;
    }

    private void OnUseTransparentProxyClick(object sender, RoutedEventArgs e)
    {
        ViewModel.TryApplyTransparentProxyEndpoint(App.TransparentProxyViewModel, overwrite: true);
        RefreshProxyContext();
    }

    private async void ChatInputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shiftState.HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        e.Handled = true;
        if (ViewModel.SendChatMessageCommand.CanExecute(null))
        {
            await ViewModel.SendChatMessageCommand.ExecuteAsync(null);
        }
    }

    private void SubscribeProxyContext()
    {
        if (_proxySubscribed)
        {
            return;
        }

        var proxy = App.TransparentProxyViewModel;
        proxy.PropertyChanged += OnProxyPropertyChanged;
        proxy.Routes.CollectionChanged += OnProxyCollectionChanged;
        proxy.RouteQueue.CollectionChanged += OnProxyCollectionChanged;
        proxy.ModelPool.CollectionChanged += OnProxyCollectionChanged;
        proxy.ProviderAccounts.CollectionChanged += OnProxyCollectionChanged;
        proxy.ModelRewriteRules.CollectionChanged += OnProxyCollectionChanged;
        _proxySubscribed = true;
    }

    private void UnsubscribeProxyContext()
    {
        if (!_proxySubscribed)
        {
            return;
        }

        var proxy = App.TransparentProxyViewModel;
        proxy.PropertyChanged -= OnProxyPropertyChanged;
        proxy.Routes.CollectionChanged -= OnProxyCollectionChanged;
        proxy.RouteQueue.CollectionChanged -= OnProxyCollectionChanged;
        proxy.ModelPool.CollectionChanged -= OnProxyCollectionChanged;
        proxy.ProviderAccounts.CollectionChanged -= OnProxyCollectionChanged;
        proxy.ModelRewriteRules.CollectionChanged -= OnProxyCollectionChanged;
        _proxySubscribed = false;
    }

    private void OnProxyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransparentProxyViewModel.IsRunning)
            or nameof(TransparentProxyViewModel.ListenAddress)
            or nameof(TransparentProxyViewModel.CacheHitRate)
            or nameof(TransparentProxyViewModel.P50Latency)
            or nameof(TransparentProxyViewModel.P95Latency)
            or nameof(TransparentProxyViewModel.ActiveConnections)
            or nameof(TransparentProxyViewModel.ProviderAccountSummary))
        {
            RefreshProxyContext();
        }
    }

    private void OnProxyCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshProxyContext();

    private void RefreshProxyContext()
        => ViewModel.RefreshTransparentProxyContext(App.TransparentProxyViewModel);

    private void SubscribeChatMessages()
    {
        if (_chatMessagesSubscribed)
        {
            return;
        }

        ViewModel.Messages.CollectionChanged += OnChatMessagesChanged;
        _chatMessagesSubscribed = true;
    }

    private void UnsubscribeChatMessages()
    {
        if (!_chatMessagesSubscribed)
        {
            return;
        }

        ViewModel.Messages.CollectionChanged -= OnChatMessagesChanged;
        _chatMessagesSubscribed = false;
    }

    private void OnChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_autoScrollChatMessages)
            {
                ScrollChatMessagesToBottom();
            }

            RefreshBackToBottomButton();
        });
    }

    private void ChatMessagesScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        var isAtBottom = ChatMessagesScrollViewer.ScrollableHeight <= 0 ||
                         ChatMessagesScrollViewer.VerticalOffset >= ChatMessagesScrollViewer.ScrollableHeight - 12;
        _autoScrollChatMessages = isAtBottom;
        RefreshBackToBottomButton();
    }

    private void BackToBottomButton_Click(object sender, RoutedEventArgs e)
    {
        _autoScrollChatMessages = true;
        ScrollChatMessagesToBottom();
        RefreshBackToBottomButton();
    }

    private void ScrollChatMessagesToBottom()
    {
        ChatMessagesScrollViewer.ChangeView(
            horizontalOffset: null,
            verticalOffset: ChatMessagesScrollViewer.ScrollableHeight,
            zoomFactor: null,
            disableAnimation: false);
    }

    private void RefreshBackToBottomButton()
    {
        BackToBottomButton.Visibility =
            !_autoScrollChatMessages && ChatMessagesScrollViewer.ScrollableHeight > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelChatViewModel.IsStreaming))
        {
            UpdateStreamingAnimation();
        }
        else if (e.PropertyName == nameof(ModelChatViewModel.IsChatSettingsPanelOpen))
        {
            SyncChatSettingsPanelLayout();
        }
    }

    private void UpdateStreamingAnimation()
    {
        if (ViewModel.IsStreaming)
        {
            StreamingPulseStoryboard.Begin();
        }
        else
        {
            StreamingPulseStoryboard.Stop();
            StreamingGlow.Opacity = 0;
        }
    }

    private void OnSessionCreated(object? sender, EventArgs e)
    {
        ViewModel.NewChatSessionCommand.Execute(null);
    }

    private void OnSessionSelected(object? sender, ChatSession session)
    {
        ViewModel.SwitchSessionCommand.Execute(session);
    }

    private void OnSessionDeleted(object? sender, ChatSession session)
    {
        ViewModel.DeleteChatSessionCommand.Execute(session);
    }

    private void OnSessionRenamed(object? sender, ChatSession session)
    {
        ViewModel.CommitRenameChatSessionCommand.Execute(session);
    }

    private void OnReasoningEffortLow(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedReasoningEffort = "Low";
    }

    private void OnReasoningEffortMedium(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedReasoningEffort = "Medium";
    }

    private void OnReasoningEffortHigh(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedReasoningEffort = "High";
    }

    // ─── Drag-Drop for Attachments ────────────────────────────────────────

    private void OnInputAreaDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
        e.Handled = true;
    }

    private async void OnInputAreaDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFile file)
            {
                ViewModel.AttachFileCommand.Execute(file.Path);
            }
        }
    }

    // ─── Multi-Model Selection ────────────────────────────────────────────

    private void OnAddCompareModelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        if (sender is not Microsoft.UI.Xaml.Controls.TextBox textBox) return;

        var modelName = textBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(modelName)) return;

        ViewModel.AddChatSelectedModelCommand.Execute(modelName);
        textBox.Text = "";
        e.Handled = true;
    }

}
