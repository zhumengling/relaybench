using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Status of a scenario in the execution timeline.
/// </summary>
public enum ScenarioStatus
{
    Pending,
    InProgress,
    Passed,
    Failed
}

/// <summary>
/// Represents a single scenario item in the execution timeline.
/// Shows status via icon: green check (pass), red X (fail), ProgressRing (in-progress), gray circle (pending).
/// </summary>
public sealed partial class ScenarioTimelineItem : ObservableObject
{
    [ObservableProperty] public partial ScenarioStatus Status { get; set; } = ScenarioStatus.Pending;
    [ObservableProperty] public partial int Index { get; set; }

    public ScenarioTimelineItem() { }

    public ScenarioTimelineItem(int index, ScenarioStatus status)
    {
        Index = index;
        Status = status;
    }

    /// <summary>Icon glyph for the current status.</summary>
    public string StatusGlyph => Status switch
    {
        ScenarioStatus.Passed => "\uE73E",   // Checkmark
        ScenarioStatus.Failed => "\uE711",   // Cancel/X
        ScenarioStatus.Pending => "\uEA3A",  // Circle outline
        _ => ""                               // InProgress uses ProgressRing instead
    };

    public Visibility PassedVisibility => Status == ScenarioStatus.Passed ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FailedVisibility => Status == ScenarioStatus.Failed ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PendingVisibility => Status == ScenarioStatus.Pending ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether this item is currently in-progress (shows ProgressRing instead of icon).</summary>
    public bool IsInProgress => Status == ScenarioStatus.InProgress;

    /// <summary>Whether this item shows a static icon (not in-progress).</summary>
    public bool IsNotInProgress => Status != ScenarioStatus.InProgress;

    partial void OnStatusChanged(ScenarioStatus value)
    {
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(PassedVisibility));
        OnPropertyChanged(nameof(FailedVisibility));
        OnPropertyChanged(nameof(PendingVisibility));
        OnPropertyChanged(nameof(IsInProgress));
        OnPropertyChanged(nameof(IsNotInProgress));
    }
}
