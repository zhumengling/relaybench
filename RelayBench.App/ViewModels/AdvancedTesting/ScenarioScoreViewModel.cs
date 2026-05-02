using RelayBench.App.Infrastructure;

namespace RelayBench.App.ViewModels.AdvancedTesting;

public sealed class ScenarioScoreViewModel : ObservableObject
{
    private double _score;
    private string _detail = "尚未运行";

    public ScenarioScoreViewModel(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }

    public string Description { get; }

    public double Score
    {
        get => _score;
        set
        {
            if (SetProperty(ref _score, Math.Round(value, 1)))
            {
                OnPropertyChanged(nameof(ScoreText));
                OnPropertyChanged(nameof(ScoreBrush));
            }
        }
    }

    public string ScoreText => Score <= 0 ? "--" : Score.ToString("0.0");

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public string ScoreBrush
        => Score switch
        {
            >= 85 => "#059669",
            >= 70 => "#2563EB",
            >= 50 => "#D97706",
            > 0 => "#DC2626",
            _ => "#64748B"
        };
}
