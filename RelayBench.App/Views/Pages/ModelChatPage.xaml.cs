using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RelayBench.App.ViewModels;

namespace RelayBench.App.Views.Pages;

public partial class ModelChatPage : UserControl
{
    private readonly Brush _dropZoneHighlightBrush = new SolidColorBrush(Color.FromRgb(239, 246, 255));
    private bool _autoScrollChatMessages = true;
    private bool _isAutoScrolling;
    private MainWindowViewModel? _viewModel;

    public ModelChatPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as MainWindowViewModel);
        ScheduleScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        AttachViewModel(e.NewValue as MainWindowViewModel);
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        _viewModel = viewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ChatMessages.CollectionChanged += OnChatMessagesChanged;
        foreach (var message in _viewModel.ChatMessages)
        {
            AttachMessage(message);
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ChatMessages.CollectionChanged -= OnChatMessagesChanged;
        foreach (var message in _viewModel.ChatMessages)
        {
            DetachMessage(message);
        }

        _viewModel = null;
    }

    private void OnChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ChatMessageViewModel message in e.OldItems)
            {
                DetachMessage(message);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ChatMessageViewModel message in e.NewItems)
            {
                AttachMessage(message);
            }
        }

        ScheduleScrollToEnd();
    }

    private void AttachMessage(ChatMessageViewModel message)
    {
        message.PropertyChanged += OnChatMessagePropertyChanged;
        foreach (var answer in message.ModelAnswers)
        {
            answer.PropertyChanged += OnChatModelAnswerPropertyChanged;
        }
    }

    private void DetachMessage(ChatMessageViewModel message)
    {
        message.PropertyChanged -= OnChatMessagePropertyChanged;
        foreach (var answer in message.ModelAnswers)
        {
            answer.PropertyChanged -= OnChatModelAnswerPropertyChanged;
        }
    }

    private void OnChatMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessageViewModel.Content) or nameof(ChatMessageViewModel.Error) or nameof(ChatMessageViewModel.MetricsSummary))
        {
            ScheduleScrollToEnd();
        }
    }

    private void OnChatModelAnswerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatModelAnswerViewModel.Content) or nameof(ChatModelAnswerViewModel.Error) or nameof(ChatModelAnswerViewModel.StatusText))
        {
            ScheduleScrollToEnd();
        }
    }

    private void ScheduleScrollToEnd()
    {
        if (!_autoScrollChatMessages)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                _isAutoScrolling = true;
                ChatMessagesScrollViewer.ScrollToEnd();
                _isAutoScrolling = false;
                BackToBottomButton.Visibility = Visibility.Collapsed;
            },
            DispatcherPriority.Background);
    }

    private void ChatMessagesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isAutoScrolling)
        {
            return;
        }

        if (e.ExtentHeightChange > 0)
        {
            ScheduleScrollToEnd();
            return;
        }

        var distanceToBottom = ChatMessagesScrollViewer.ScrollableHeight - ChatMessagesScrollViewer.VerticalOffset;
        _autoScrollChatMessages = distanceToBottom < 28;
        BackToBottomButton.Visibility = _autoScrollChatMessages ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BackToBottomButton_Click(object sender, RoutedEventArgs e)
    {
        _autoScrollChatMessages = true;
        ScheduleScrollToEnd();
    }

    private void ModelChatPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Enter &&
            ChatInputTextBox.IsKeyboardFocusWithin &&
            Keyboard.Modifiers != ModifierKeys.Shift)
        {
            ExecuteIfPossible(viewModel.SendChatMessageCommand);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            ExecuteIfPossible(viewModel.SendChatMessageCommand);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (viewModel.IsChatSettingsPanelOpen)
            {
                ExecuteIfPossible(viewModel.CloseChatSettingsPanelCommand);
                e.Handled = true;
                return;
            }

            if (viewModel.IsEditingChatMessage)
            {
                ExecuteIfPossible(viewModel.CancelChatEditCommand);
                e.Handled = true;
                return;
            }

            if (viewModel.CanStopChatStreamingNow)
            {
                ExecuteIfPossible(viewModel.StopChatStreamingCommand);
                e.Handled = true;
            }
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            ExecuteIfPossible(viewModel.NewChatSessionCommand);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.E)
        {
            ExecuteIfPossible(viewModel.ExportChatSessionMarkdownCommand);
            e.Handled = true;
        }
    }

    private void ChatSessionRenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox ||
            DataContext is not MainWindowViewModel viewModel ||
            textBox.DataContext is not ChatSessionListItemViewModel item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            ExecuteIfPossible(viewModel.CommitRenameChatSessionCommand, item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ExecuteIfPossible(viewModel.CancelRenameChatSessionCommand, item);
            e.Handled = true;
        }
    }

    private void ChatInputDropZone_DragEnter(object sender, DragEventArgs e)
        => UpdateDropState(e, isOver: true);

    private void ChatInputDropZone_DragOver(object sender, DragEventArgs e)
        => UpdateDropState(e, isOver: true);

    private void ChatInputDropZone_DragLeave(object sender, DragEventArgs e)
        => ResetDropState();

    private void ChatInputDropZone_Drop(object sender, DragEventArgs e)
    {
        ResetDropState();
        if (!TryGetDroppedFiles(e, out var files) ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        ExecuteIfPossible(viewModel.AddChatAttachmentFilesCommand, files);
        e.Handled = true;
    }

    private void UpdateDropState(DragEventArgs e, bool isOver)
    {
        if (!TryGetDroppedFiles(e, out _))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        if (isOver)
        {
            ChatInputDropZone.Background = _dropZoneHighlightBrush;
        }

        e.Handled = true;
    }

    private void ResetDropState()
    {
        ChatInputDropZone.ClearValue(Border.BackgroundProperty);
    }

    private static bool TryGetDroppedFiles(DragEventArgs e, out string[] files)
    {
        files = [];
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        return files.Length > 0;
    }

    private static void ExecuteIfPossible(ICommand command, object? parameter = null)
    {
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
