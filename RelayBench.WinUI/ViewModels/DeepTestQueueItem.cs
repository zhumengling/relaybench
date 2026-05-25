using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Status of a batch test queue item.
/// </summary>
public enum DeepTestStatus
{
    Pending,
    Active,
    Completed
}

/// <summary>
/// Batch comparison run mode.
/// </summary>
public enum BatchRunMode
{
    Quick,
    Deep
}

/// <summary>
/// Represents a single item in the batch test queue panel.
/// </summary>
public sealed partial class DeepTestQueueItem : ObservableObject
{
    [ObservableProperty] public partial string SiteName { get; set; } = string.Empty;
    [ObservableProperty] public partial DeepTestStatus Status { get; set; } = DeepTestStatus.Pending;
    [ObservableProperty] public partial int PassCount { get; set; }
    [ObservableProperty] public partial int FailCount { get; set; }
    [ObservableProperty] public partial string CurrentStage { get; set; } = "等待";
    [ObservableProperty] public partial string RoundText { get; set; } = "0/0";
    [ObservableProperty] public partial string LatestResult { get; set; } = "0";
    [ObservableProperty] public partial string ScoreText { get; set; } = "0";
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial double ProgressMaximum { get; set; } = 1;
    [ObservableProperty] public partial string StartedAtText { get; set; } = "--";
    [ObservableProperty] public partial string ElapsedText { get; set; } = "--";

    public ObservableCollection<DeepTestBadgeItem> TestBadges { get; } = new();
    public ObservableCollection<BatchRunTimelineItem> TimelineItems { get; } = new();

    public DeepTestQueueItem() { }

    public DeepTestQueueItem(string siteName, DeepTestStatus status, int passCount, int failCount)
    {
        SiteName = siteName;
        Status = status;
        PassCount = passCount;
        FailCount = failCount;
        RoundText = passCount + failCount > 0 ? $"{passCount + failCount}" : "0/0";
    }

    public string StatusText => Status switch
    {
        DeepTestStatus.Pending => "等待中",
        DeepTestStatus.Active => "评测中",
        DeepTestStatus.Completed => "已完成",
        _ => "未知"
    };

    public Visibility PendingStatusVisibility => Status == DeepTestStatus.Pending ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActiveStatusVisibility => Status == DeepTestStatus.Active ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompletedStatusVisibility => Status == DeepTestStatus.Completed ? Visibility.Visible : Visibility.Collapsed;

    partial void OnStatusChanged(DeepTestStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PendingStatusVisibility));
        OnPropertyChanged(nameof(ActiveStatusVisibility));
        OnPropertyChanged(nameof(CompletedStatusVisibility));
    }
}

public sealed partial class DeepTestBadgeItem : ObservableObject
{
    [ObservableProperty] public partial string Label { get; set; } = string.Empty;
    [ObservableProperty] public partial string Value { get; set; } = "--";
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial string DetailText { get; set; } = string.Empty;
    [ObservableProperty] public partial string Tooltip { get; set; } = string.Empty;
    [ObservableProperty] public partial string Tone { get; set; } = "Pending";

    public DeepTestBadgeItem() { }

    public DeepTestBadgeItem(
        string label,
        string value,
        string tooltip,
        string tone = "Pending",
        string? title = null,
        string? description = null,
        string? detailText = null)
    {
        Label = label;
        Value = value;
        Tooltip = tooltip;
        Tone = tone;
        Title = title ?? label;
        Description = description ?? tooltip;
        DetailText = detailText ?? string.Empty;
    }

    public Visibility PassToneVisibility => IsTone("Pass") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarnToneVisibility => IsTone("Warn") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FailToneVisibility => IsTone("Fail") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OffToneVisibility => IsTone("Off") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningToneVisibility => IsTone("Running") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingToneVisibility => IsKnownTone() ? Visibility.Collapsed : Visibility.Visible;

    partial void OnToneChanged(string value)
    {
        OnPropertyChanged(nameof(PassToneVisibility));
        OnPropertyChanged(nameof(WarnToneVisibility));
        OnPropertyChanged(nameof(FailToneVisibility));
        OnPropertyChanged(nameof(OffToneVisibility));
        OnPropertyChanged(nameof(RunningToneVisibility));
        OnPropertyChanged(nameof(PendingToneVisibility));
    }

    private bool IsKnownTone()
        => IsTone("Pass") || IsTone("Warn") || IsTone("Fail") || IsTone("Off") || IsTone("Running");

    private bool IsTone(string tone)
        => string.Equals(Tone, tone, StringComparison.Ordinal);
}
