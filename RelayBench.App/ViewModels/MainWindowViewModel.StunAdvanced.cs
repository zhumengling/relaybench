namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _stunTestSummary = "尚无 NAT 分类测试过程。";
    private string _stunCoverageSummary = "运行 STUN 检测后，这里会解释当前归类覆盖度、可信度与复核建议。";

    public string StunTestSummary
    {
        get => _stunTestSummary;
        private set => SetProperty(ref _stunTestSummary, value);
    }

    public string StunCoverageSummary
    {
        get => _stunCoverageSummary;
        private set => SetProperty(ref _stunCoverageSummary, value);
    }
}
