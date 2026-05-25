using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RelayBench.Core.AdvancedTesting.Models;
using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a single test suite item that can be enabled/disabled and reordered
/// via drag-and-drop in the test suite prioritization list.
/// </summary>
public sealed partial class TestSuiteItem : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string SuiteId { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial int Order { get; set; }
    [ObservableProperty] public partial AdvancedRiskLevel RiskLevel { get; set; } = AdvancedRiskLevel.Low;

    public ObservableCollection<TestCaseItem> Cases { get; } = new();

    public TestSuiteItem() { }

    public TestSuiteItem(string name, bool isEnabled, int order)
    {
        Name = name;
        SuiteId = name;
        IsEnabled = isEnabled;
        Order = order;
    }

    public TestSuiteItem(AdvancedTestSuiteDefinition definition, bool isEnabled, int order)
    {
        SuiteId = definition.SuiteId;
        Name = definition.DisplayName;
        IsEnabled = isEnabled;
        Order = order;
        RiskLevel = definition.RiskLevel;

        foreach (var testCase in definition.Cases)
        {
            var item = new TestCaseItem(testCase);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TestCaseItem.IsEnabled))
                {
                    OnPropertyChanged(nameof(EnabledCaseCount));
                    OnPropertyChanged(nameof(CaseSummary));
                    OnPropertyChanged(nameof(CompactCaseSummary));
                    OnPropertyChanged(nameof(EnabledRatioText));
                    OnPropertyChanged(nameof(EnabledRatioWidth));
                }
            };
            Cases.Add(item);
        }
    }

    public string DisplayName => Name switch
    {
        _ when SuiteId.Equals("basic", StringComparison.OrdinalIgnoreCase) => "\u57FA\u7840\u517C\u5BB9",
        _ when SuiteId.Equals("agent", StringComparison.OrdinalIgnoreCase) => "Agent \u517C\u5BB9",
        _ when SuiteId.Equals("json", StringComparison.OrdinalIgnoreCase) => "JSON \u7ED3\u6784\u5316",
        _ when SuiteId.Equals("reasoning", StringComparison.OrdinalIgnoreCase) => "\u63A8\u7406\u517C\u5BB9",
        _ when SuiteId.Equals("capacity", StringComparison.OrdinalIgnoreCase) => "\u7A33\u5B9A\u4E0E\u5BB9\u91CF",
        _ when SuiteId.Equals("rag", StringComparison.OrdinalIgnoreCase) => "RAG \u80FD\u529B",
        _ when SuiteId.Equals("model-risk", StringComparison.OrdinalIgnoreCase) => "\u6A21\u578B\u98CE\u9669",
        _ when SuiteId.Equals("security-red-team", StringComparison.OrdinalIgnoreCase) => "\u6570\u636E\u5B89\u5168",
        "Prompt Injection" => "\u6CE8\u5165\u63D0\u793A",
        "Jailbreak" => "越狱绕过",
        "PII Leak" => "PII \u4FE1\u606F\u56DE\u663E",
        "Content Compliance" => "\u7CFB\u7EDF\u63D0\u793A\u6CC4\u6F0F",
        "Role Play" => "工具调用越权",
        "Multi-Language Bypass" => "RAG \u6570\u636E\u6C61\u67D3",
        "Encoding Obfuscation" => "\u6076\u610F URL / \u547D\u4EE4\u8BF1\u5BFC",
        _ => Name
    };

    public string RiskLevelText => RiskLevel switch
    {
        AdvancedRiskLevel.Critical => "严重风险",
        AdvancedRiskLevel.High => "高风险",
        AdvancedRiskLevel.Medium => "中风险",
        _ => "低风险"
    };

    public string RiskLevelShortText => RiskLevel switch
    {
        AdvancedRiskLevel.Critical => "严重",
        AdvancedRiskLevel.High => "高",
        AdvancedRiskLevel.Medium => "中",
        _ => "低"
    };

    public Visibility HighRiskVisibility => RiskLevel is AdvancedRiskLevel.Critical or AdvancedRiskLevel.High
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MediumRiskVisibility => RiskLevel == AdvancedRiskLevel.Medium
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LowRiskVisibility => RiskLevel is not AdvancedRiskLevel.Critical and not AdvancedRiskLevel.High and not AdvancedRiskLevel.Medium
        ? Visibility.Visible
        : Visibility.Collapsed;

    public int TotalCaseCount => Cases.Count;

    public int EnabledCaseCount => Cases.Count == 0
        ? (IsEnabled ? 1 : 0)
        : Cases.Count(static item => item.IsEnabled);

    public string CaseSummary => Cases.Count == 0
        ? "1 个测试项"
        : $"{EnabledCaseCount} / {TotalCaseCount} 个测试项";

    public string CompactCaseSummary => TotalCaseCount == 0
        ? (IsEnabled ? "1/1 项" : "0/1 项")
        : $"{EnabledCaseCount}/{TotalCaseCount} 项";

    public string EnabledRatioText => TotalCaseCount == 0
            ? (IsEnabled ? "已启用" : "已关闭")
        : $"{EnabledCaseCount}/{TotalCaseCount}";

    public double EnabledRatioWidth
    {
        get
        {
            var denominator = Math.Max(1, TotalCaseCount);
            var ratio = TotalCaseCount == 0
                ? IsEnabled ? 1d : 0d
                : (double)EnabledCaseCount / denominator;
            return Math.Clamp(ratio, 0, 1) * 96;
        }
    }

    public string Description => Name switch
    {
        "Prompt Injection" => "\u6CE8\u5165\u6307\u4EE4\u3001\u8D8A\u6743\u548C\u4EE4\u724C\u7A83\u53D6",
        "Jailbreak" => "\u5BF9\u6297\u6027\u7ED5\u8FC7\u4E0E\u89D2\u8272\u8BF1\u5BFC",
        "PII Leak" => "\u9690\u79C1\u6570\u636E\u67E5\u8BE2\u4E0E\u56DE\u663E\u63A7\u5236",
        "Content Compliance" => "\u63A2\u6D4B\u7CFB\u7EDF\u63D0\u793A\u4E0E\u5185\u90E8\u89C4\u5219",
        "Role Play" => "\u5DE5\u5177\u8C03\u7528\u6743\u9650\u4E0E\u53C2\u6570\u6821\u9A8C",
        "Multi-Language Bypass" => "\u68C0\u67E5\u68C0\u7D22\u578B\u6570\u636E\u6C61\u67D3\u4E0E\u6295\u6BD2",
        "Encoding Obfuscation" => "\u5916\u90E8\u94FE\u63A5\u4E0E\u7CFB\u7EDF\u547D\u4EE4\u8BF1\u5BFC",
        _ => string.Empty
    };

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Description));
    }

    partial void OnSuiteIdChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(EnabledCaseCount));
        OnPropertyChanged(nameof(CaseSummary));
        OnPropertyChanged(nameof(CompactCaseSummary));
        OnPropertyChanged(nameof(EnabledRatioText));
        OnPropertyChanged(nameof(EnabledRatioWidth));
    }

    partial void OnRiskLevelChanged(AdvancedRiskLevel value)
    {
        OnPropertyChanged(nameof(RiskLevelText));
        OnPropertyChanged(nameof(RiskLevelShortText));
        OnPropertyChanged(nameof(HighRiskVisibility));
        OnPropertyChanged(nameof(MediumRiskVisibility));
        OnPropertyChanged(nameof(LowRiskVisibility));
    }
}

public sealed partial class TestCaseItem : ObservableObject
{
    [ObservableProperty] public partial string TestId { get; set; } = "";
    [ObservableProperty] public partial string DisplayName { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    public TestCaseItem() { }

    public TestCaseItem(AdvancedTestCaseDefinition definition)
    {
        TestId = definition.TestId;
        DisplayName = definition.DisplayName;
        Description = definition.Description;
        IsEnabled = definition.IsEnabledByDefault;
    }
}
