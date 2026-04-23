using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;
using NetTest.Core.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ClientApiDiagnosticsService _clientApiDiagnosticsService = new();
    private readonly ClientApiConfigRestoreService _clientApiConfigRestoreService = new();
    private ClientApiDiagnosticsResult? _lastClientApiDiagnosticsResult;
    private string _clientApiSummary = "运行后显示已发现应用和接入状态。";
    private string _clientApiDetail = "尚未扫描应用接入。";

    public ObservableCollection<OfficialApiStatusRowViewModel> ClientApiStatusRows { get; } = [];

    public string ClientApiSummary
    {
        get => _clientApiSummary;
        private set => SetProperty(ref _clientApiSummary, value);
    }

    public string ClientApiDetail
    {
        get => _clientApiDetail;
        private set => SetProperty(ref _clientApiDetail, value);
    }

    public bool HasClientApiStatusRows => ClientApiStatusRows.Count > 0;

    private void ApplyClientApiResult(ClientApiDiagnosticsResult result)
    {
        _lastClientApiDiagnosticsResult = result;

        ClientApiSummary =
            $"检测时间：{result.CheckedAt:yyyy-MM-dd HH:mm:ss}\n" +
            $"目标数：{result.Checks.Count}\n" +
            $"已发现安装：{result.InstalledCount}/{result.Checks.Count}\n" +
            $"发现配置：{result.ConfiguredCount}/{result.Checks.Count}\n" +
            $"底层 API 可达：{result.ReachableCount}/{result.Checks.Count}\n" +
            $"摘要：{result.Summary}\n" +
            $"错误：{result.Error ?? "无"}";

        StringBuilder builder = new();
        foreach (var check in result.Checks
                     .OrderBy(check => check.Provider, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(check => check.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"{check.Name} [{check.Provider}]");
            builder.AppendLine($"类型：{check.Kind}");
            builder.AppendLine($"探测地址：{check.ProbeUrl}");
            builder.AppendLine($"探测方法：{check.ProbeMethod}");
            builder.AppendLine($"已安装：{(check.Installed ? "是" : "否")}");
            builder.AppendLine($"已发现配置：{(check.ConfigDetected ? "是" : "否")}");
            builder.AppendLine($"安装线索：{check.InstallEvidence}");
            builder.AppendLine($"配置来源：{check.ConfigSource}");
            builder.AppendLine($"代理来源：{check.ProxySource}");
            builder.AppendLine($"接入方式：{check.AccessPathLabel}");
            builder.AppendLine($"注入方式：{check.ConfigOriginLabel}");
            builder.AppendLine($"当前入口：{check.EndpointLabel}");
            builder.AppendLine($"备注：{check.RoutingNote}");
            builder.AppendLine($"支持还原：{(check.RestoreSupported ? "是" : "否")}");
            builder.AppendLine($"还原说明：{check.RestoreHint}");
            builder.AppendLine($"API 可达：{(check.Reachable ? "是" : "否")}");
            builder.AppendLine($"状态码：{check.StatusCode?.ToString() ?? "--"}");
            builder.AppendLine($"延迟：{FormatMilliseconds(check.Latency)}");
            builder.AppendLine($"结论：{check.Verdict}");
            builder.AppendLine($"摘要：{check.Summary}");
            builder.AppendLine($"证据：{check.Evidence ?? "无"}");
            builder.AppendLine($"错误：{check.Error ?? "无"}");
            builder.AppendLine();
        }

        ClientApiDetail = builder.Length == 0
            ? "尚未扫描应用接入。"
            : builder.ToString().TrimEnd();

        RefreshClientApiStatusRows(result);
        AppendModuleOutput("客户端 API 联通鉴定返回", ClientApiSummary, ClientApiDetail);
    }

    private void RefreshClientApiStatusRows(ClientApiDiagnosticsResult result)
    {
        ClientApiStatusRows.Clear();

        foreach (var check in result.Checks
                     .OrderBy(check => check.Provider, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(check => check.Name, StringComparer.OrdinalIgnoreCase))
        {
            ClientApiStatusRows.Add(BuildClientApiStatusRow(check));
        }

        OnPropertyChanged(nameof(HasClientApiStatusRows));
    }

    private OfficialApiStatusRowViewModel BuildClientApiStatusRow(ClientApiCheck check)
    {
        var availabilityText = BuildClientApiAvailabilityText(check);
        var endpointMetaText =
            $"{check.Kind} · {check.ConfigOriginLabel} · HTTP {check.StatusCode?.ToString() ?? "--"} · {FormatMilliseconds(check.Latency)}";
        var traceTitle = $"{check.Name} / 客户端 Trace";
        var traceContent = BuildClientApiTraceContent(check);
        var (statusBackground, statusForeground) = ResolveClientApiStatusColors(check);
        var summary =
            $"{check.ConfigOriginLabel} · {check.RoutingNote} · 当前入口 {check.EndpointLabel} · {(check.Reachable ? "API 可达" : "API 不通")}";

        return new OfficialApiStatusRowViewModel(
            check.Provider,
            check.Name,
            availabilityText,
            summary,
            endpointMetaText,
            statusBackground,
            statusForeground,
            () => OpenOfficialApiTraceDialogAsync(traceTitle, traceContent),
            check.RestoreSupported
                ? () => RestoreClientApiDefaultConfigAsync(check)
                : null,
            BuildClientApiStateText(check),
            BuildClientApiAccessDetailText(check),
            check.EndpointLabel,
            check.ConfigSource,
            check.RestoreSupported
                ? check.RestoreHint
                : "\u5F53\u524D\u5BA2\u6237\u7AEF\u6682\u4E0D\u652F\u6301\u81EA\u52A8\u8FD8\u539F\u3002");
    }

    private static string BuildClientApiStateText(ClientApiCheck check)
        => $"\u5B89\u88C5\uFF1A{(check.Installed ? "\u5DF2\u53D1\u73B0" : "\u672A\u53D1\u73B0")} \u00B7 " +
           $"\u914D\u7F6E\uFF1A{(check.ConfigDetected ? "\u5DF2\u8BC6\u522B" : "\u672A\u8BC6\u522B")} \u00B7 " +
           $"API\uFF1A{(check.Reachable ? "\u53EF\u8FBE" : "\u4E0D\u53EF\u8FBE")}";

    private static string BuildClientApiAccessDetailText(ClientApiCheck check)
        => $"{check.AccessPathLabel} \u00B7 {check.ConfigOriginLabel} \u00B7 {check.RoutingNote}";

    private static string BuildClientApiAvailabilityText(ClientApiCheck check)
        => check.AccessPathLabel switch
        {
            "本地代理接管" => "本地代理接管",
            "直连第三方" => "直连第三方",
            "直连官方" => "直连官方",
            _ => "待复核"
        };

    private static (string Background, string Foreground) ResolveClientApiStatusColors(ClientApiCheck check)
        => check.AccessPathLabel switch
        {
            "本地代理接管" => ("#FFF7E8", "#B54708"),
            "直连第三方" => ("#EFF8FF", "#175CD3"),
            "直连官方" => ("#ECFDF3", "#027A48"),
            _ => ("#F2F4F7", "#344054")
        };

    private static string BuildClientApiTraceContent(ClientApiCheck check)
    {
        StringBuilder builder = new();
        builder.AppendLine($"名称：{check.Name}");
        builder.AppendLine($"提供商：{check.Provider}");
        builder.AppendLine($"类型：{check.Kind}");
        builder.AppendLine($"探测地址：{check.ProbeUrl}");
        builder.AppendLine($"探测方法：{check.ProbeMethod}");
        builder.AppendLine($"已安装：{(check.Installed ? "是" : "否")}");
        builder.AppendLine($"已发现配置：{(check.ConfigDetected ? "是" : "否")}");
        builder.AppendLine($"安装线索：{check.InstallEvidence}");
        builder.AppendLine($"配置来源：{check.ConfigSource}");
        builder.AppendLine($"代理来源：{check.ProxySource}");
        builder.AppendLine($"接入方式：{check.AccessPathLabel}");
        builder.AppendLine($"注入方式：{check.ConfigOriginLabel}");
        builder.AppendLine($"当前入口：{check.EndpointLabel}");
        builder.AppendLine($"备注：{check.RoutingNote}");
        builder.AppendLine($"支持还原：{(check.RestoreSupported ? "是" : "否")}");
        builder.AppendLine($"还原说明：{check.RestoreHint}");
        builder.AppendLine($"API 可达：{(check.Reachable ? "是" : "否")}");
        builder.AppendLine($"HTTP 状态：{check.StatusCode?.ToString() ?? "--"}");
        builder.AppendLine($"延迟：{FormatMilliseconds(check.Latency)}");
        builder.AppendLine($"结论：{check.Verdict}");
        builder.AppendLine();
        builder.AppendLine("摘要：");
        builder.AppendLine(check.Summary);
        builder.AppendLine();
        builder.AppendLine("原始证据 / 响应片段：");
        builder.AppendLine(string.IsNullOrWhiteSpace(check.Evidence) ? "无" : check.Evidence);
        builder.AppendLine();
        builder.AppendLine($"错误：{check.Error ?? "无"}");
        return builder.ToString().TrimEnd();
    }

    private Task RestoreClientApiDefaultConfigAsync(ClientApiCheck check)
    {
        var confirmed = MessageBox.Show(
            $"确定要还原 {check.Name} 的默认配置吗？\n\n这会尝试清理代理接管或自定义入口配置，并在修改前自动创建备份文件。",
            "确认还原默认配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmed != MessageBoxResult.Yes)
        {
            StatusMessage = $"已取消还原 {check.Name} 的默认配置。";
            return Task.CompletedTask;
        }

        return ExecuteBusyActionAsync(
            $"正在还原 {check.Name} 的默认配置...",
            async () =>
            {
                var result = await _clientApiConfigRestoreService.RestoreAsync(check.Name);
                StatusMessage = result.Succeeded
                    ? result.Summary
                    : $"还原失败：{result.Error ?? result.Summary}";

                AppendModuleOutput(
                    $"{check.Name} 默认配置还原",
                    result.Summary,
                    result.ChangedFiles.Count == 0
                        ? $"备份：无\n错误：{result.Error ?? "无"}"
                        : $"已处理：{string.Join("\n", result.ChangedFiles)}\n备份：{string.Join("\n", result.BackupFiles)}\n错误：{result.Error ?? "无"}");

                if (result.Succeeded)
                {
                    await RunClientApiDiagnosticsCoreAsync();
                }
            });
    }
}



