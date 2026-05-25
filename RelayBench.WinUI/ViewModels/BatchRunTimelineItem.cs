using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class BatchRunTimelineItem : ObservableObject
{
    [ObservableProperty] public partial string TimeText { get; set; } = "--";
    [ObservableProperty] public partial string SiteName { get; set; } = "--";
    [ObservableProperty] public partial string Stage { get; set; } = "--";
    [ObservableProperty] public partial string Summary { get; set; } = "--";
    [ObservableProperty] public partial string Tone { get; set; } = "Info";

    public BatchRunTimelineItem() { }

    public BatchRunTimelineItem(string siteName, string stage, string summary, string tone = "Info")
    {
        TimeText = DateTimeOffset.Now.ToString("HH:mm:ss");
        SiteName = string.IsNullOrWhiteSpace(siteName) ? "--" : siteName;
        Stage = string.IsNullOrWhiteSpace(stage) ? "--" : stage;
        Summary = string.IsNullOrWhiteSpace(summary) ? "--" : summary;
        Tone = string.IsNullOrWhiteSpace(tone) ? "Info" : tone;
    }

    public Visibility PassToneVisibility => IsTone("Pass") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarnToneVisibility => IsTone("Warn") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FailToneVisibility => IsTone("Fail") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningToneVisibility => IsTone("Running") ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InfoToneVisibility => IsKnownTone() ? Visibility.Collapsed : Visibility.Visible;

    partial void OnToneChanged(string value)
    {
        OnPropertyChanged(nameof(PassToneVisibility));
        OnPropertyChanged(nameof(WarnToneVisibility));
        OnPropertyChanged(nameof(FailToneVisibility));
        OnPropertyChanged(nameof(RunningToneVisibility));
        OnPropertyChanged(nameof(InfoToneVisibility));
    }

    private bool IsKnownTone()
        => IsTone("Pass") || IsTone("Warn") || IsTone("Fail") || IsTone("Running");

    private bool IsTone(string tone)
        => string.Equals(Tone, tone, StringComparison.Ordinal);
}
