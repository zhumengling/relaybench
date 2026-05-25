using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public sealed record BatchTopCandidateApplicationCandidate(
    int Rank,
    string SiteName,
    string BaseUrl,
    string ApiKey,
    string Model,
    IReadOnlyList<string> Models,
    string ScoreText,
    string ProtocolSummary,
    string DetailText);

public sealed partial class BatchApplicationTargetChoice : ObservableObject
{
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public BatchApplicationTargetChoice(
        string targetId,
        string name,
        string protocolText,
        string detailText,
        bool isSelected)
    {
        TargetId = targetId;
        Name = name;
        ProtocolText = protocolText;
        DetailText = detailText;
        IsSelected = isSelected;
    }

    public string TargetId { get; }
    public string Name { get; }
    public string ProtocolText { get; }
    public string DetailText { get; }
}

public sealed partial class BatchTopCandidateApplyDialogViewModel : ObservableObject
{
    public ApplicationCenterViewModel ApplicationAccess { get; }

    public ObservableCollection<BatchTopCandidateApplicationCandidate> Candidates { get; } = [];

    public ObservableCollection<BatchApplicationTargetChoice> Targets { get; } =
    [
        new("codex", "Codex", "Responses", "仅在 /v1/responses 探测通过后写入", true),
        new("claude-cli", "Claude CLI", "Anthropic / Chat", "Anthropic 直连；仅支持 Chat 时通过本地转换入口", true)
    ];

    [ObservableProperty] public partial BatchTopCandidateApplicationCandidate? SelectedCandidate { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "选择一个 Top 候选后写入 Codex 或 Claude。";

    [ObservableProperty] public partial bool IsApplying { get; set; }

    public bool CodexHistoryMergeRequested { get; private set; }

    public BatchTopCandidateApplyDialogViewModel(
        IEnumerable<BatchTopCandidateApplicationCandidate> candidates)
    {
        ApplicationAccess = new ApplicationCenterViewModel(
            () => null,
            _ => Task.FromResult((IReadOnlyList<EndpointHistoryItem>)Array.Empty<EndpointHistoryItem>()));
        ApplicationAccess.CodexHistoryMergeReviewRequested += (_, _) => CodexHistoryMergeRequested = true;
        ApplicationAccess.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ApplicationCenterViewModel.StatusText))
            {
                StatusText = ApplicationAccess.StatusText;
            }
        };

        foreach (var candidate in candidates)
        {
            Candidates.Add(candidate);
        }

        SelectedCandidate = Candidates.FirstOrDefault();
        ApplySelectedCandidateToAccess();
    }

    public void ConfigureClaudeRelayEndpointResolver(
        Func<ProxyEndpointSettings, ProxyEndpointProtocolProbeResult, string, Task<ClientApplyEndpoint?>> resolver)
        => ApplicationAccess.ConfigureClaudeRelayEndpointResolver(resolver);

    public async Task<bool> ApplyAsync()
    {
        if (SelectedCandidate is null)
        {
            StatusText = "没有可接入的 Top 候选。";
            return false;
        }

        var selectedTargetIds = Targets
            .Where(static target => target.IsSelected)
            .Select(static target => target.TargetId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedTargetIds.Count == 0)
        {
            StatusText = "请至少选择 Codex 或 Claude。";
            return false;
        }

        IsApplying = true;
        try
        {
            ApplySelectedCandidateToAccess();
            StatusText = $"正在探测 {SelectedCandidate.SiteName} 的协议...";
            await ApplicationAccess.ProbeProtocolCommand.ExecuteAsync(null);

            foreach (var target in ApplicationAccess.Targets)
            {
                target.IsSelected = selectedTargetIds.Contains(target.TargetId) && target.IsSelectable;
            }

            var selectedWritable = ApplicationAccess.Targets
                .Where(static target => target.IsSelected && target.IsSelectable)
                .ToList();
            if (selectedWritable.Count == 0)
            {
                var reasons = ApplicationAccess.Targets
                    .Where(target => selectedTargetIds.Contains(target.TargetId))
                    .Select(static target => $"{target.Name}: {target.DisabledReason ?? target.SelectabilityText}")
                    .DefaultIfEmpty("协议探测后没有可写入目标");
                StatusText = string.Join(Environment.NewLine, reasons);
                return false;
            }

            await ApplicationAccess.ConfirmClientApplyTargetDialogCommand.ExecuteAsync(null);
            StatusText = ApplicationAccess.StatusText;
            return true;
        }
        finally
        {
            IsApplying = false;
        }
    }

    partial void OnSelectedCandidateChanged(BatchTopCandidateApplicationCandidate? value)
        => ApplySelectedCandidateToAccess();

    private void ApplySelectedCandidateToAccess()
    {
        if (SelectedCandidate is not { } candidate)
        {
            return;
        }

        ApplicationAccess.ApplyExternalEndpoint(
            candidate.BaseUrl,
            candidate.ApiKey,
            candidate.Model,
            candidate.Models,
            $"批量 Top {candidate.Rank}：{candidate.SiteName}");
        StatusText = $"已选择 Top {candidate.Rank}：{candidate.SiteName}";
    }
}
