using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using RelayBench.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class ApiTraceDialog : ContentDialog
{
    public ApiTraceDialog(NetworkReviewViewModel viewModel)
    {
        TitleText = "API 追踪";
        SubtitleText = "汇总 CDN 追踪、公网出口、地域判定与原始响应证据";
        PublicIpText = EmptyToDash(viewModel.ApiTracePublicIp);
        ColoText = EmptyToDash(viewModel.ApiTraceColo);
        LocationText = BuildLocationText(viewModel.ApiTraceLocationCode, viewModel.ApiTraceLocationName);
        SupportText = EmptyToDash(viewModel.ApiTraceSupportSummary);
        SnapshotText = EmptyToDash(viewModel.SnapshotTimeText);
        SummaryText = BuildSummaryText(viewModel);
        TraceContent = string.IsNullOrWhiteSpace(viewModel.ApiTraceRawTrace)
            ? "尚未捕获原始追踪。请先运行 API 追踪，再重新打开此详情视图。"
            : viewModel.ApiTraceRawTrace;
        UnlockRows = viewModel.UnlockCapabilityRows.ToArray();
        SupportedRegionVisibility = viewModel.ApiTraceIsSupportedRegion ? Visibility.Visible : Visibility.Collapsed;
        UnsupportedRegionVisibility = viewModel.ApiTraceIsSupportedRegion ? Visibility.Collapsed : Visibility.Visible;
        CopyContent = BuildCopyContent();
        InitializeComponent();
    }

    public string TitleText { get; }

    public string SubtitleText { get; }

    public string PublicIpText { get; }

    public string ColoText { get; }

    public string LocationText { get; }

    public string SupportText { get; }

    public string SnapshotText { get; }

    public string SummaryText { get; }

    public string TraceContent { get; }

    public IReadOnlyList<NetworkReviewUnlockRow> UnlockRows { get; }

    public Visibility SupportedRegionVisibility { get; }

    public Visibility UnsupportedRegionVisibility { get; }

    private string CopyContent { get; }

    private void OnCopyTraceClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(CopyContent);
        Clipboard.SetContent(package);
    }

    private string BuildCopyContent()
    {
        var unlockText = UnlockRows.Count == 0
            ? "尚未捕获解锁能力结果。"
            : string.Join(Environment.NewLine, UnlockRows.Select(row => $"- {row.Name}: {row.Status} ({row.Latency})"));
        return
            $"{TitleText}\n" +
            $"公网 IP：{PublicIpText}\n" +
            $"Cloudflare 节点：{ColoText}\n" +
            $"地域：{LocationText}\n" +
            $"支持状态：{SupportText}\n" +
            $"快照：{SnapshotText}\n\n" +
            $"{SummaryText}\n\n" +
            "解锁能力快照：\n" +
            $"{unlockText}\n\n" +
            "原始追踪:\n" +
            TraceContent;
    }

    private static string BuildSummaryText(NetworkReviewViewModel viewModel)
        => $"支持判定：{EmptyToDash(viewModel.ApiTraceSupportSummary)}\n" +
           $"出口画像：{EmptyToDash(viewModel.PublicIpOrganization)} / {EmptyToDash(viewModel.PublicIpAsn)}\n" +
           $"IP 类型：{EmptyToDash(viewModel.PublicIpType)}\n" +
           $"反向 DNS：{EmptyToDash(viewModel.PublicIpDns)}";

    private static string BuildLocationText(string code, string name)
    {
        var normalizedCode = EmptyToDash(code);
        var normalizedName = EmptyToDash(name);
        return normalizedName == "--" ? normalizedCode : $"{normalizedCode} / {normalizedName}";
    }

    private static string EmptyToDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
}
