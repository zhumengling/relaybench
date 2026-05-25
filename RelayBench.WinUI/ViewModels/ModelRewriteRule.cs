using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a model rewrite rule: source model name mapped to target model name.
/// </summary>
public sealed partial class ModelRewriteRule : ObservableObject
{
    [ObservableProperty] public partial string SourceModel { get; set; }
    [ObservableProperty] public partial string TargetModel { get; set; }

    public ModelRewriteRule(string sourceModel, string targetModel)
    {
        SourceModel = sourceModel;
        TargetModel = targetModel;
    }
}
