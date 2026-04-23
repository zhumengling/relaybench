using System.Text;
using System.Windows.Media.Imaging;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ProxyTrendStore _proxyTrendStore = new();
    private readonly ProxyTrendChartRenderService _proxyTrendChartRenderService = new();
    private readonly ProxyBatchComparisonChartRenderService _proxyBatchComparisonChartRenderService = new();
    private readonly ProxyBatchDeepComparisonChartRenderService _proxyBatchDeepComparisonChartRenderService = new();
    private readonly ProxyConcurrencyChartRenderService _proxyConcurrencyChartRenderService = new();
    private readonly ProxySingleCapabilityChartRenderService _proxySingleCapabilityChartRenderService = new();
    private string _proxyTrendSummary = "填写接口地址后，这里会显示同一接口的历史趋势。";
    private string _proxyTrendDetail = "尚无接口趋势记录。";
    private string _proxyTrendChartStatusSummary = "完成基础/深度单次诊断、稳定性巡检或入口组检测后，这里会显示对应图表。";
    private BitmapSource? _proxyTrendChartImage;
    private string _proxyChartDialogTitle = "接口稳定性图表";
    private string _proxyChartDialogIntro = "弹窗会显示同一个 URL 的稳定性、普通延迟和 TTFT 曲线；诊断完成后会自动弹出，也可以点“查看稳定性图表”重新打开。";
    private string _proxyChartDialogSummary = "完成基础/深度单次诊断、稳定性巡检或入口组检测后，这里会显示对应图表摘要。";
    private string _proxyChartDialogCapabilitySummary = "完成基础或深度单次诊断后，这里会按基础能力、增强测试、深度测试分区显示检测摘要。";
    private string _proxyChartDialogCapabilityDetail = "完成基础或深度单次诊断后，这里会显示各检测项的状态码、主指标与摘要说明。";
    private string _proxyChartDialogGuideSummary = "蓝线：稳定性，越高越好。\n橙线：普通延迟，越低越好。\n绿线：TTFT，越低越好，适合看首字响应是否抖动。";
    private string _proxyChartDialogStatusSummary = "完成基础/深度单次诊断、稳定性巡检或入口组检测后，这里会显示对应图表。";
    private string _proxyChartDialogEmptyStateText = "当前没有可展示的图表。";
    private BitmapSource? _proxyChartDialogImage;

    public string ProxyTrendSummary
    {
        get => _proxyTrendSummary;
        private set => SetProperty(ref _proxyTrendSummary, value);
    }

    public string ProxyTrendDetail
    {
        get => _proxyTrendDetail;
        private set => SetProperty(ref _proxyTrendDetail, value);
    }

    public BitmapSource? ProxyTrendChartImage
    {
        get => _proxyTrendChartImage;
        private set
        {
            if (SetProperty(ref _proxyTrendChartImage, value))
            {
                OnPropertyChanged(nameof(HasProxyTrendChart));
            }
        }
    }

    public string ProxyChartDialogTitle
    {
        get => _proxyChartDialogTitle;
        private set => SetProperty(ref _proxyChartDialogTitle, value);
    }

    public string ProxyChartDialogIntro
    {
        get => _proxyChartDialogIntro;
        private set => SetProperty(ref _proxyChartDialogIntro, value);
    }

    public string ProxyChartDialogSummary
    {
        get => _proxyChartDialogSummary;
        private set => SetProperty(ref _proxyChartDialogSummary, value);
    }

    public string ProxyChartDialogCapabilitySummary
    {
        get => _proxyChartDialogCapabilitySummary;
        private set => SetProperty(ref _proxyChartDialogCapabilitySummary, value);
    }

    public string ProxyChartDialogCapabilityDetail
    {
        get => _proxyChartDialogCapabilityDetail;
        private set => SetProperty(ref _proxyChartDialogCapabilityDetail, value);
    }

    public string ProxyChartDialogGuideSummary
    {
        get => _proxyChartDialogGuideSummary;
        private set => SetProperty(ref _proxyChartDialogGuideSummary, value);
    }

    public string ProxyChartDialogStatusSummary
    {
        get => _proxyChartDialogStatusSummary;
        private set => SetProperty(ref _proxyChartDialogStatusSummary, value);
    }

    public string ProxyChartDialogEmptyStateText
    {
        get => _proxyChartDialogEmptyStateText;
        private set => SetProperty(ref _proxyChartDialogEmptyStateText, value);
    }

    public BitmapSource? ProxyChartDialogImage
    {
        get => _proxyChartDialogImage;
        private set
        {
            if (SetProperty(ref _proxyChartDialogImage, value))
            {
                OnPropertyChanged(nameof(HasProxyChartDialogImage));
            }
        }
    }

    public bool HasProxyChartDialogImage
        => ProxyChartDialogImage is not null;

    public bool HasProxyTrendChart
        => ProxyTrendChartImage is not null;

    public string ProxyTrendChartStatusSummary
    {
        get => _proxyTrendChartStatusSummary;
        private set => SetProperty(ref _proxyTrendChartStatusSummary, value);
    }
}
