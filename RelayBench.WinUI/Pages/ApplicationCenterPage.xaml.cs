using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.Dialogs;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using RelayBench.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.Pages;

public sealed partial class ApplicationCenterPage : PageBase
{
    public ApplicationCenterViewModel ViewModel { get; } = new();
    private bool _proxySubscribed;

    public ApplicationCenterPage()
    {
        InitializeComponent();
        ViewModel.ConfigureClaudeRelayEndpointResolver(
            (settings, probeResult, sourceName) =>
                App.TransparentProxyViewModel.EnsureClaudeRelayEndpointForClientApplyAsync(
                    settings,
                    probeResult,
                    sourceName));
        ViewModel.CodexConfigTemplateDialogOpenRequested += OnCodexConfigTemplateDialogOpenRequested;
        ViewModel.CodexHistoryMergeReviewRequested += OnCodexHistoryMergeReviewRequested;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeProxyContext();
        RefreshTransparentProxyContext();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
        => UnsubscribeProxyContext();

    private void SubscribeProxyContext()
    {
        if (_proxySubscribed)
        {
            return;
        }

        var proxy = App.TransparentProxyViewModel;
        proxy.PropertyChanged += OnProxyPropertyChanged;
        proxy.Routes.CollectionChanged += OnProxyCollectionChanged;
        proxy.RouteQueue.CollectionChanged += OnProxyCollectionChanged;
        proxy.ModelPool.CollectionChanged += OnProxyCollectionChanged;
        proxy.ModelRewriteRules.CollectionChanged += OnProxyCollectionChanged;
        proxy.ProviderAccounts.CollectionChanged += OnProxyCollectionChanged;
        _proxySubscribed = true;
    }

    private void UnsubscribeProxyContext()
    {
        if (!_proxySubscribed)
        {
            return;
        }

        var proxy = App.TransparentProxyViewModel;
        proxy.PropertyChanged -= OnProxyPropertyChanged;
        proxy.Routes.CollectionChanged -= OnProxyCollectionChanged;
        proxy.RouteQueue.CollectionChanged -= OnProxyCollectionChanged;
        proxy.ModelPool.CollectionChanged -= OnProxyCollectionChanged;
        proxy.ModelRewriteRules.CollectionChanged -= OnProxyCollectionChanged;
        proxy.ProviderAccounts.CollectionChanged -= OnProxyCollectionChanged;
        _proxySubscribed = false;
    }

    private void OnProxyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransparentProxyViewModel.ListenAddress)
            or nameof(TransparentProxyViewModel.ActiveConnections)
            or nameof(TransparentProxyViewModel.ProviderAccountSummary))
        {
            RefreshTransparentProxyContext();
        }
    }

    private void OnProxyCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshTransparentProxyContext();

    private void RefreshTransparentProxyContext()
        => ViewModel.TryApplyTransparentProxyEndpoint(App.TransparentProxyViewModel, overwrite: false);

    private void OnUseTransparentProxyClick(object sender, RoutedEventArgs e)
        => ViewModel.TryApplyTransparentProxyEndpoint(App.TransparentProxyViewModel, overwrite: true);

    private async void OnOpenClientApplyTargetDialogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ClientApplyTargetDialog(ViewModel).UseHostTheme(this);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary &&
            ViewModel.ConfirmClientApplyTargetDialogCommand.CanExecute(null))
        {
            await ViewModel.ConfirmClientApplyTargetDialogCommand.ExecuteAsync(null);
        }
        else if (result == ContentDialogResult.Secondary &&
                 ViewModel.RestoreSelectedCommand.CanExecute(null))
        {
            await ViewModel.RestoreSelectedCommand.ExecuteAsync(null);
        }
        else
        {
            ViewModel.CancelClientApplyTargetDialogCommand.Execute(null);
        }
    }

    private async void OnPreviewTargetClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveTarget(sender, out var target))
        {
            await ViewModel.PreviewTargetCommand.ExecuteAsync(target);
        }
    }

    private async void OnApplyTargetClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveTarget(sender, out var target))
        {
            await ViewModel.ApplyTargetCommand.ExecuteAsync(target);
        }
    }

    private async void OnRestoreTargetClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveTarget(sender, out var target))
        {
            await ViewModel.RestoreTargetCommand.ExecuteAsync(target);
        }
    }

    private void OnCopyTargetNameClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveTarget(sender, out var target))
        {
            SetClipboardText(target.Name);
        }
    }

    private void OnCopyTargetConfigPathClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveTarget(sender, out var target))
        {
            SetClipboardText(SanitizeCopiedField(target.ConfigFile));
        }
    }

    private void OnCopyTargetCurrentConfigClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveTarget(sender, out var target))
        {
            SetClipboardText(SanitizeCopiedField(target.CurrentConfig));
        }
    }

    private void OnCopyTargetEndpointClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveTarget(sender, out var target))
        {
            SetClipboardText(SanitizeCopiedField(target.Endpoint));
        }
    }

    private void OnCopyTargetSummaryClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveTarget(sender, out var target))
        {
            SetClipboardText(BuildTargetSummary(target));
        }
    }

    private void OnCopyTargetDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveTarget(sender, out var target))
        {
            SetClipboardText(BuildTargetDiagnosticsSummary(target));
        }
    }

    private async void OnProbeProtocolMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ProbeProtocolCommand.CanExecute(null))
        {
            await ViewModel.ProbeProtocolCommand.ExecuteAsync(null);
        }
    }

    private async void OnRunClientApiDiagnosticsMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RunClientApiDiagnosticsCommand.CanExecute(null))
        {
            await ViewModel.RunClientApiDiagnosticsCommand.ExecuteAsync(null);
        }
    }

    private void OnUseTransparentProxyMenuClick(object sender, RoutedEventArgs e)
        => ViewModel.TryApplyTransparentProxyEndpoint(App.TransparentProxyViewModel, overwrite: true);

    private async void OnExportChatHistoryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ExportClientChatHistoryCommand.CanExecute(null))
        {
            await ViewModel.ExportClientChatHistoryCommand.ExecuteAsync(null);
        }
    }

    private async void OnImportChatHistoryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ImportClientChatHistoryCommand.CanExecute(null))
        {
            await ViewModel.ImportClientChatHistoryCommand.ExecuteAsync(null);
        }
    }

    private async void OnRestoreSelectedMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RestoreSelectedCommand.CanExecute(null))
        {
            await ViewModel.RestoreSelectedCommand.ExecuteAsync(null);
        }
    }

    private async void OnCodexConfigTemplateDialogOpenRequested(object? sender, EventArgs e)
        => await OpenCodexTemplateDialogAsync();

    private async void OnCodexHistoryMergeReviewRequested(object? sender, EventArgs e)
        => await OpenCodexHistoryMergeDialogAsync();

    private async Task OpenCodexTemplateDialogAsync()
    {
        var dialog = new CodexTemplateDialog
        {
            CodexTemplate = ViewModel.CreateCodexTemplateSnapshot(),
            DefaultTemplate = ViewModel.CreateDefaultCodexTemplateSnapshot()
        }.UseHostTheme(this);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.Result is not null)
        {
            ViewModel.SaveCodexConfigTemplateCommand.Execute(dialog.Result);
        }
        else
        {
            ViewModel.CloseCodexConfigTemplateDialogCommand.Execute(null);
        }
    }

    private async Task OpenCodexHistoryMergeDialogAsync()
    {
        var dialog = new CodexHistoryMergeDialog(
            ViewModel.CodexHistoryStatusText,
            ViewModel.CodexHistoryDetailText).UseHostTheme(this);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.MergeCodexHistoryAfterConfirmationAsync(dialog.SelectedTarget);
        }
    }

    private async void OnOpenEndpointHistoryDialogClick(object sender, RoutedEventArgs e)
    {
        var store = new EndpointHistoryStore();
        var items = await store.LoadAsync();
        AddTransparentProxyCandidate(items);

        var dialog = new EndpointHistoryDialog(items, "应用接入").UseHostTheme(this);
        var result = await dialog.ShowAsync();
        if (dialog.ClearRequested)
        {
            await store.ClearAsync();
            ViewModel.StatusText = "已清空接口历史";
            return;
        }

        if (result == ContentDialogResult.Primary && dialog.Result is not null)
        {
            ViewModel.ApplyEndpointHistoryItem(dialog.Result);
        }
    }

    private static void AddTransparentProxyCandidate(List<EndpointHistoryItem> items)
    {
        var proxy = App.TransparentProxyViewModel;
        if (string.IsNullOrWhiteSpace(proxy.LocalEndpoint) ||
            items.Any(item => string.Equals(item.BaseUrl, proxy.LocalEndpoint, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var models = proxy.ModelPool
            .Select(static item => item.Name)
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        items.Insert(0, new EndpointHistoryItem(
            proxy.LocalEndpoint,
            "relaybench-local",
            models.FirstOrDefault() ?? string.Empty,
            DateTime.UtcNow,
            models));
    }

    private async void OnRestoreClientApiClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClientApiStatusRow row)
        {
            return;
        }

        var dialog = new ConfirmationDialog(
            $"还原 {row.Name}",
            $"确定要还原 {row.Name} 的默认配置吗？",
            "执行前会自动创建备份，并可能清理现有代理入口或自定义接口。",
            "还原",
            "取消",
            "配置还原",
            "\uE777").UseHostTheme(this);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.RestoreClientApiDefaultConfigCommand.ExecuteAsync(row);
        }
    }

    private async void OnOpenClientApiDetailClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClientApiStatusRow row)
        {
            return;
        }

        var dialog = new ClientApiStatusDetailDialog(row).UseHostTheme(this);
        await dialog.ShowAsync();
    }

    private static bool TryResolveTarget(object sender, out AppTargetItem target)
    {
        target = null!;
        if (sender is FrameworkElement { Tag: AppTargetItem taggedTarget })
        {
            target = taggedTarget;
            return true;
        }

        if (sender is FrameworkElement { DataContext: AppTargetItem contextTarget })
        {
            target = contextTarget;
            return true;
        }

        return false;
    }

    private static void SetClipboardText(string text)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
    }

    private string BuildTargetSummary(AppTargetItem target)
    {
        StringBuilder builder = new();
        builder.AppendLine($"目标：{target.Name}");
        builder.AppendLine($"目标 ID：{target.TargetId}");
        builder.AppendLine($"已安装：{target.Installed}");
        builder.AppendLine($"可选择：{target.IsSelectable}");
        builder.AppendLine($"协议：{target.Protocol}");
        builder.AppendLine($"协议类型：{target.ProtocolKind}");
        builder.AppendLine($"当前配置：{SanitizeCopiedField(target.CurrentConfig)}");
        builder.AppendLine($"入口：{SanitizeCopiedField(target.Endpoint)}");
        builder.AppendLine($"配置文件：{SanitizeCopiedField(target.ConfigFile)}");
        builder.AppendLine($"禁用原因：{SanitizeCopiedField(target.DisabledReason)}");
        return builder.ToString().TrimEnd();
    }

    private string BuildTargetDiagnosticsSummary(AppTargetItem target)
    {
        StringBuilder builder = new();
        builder.AppendLine(BuildTargetSummary(target));
        builder.AppendLine($"首选传输 API：{SanitizeCopiedField(ViewModel.PreferredProtocol)}");
        builder.AppendLine($"入口接管：{SanitizeCopiedField(ViewModel.EndpointTakeoverText)}");
        builder.AppendLine($"协议覆盖：{SanitizeCopiedField(ViewModel.ProtocolCoverageText)}");
        builder.AppendLine($"上次写入：{SanitizeCopiedField(ViewModel.LastWriteTime)}");
        builder.AppendLine($"已选目标：{ViewModel.SelectedTargetCount}");
        builder.AppendLine($"可用模型：{ViewModel.AvailableModels.Count}");
        return builder.ToString().TrimEnd();
    }

    private static string SanitizeCopiedField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var text = value.Trim();
        text = Regex.Replace(
            text,
            @"(?i)\b(api[_ -]?key|token|authorization)\b(\s*[:=]\s*)([^\s,;]+)",
            "$1$2***",
            RegexOptions.CultureInvariant);
        text = Regex.Replace(
            text,
            @"(?i)\bBearer\s+[A-Za-z0-9._\-]+",
            "Bearer ***",
            RegexOptions.CultureInvariant);
        text = Regex.Replace(
            text,
            @"(?i)\b(sk-[A-Za-z0-9_\-]{6})[A-Za-z0-9_\-]*",
            "$1...",
            RegexOptions.CultureInvariant);
        return text;
    }
}
