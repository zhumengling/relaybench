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
        foreach (var item in ClientApplyTargetItems.Where(item => item.IsSelectable))
        {
            item.IsSelected = true;
        }

        RefreshClientApplyTargetDialogState();
        return Task.CompletedTask;
    }

    private Task InvertClientApplyTargetsAsync()
    {
        foreach (var item in ClientApplyTargetItems.Where(item => item.IsSelectable))
        {
            item.IsSelected = !item.IsSelected;
        }

        RefreshClientApplyTargetDialogState();
        return Task.CompletedTask;
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
