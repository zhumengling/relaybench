using RelayBench.App.Infrastructure;
using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.App.ViewModels.AdvancedTesting;

public sealed class AdvancedTestSuiteViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isActive;

    public AdvancedTestSuiteViewModel(AdvancedTestSuiteDefinition definition)
    {
        Definition = definition;
        _isSelected = definition.SuiteId is "basic" or "agent" or "json";
    }

    public AdvancedTestSuiteDefinition Definition { get; }

    public string SuiteId => Definition.SuiteId;

    public string DisplayName => Definition.DisplayName;

    public string Description => Definition.Description;

    public string RiskText
        => Definition.RiskLevel switch
        {
            AdvancedRiskLevel.Critical => "严重",
            AdvancedRiskLevel.High => "高风险",
            AdvancedRiskLevel.Medium => "中风险",
            _ => "低风险"
        };

    public string RiskBrush
        => Definition.RiskLevel switch
        {
            AdvancedRiskLevel.Critical => "#7F1D1D",
            AdvancedRiskLevel.High => "#DC2626",
            AdvancedRiskLevel.Medium => "#D97706",
            _ => "#059669"
        };

    public int CaseCount => Definition.Cases.Count;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
