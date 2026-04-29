using System.Collections.ObjectModel;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isClientApplyTargetDialogOpen;
    private string _clientApplyTargetDialogTitle = "应用到软件";
    private string _clientApplyTargetDialogSummary = string.Empty;
    private TaskCompletionSource<IReadOnlyList<ClientApplyTargetSelection>>? _clientApplyTargetDialogCompletionSource;

    public ObservableCollection<ClientApplyTargetItemViewModel> ClientApplyTargetItems { get; } = [];

    public bool IsClientApplyTargetDialogOpen
    {
        get => _isClientApplyTargetDialogOpen;
        private set => SetProperty(ref _isClientApplyTargetDialogOpen, value);
    }

    public string ClientApplyTargetDialogTitle
    {
        get => _clientApplyTargetDialogTitle;
        private set => SetProperty(ref _clientApplyTargetDialogTitle, value);
    }

    public string ClientApplyTargetDialogSummary
    {
        get => _clientApplyTargetDialogSummary;
        private set => SetProperty(ref _clientApplyTargetDialogSummary, value);
    }

    public bool HasSelectableClientApplyTargets
        => ClientApplyTargetItems.Any(item => item.IsSelectable);

    public bool HasSelectedClientApplyTargets
        => ClientApplyTargetItems.Any(item => item.IsSelectable && item.IsSelected);

    private Task<IReadOnlyList<ClientApplyTargetSelection>> ShowClientApplyTargetDialogAsync(
        string title,
        string summary,
        IReadOnlyList<ClientApplyTarget> targets)
    {
        _clientApplyTargetDialogCompletionSource?.TrySetResult([]);
        ClientApplyTargetItems.Clear();
        foreach (var target in targets)
        {
            var item = new ClientApplyTargetItemViewModel(target);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ClientApplyTargetItemViewModel.IsSelected))
                {
                    RefreshClientApplyTargetDialogState();
                }
            };
            ClientApplyTargetItems.Add(item);
        }

        ClientApplyTargetDialogTitle = string.IsNullOrWhiteSpace(title) ? "应用到软件" : title.Trim();
        ClientApplyTargetDialogSummary = summary?.Trim() ?? string.Empty;
        _clientApplyTargetDialogCompletionSource =
            new TaskCompletionSource<IReadOnlyList<ClientApplyTargetSelection>>(TaskCreationOptions.RunContinuationsAsynchronously);
        RefreshClientApplyTargetDialogState();
        IsClientApplyTargetDialogOpen = true;
        return _clientApplyTargetDialogCompletionSource.Task;
    }

    private Task SelectAllClientApplyTargetsAsync()
    {
        return SelectAllClientApplyTargetsCoreAsync();
    }

    private async Task SelectAllClientApplyTargetsCoreAsync()
    {
        var selectableItems = ClientApplyTargetItems
            .Where(item => item.IsSelectable)
            .ToArray();
        var warningItems = selectableItems
            .Where(item => item.RequiresCompatibilityConfirmation && !item.IsSelected)
            .ToArray();

        if (warningItems.Length > 0 && !await ConfirmClientApplyCompatibilityOverrideAsync(warningItems))
        {
            return;
        }

        foreach (var item in selectableItems)
        {
            item.IsSelected = true;
        }

        RefreshClientApplyTargetDialogState();
    }

    private Task InvertClientApplyTargetsAsync()
    {
        return InvertClientApplyTargetsCoreAsync();
    }

    private async Task InvertClientApplyTargetsCoreAsync()
    {
        var selectableItems = ClientApplyTargetItems
            .Where(item => item.IsSelectable)
            .ToArray();
        var warningItems = selectableItems
            .Where(item => !item.IsSelected && item.RequiresCompatibilityConfirmation)
            .ToArray();

        if (warningItems.Length > 0 && !await ConfirmClientApplyCompatibilityOverrideAsync(warningItems))
        {
            return;
        }

        foreach (var item in selectableItems)
        {
            item.IsSelected = !item.IsSelected;
        }

        RefreshClientApplyTargetDialogState();
    }

    private async Task ToggleClientApplyTargetSelectionAsync(ClientApplyTargetItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsSelected)
        {
            item.IsSelected = false;
            return;
        }

        if (!item.IsSelectable)
        {
            item.IsSelected = false;
            item.RefreshSelectionState();
            return;
        }

        if (item.RequiresCompatibilityConfirmation &&
            !await ConfirmClientApplyCompatibilityOverrideAsync(new[] { item }))
        {
            item.IsSelected = false;
            item.RefreshSelectionState();
            return;
        }

        item.IsSelected = true;
    }

    private Task<bool> ConfirmClientApplyCompatibilityOverrideAsync(
        IReadOnlyCollection<ClientApplyTargetItemViewModel> items)
    {
        var targetLines = string.Join(
            "\n",
            items.Select(item => $"- {item.DisplayName}: {item.DisabledReason}"));
        var message = items.Count == 1
            ? $"\u201c{items.First().DisplayName}\u201d\u4e0e\u5f53\u524d\u6a21\u578b\u6216\u63a5\u53e3\u63a2\u6d4b\u7ed3\u679c\u4e0d\u4e00\u5b9a\u517c\u5bb9\u3002"
            : $"\u5c06\u52fe\u9009 {items.Count} \u4e2a\u4e0e\u5f53\u524d\u6a21\u578b\u6216\u63a5\u53e3\u63a2\u6d4b\u7ed3\u679c\u4e0d\u4e00\u5b9a\u517c\u5bb9\u7684\u8f6f\u4ef6\u3002";

        return ShowConfirmationDialogAsync(
            "\u517c\u5bb9\u6027\u63d0\u793a",
            message,
            "\u7ee7\u7eed\u5e94\u7528\u53ef\u80fd\u5bfc\u81f4\u8be5\u8f6f\u4ef6\u65e0\u6cd5\u6b63\u5e38\u8c03\u7528\u5f53\u524d\u6a21\u578b\u3002\n\n" +
            targetLines +
            "\n\n\u662f\u5426\u4ecd\u7136\u8981\u52fe\u9009\uff1f",
            "\u662f\uff0c\u7ee7\u7eed",
            "\u5426\uff0c\u53d6\u6d88");
    }

    private Task ConfirmClientApplyTargetDialogAsync()
    {
        var selectedTargets = ClientApplyTargetItems
            .Where(item => item.IsSelectable && item.IsSelected)
            .Select(item => new ClientApplyTargetSelection(item.TargetId, item.Protocol))
            .ToArray();

        if (selectedTargets.Length == 0)
        {
            StatusMessage = "请至少选择一个要应用的软件。";
            return Task.CompletedTask;
        }

        IsClientApplyTargetDialogOpen = false;
        _clientApplyTargetDialogCompletionSource?.TrySetResult(selectedTargets);
        _clientApplyTargetDialogCompletionSource = null;
        return Task.CompletedTask;
    }

    private Task CancelClientApplyTargetDialogAsync()
    {
        IsClientApplyTargetDialogOpen = false;
        _clientApplyTargetDialogCompletionSource?.TrySetResult([]);
        _clientApplyTargetDialogCompletionSource = null;
        return Task.CompletedTask;
    }

    private void RefreshClientApplyTargetDialogState()
    {
        OnPropertyChanged(nameof(HasSelectableClientApplyTargets));
        OnPropertyChanged(nameof(HasSelectedClientApplyTargets));
        ConfirmClientApplyTargetDialogCommand?.RaiseCanExecuteChanged();
        SelectAllClientApplyTargetsCommand?.RaiseCanExecuteChanged();
        InvertClientApplyTargetsCommand?.RaiseCanExecuteChanged();
    }

    private bool CanConfirmClientApplyTargetDialog()
        => HasSelectedClientApplyTargets;

    private bool CanEditClientApplyTargetSelection()
        => HasSelectableClientApplyTargets;
}
